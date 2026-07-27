using System.Net;
using System.Net.Http.Json;
using CompliDrop.Api.Entities;
using CompliDrop.Api.Services;
using CompliDrop.Api.Tests.TestHelpers;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using static CompliDrop.Api.Tests.TestHelpers.UploadFixtures;

namespace CompliDrop.Api.Tests;

/// <summary>
/// HTTP-level tests for the audit-row client-input boundary (#372).
///
/// The audit row is added to the SAME <c>SaveChanges</c> as the business mutation, and Npgsql does
/// not truncate — so before the boundary clamp an over-length inbound <c>User-Agent</c> or
/// <c>X-Trace-Id</c> failed the WHOLE unit of work with Postgres 22001 -> unhandled
/// <c>DbUpdateException</c> -> 500. These tests pin the two halves of that: the mutation still
/// succeeds, and (the audit-suppression half) the row it should have written is actually there.
///
/// Every assertion here runs through the real host, so it covers the interceptor writer (the
/// <c>/api/vendors</c> cases) AND the explicit <c>IAuditLogger</c> writer (the failed login, and the
/// PUBLIC <c>/api/portal/{token}/upload</c> whose <c>audit.LogAsync</c> sits inside the permit
/// reservation's transaction) — all three read the same clamped <c>ICurrentUser</c>.
/// </summary>
public sealed class AuditClientInputClampIntegrationTests(IntegrationTestFixture fixture)
    : IntegrationTestBase(fixture)
{
    private const string TraceHeader = "X-Trace-Id";

    // Comfortably past varchar(500) — a corporate proxy appending its own product tokens gets a
    // legitimately long UA, and a hostile client can send any length it likes.
    private static readonly string OversizeUserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) CorporateProxy/" + new string('x', 900);

    private async Task<HttpResponseMessage> CreateVendorAsync(HttpClient client, string name) =>
        await client.PostAsJsonAsync("/api/vendors", new
        {
            name,
            contactEmail = (string?)null,
            contactPhone = (string?)null,
            category = (string?)null,
            complianceTemplateId = (Guid?)null,
        });

    /// <summary>Mirrors <c>VendorPortalEndpointsTests.UploadAsync</c> — the PUBLIC upload route.</summary>
    private static Task<HttpResponseMessage> UploadAsync(
        HttpClient client, string token, byte[] bytes, string fileName, string contentType) =>
        client.PostAsync($"/api/portal/{token}/upload", UploadForm(bytes, fileName, contentType));

    private async Task<List<AuditLog>> AuditRowsAsync(Guid orgId)
    {
        await using var db = CreateSystemDb();
        return await db.AuditLogs.Where(a => a.OrganizationId == orgId).ToListAsync();
    }

    [Fact]
    public async Task An_oversize_user_agent_no_longer_500s_an_audited_mutation()
    {
        var auth = await RegisterAndLoginAsync();
        auth.Client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", OversizeUserAgent);

        var resp = await CreateVendorAsync(auth.Client, "Longua Catering");

        // Pre-#372 this was 500 (Postgres 22001 from AuditLog.UserAgent varchar(500)) and no
        // vendor was created — the audit insert took the business mutation down with it.
        resp.StatusCode.Should().Be(HttpStatusCode.OK);

        await using var db = CreateSystemDb();
        (await db.Vendors.CountAsync(v => v.OrganizationId == auth.OrgId && v.Name == "Longua Catering"))
            .Should().Be(1);
    }

    [Fact]
    public async Task The_persisted_audit_row_holds_the_truncated_user_agent()
    {
        var auth = await RegisterAndLoginAsync();
        auth.Client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", OversizeUserAgent);

        (await CreateVendorAsync(auth.Client, "Truncated Co")).EnsureSuccessStatusCode();

        var rows = (await AuditRowsAsync(auth.OrgId))
            .Where(a => a.EntityType == nameof(Vendor))
            .ToList();

        rows.Should().NotBeEmpty("the vendor mutation must still be audited");
        rows.Should().OnlyContain(a => a.UserAgent!.Length == AuditColumnLengths.UserAgent);
        // Truncated, not dropped: the head still names the browser/proxy.
        rows.Should().OnlyContain(a => a.UserAgent!.StartsWith("Mozilla/5.0 (Windows NT 10.0; Win64; x64)"));
    }

    [Fact]
    public async Task An_oversize_trace_id_is_replaced_and_the_echoed_header_matches_the_stored_column()
    {
        var auth = await RegisterAndLoginAsync();
        var oversize = new string('t', 200);
        auth.Client.DefaultRequestHeaders.TryAddWithoutValidation(TraceHeader, oversize);

        var resp = await CreateVendorAsync(auth.Client, "Tracer Co");

        resp.StatusCode.Should().Be(HttpStatusCode.OK);

        var echoed = resp.Headers.GetValues(TraceHeader).Single();
        echoed.Should().NotBe(oversize);
        echoed.Length.Should().BeLessThanOrEqualTo(AuditColumnLengths.CorrelationId);

        // The contract that makes a correlation id worth having: what we hand the client for a bug
        // report is exactly what we stored, so pasting the header actually finds the rows.
        var rows = (await AuditRowsAsync(auth.OrgId))
            .Where(a => a.EntityType == nameof(Vendor))
            .ToList();
        rows.Should().NotBeEmpty();
        rows.Should().OnlyContain(a => a.CorrelationId == echoed);
    }

    [Fact]
    public async Task A_pii_shaped_inbound_trace_id_never_reaches_the_echoed_header_or_the_stored_column()
    {
        // The CHARSET half of IsUsableTraceId, driven through the REAL middleware rather than only
        // through Resolve. Every other HTTP-level trace-id case here sends an OVER-LENGTH value, so
        // they pin only the LENGTH half: a plausible "normalize only when it doesn't fit"
        // refactor keeps all of them green while silently re-opening the PII door. This id is 18
        // characters — it fits varchar(64) comfortably — and the charset is the ONLY thing that
        // stops it, which is exactly why ADR 0044 §3 calls the charset the load-bearing rule.
        //
        // What it protects: an ACCEPTED id is echoed in the X-Trace-Id response header, becomes
        // ApiError.correlationId in the frontend and is shipped to Sentry as the `correlation_id`
        // tag, which ADR 0037 deliberately applies AFTER scrubEvent and does NOT redact.
        var auth = await RegisterAndLoginAsync();
        const string pii = "pat@gardenhall.com";
        auth.Client.DefaultRequestHeaders.TryAddWithoutValidation(TraceHeader, pii);

        var resp = await CreateVendorAsync(auth.Client, "Emailtrace Co");

        resp.StatusCode.Should().Be(HttpStatusCode.OK, "a junk trace id is replaced, never a 500");

        var echoed = resp.Headers.GetValues(TraceHeader).Single();
        echoed.Should().NotBe(pii);
        echoed.Should().NotContain("@", "an email address must not survive into the Sentry tag");

        var rows = (await AuditRowsAsync(auth.OrgId))
            .Where(a => a.EntityType == nameof(Vendor))
            .ToList();
        rows.Should().NotBeEmpty("the vendor mutation must still be audited");
        rows.Should().OnlyContain(a => a.CorrelationId != pii);
        rows.Should().OnlyContain(a => !a.CorrelationId!.Contains('@'));
        // And the replacement kept the four-way agreement: echoed header == stored column.
        rows.Should().OnlyContain(a => a.CorrelationId == echoed);
    }

    [Fact]
    public async Task A_well_formed_inbound_trace_id_is_still_honored_end_to_end()
    {
        // The clamp must not cost the feature: a caller supplying its own tracing id keeps it.
        var auth = await RegisterAndLoginAsync();
        var supplied = "4bf92f3577b34da6a3ce929d0e0e4736";
        auth.Client.DefaultRequestHeaders.TryAddWithoutValidation(TraceHeader, supplied);

        var resp = await CreateVendorAsync(auth.Client, "Honored Co");
        resp.EnsureSuccessStatusCode();

        resp.Headers.GetValues(TraceHeader).Single().Should().Be(supplied);
        (await AuditRowsAsync(auth.OrgId))
            .Where(a => a.EntityType == nameof(Vendor))
            .Should().OnlyContain(a => a.CorrelationId == supplied);
    }

    [Fact]
    public async Task The_public_portal_upload_survives_an_oversize_user_agent_intact()
    {
        // The ticket's headline scenario, and the worst instance of it: /api/portal/{token}/upload
        // is PUBLIC and unauthenticated, so the header is controlled by a THIRD PARTY (the vendor,
        // or anyone holding the link). It is also the one audited path where the 22001 landed
        // inside an explicit transaction that had already burned a PAID permit and uploaded a blob
        // — `audit.LogAsync("vendorPortalLink.upload_processed", ...)` sits between the
        // ExecuteUpdateAsync reservation and the CommitAsync — so the failure cost the customer a
        // quota slot AND left the vendor staring at a 500 with no way to retry successfully.
        var seeded = await SeedLinkAsync(maxUploads: 20);
        var client = CreateClient();
        client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", OversizeUserAgent);

        var resp = await UploadAsync(client, seeded.Token, PdfBytes(), "coi.pdf", "application/pdf");

        resp.StatusCode.Should().Be(HttpStatusCode.OK, "a long User-Agent is not the vendor's fault");

        await using var db = CreateSystemDb();

        // The document landed exactly once.
        (await db.Documents.CountAsync(d => d.OrganizationId == seeded.OrgId)).Should().Be(1);

        // The audit row that the transaction used to die on is present, and truncated rather than
        // dropped — the forensic head still names the browser/proxy.
        var uploads = (await AuditRowsAsync(seeded.OrgId))
            .Where(a => a.Action == "vendorPortalLink.upload_processed")
            .ToList();
        uploads.Should().HaveCount(1);
        uploads[0].UserAgent.Should().HaveLength(AuditColumnLengths.UserAgent);
        uploads[0].UserAgent.Should().StartWith("Mozilla/5.0 (Windows NT 10.0; Win64; x64)");

        // And the paid permit stayed spent-once: not rolled back by a failed commit, not double-burned.
        (await db.VendorPortalLinks.SingleAsync(l => l.Id == seeded.LinkId)).UploadCount.Should().Be(1);
    }

    [Fact]
    public async Task A_failed_login_still_writes_its_audit_row_under_an_oversize_user_agent()
    {
        // The audit-SUPPRESSION half of the bug. The lockout increment commits in its own earlier
        // SaveChanges, so pre-#372 the follow-up `user.login_failed` insert threw 22001 and the
        // attempt vanished from the audit trail while still counting against the account — an
        // attacker could erase their own failed-login evidence by sending a long User-Agent.
        var auth = await RegisterAndLoginAsync();

        var attacker = CreateClient();
        attacker.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", OversizeUserAgent);
        var resp = await attacker.PostAsJsonAsync("/api/auth/login", new
        {
            email = auth.Email,
            password = "WrongPassword999",
        });

        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized, "a wrong password is a 401, never a 500");

        var failures = (await AuditRowsAsync(auth.OrgId))
            .Where(a => a.Action == "user.login_failed")
            .ToList();
        failures.Should().HaveCount(1);
        failures[0].UserAgent.Should().HaveLength(AuditColumnLengths.UserAgent);

        // And the lockout counter and the audit row agree about what happened.
        await using var db = CreateSystemDb();
        (await db.Users.SingleAsync(u => u.Id == auth.UserId)).FailedLoginAttempts.Should().Be(1);
    }
}
