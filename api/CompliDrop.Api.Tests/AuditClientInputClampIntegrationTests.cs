using System.Net;
using System.Net.Http.Json;
using CompliDrop.Api.Entities;
using CompliDrop.Api.Services;
using CompliDrop.Api.Tests.TestHelpers;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;

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
/// Every assertion here runs through the real host, so it covers the interceptor writer AND the
/// explicit <c>IAuditLogger</c> writer at once — both read the same clamped <c>ICurrentUser</c>.
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
