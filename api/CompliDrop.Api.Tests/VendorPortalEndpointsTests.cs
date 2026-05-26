using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using CompliDrop.Api.Entities;
using CompliDrop.Api.Tests.TestHelpers;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using static CompliDrop.Api.Tests.TestHelpers.UploadFixtures;

namespace CompliDrop.Api.Tests;

/// <summary>
/// Integration tests for the PUBLIC, unauthenticated vendor portal endpoints
/// (<see cref="CompliDrop.Api.Endpoints.VendorPortalEndpoints"/>): token info, upload, and status.
/// This is the highest-risk attack surface (project rule: "treat inputs as untrusted"), so the
/// tests assert token validation + tenant-leak avoidance, rate limits (token 10/hr, ip 30/hr),
/// the <c>MaxUploads</c> quota with auto-deactivation, magic-byte file validation, and org scoping.
///
/// The shared fixture boots the host with <c>RateLimiting:Enabled=false</c>, so the rate-limit
/// tests spin up a one-off host with the limiter enabled (same pattern as ResendWebhookTests).
/// Each gets a fresh host → fresh partition state, so limiter counters never leak across tests.
/// </summary>
public sealed class VendorPortalEndpointsTests(IntegrationTestFixture fixture) : IntegrationTestBase(fixture)
{
    private static Task<HttpResponseMessage> UploadAsync(
        HttpClient client, string token, byte[] bytes, string fileName, string contentType) =>
        client.PostAsync($"/api/portal/{token}/upload", UploadForm(bytes, fileName, contentType));

    private static async Task<string?> ErrorCode(HttpResponseMessage resp) =>
        (await resp.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("error").GetProperty("code").GetString();

    private static async Task<JsonElement> Data(HttpResponseMessage resp) =>
        (await resp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("data");

    private sealed record SeededLink(Guid OrgId, Guid VendorId, Guid LinkId, string Token, string OrgName, string VendorName);

    /// <summary>Seeds an org + vendor + portal link directly via the system context (no tenant filter).</summary>
    private async Task<SeededLink> SeedLinkAsync(
        bool isActive = true,
        DateTime? expiresAt = null,
        int maxUploads = 20,
        int uploadCount = 0)
    {
        var orgId = Guid.NewGuid();
        var vendorId = Guid.NewGuid();
        var linkId = Guid.NewGuid();
        var token = $"tok-{Guid.NewGuid():N}";
        var orgName = $"SecretOrg-{orgId:N}";
        var vendorName = $"SecretVendor-{vendorId:N}";
        var now = DateTime.UtcNow;

        await using var db = CreateSystemDb();
        db.Organizations.Add(new Organization { Id = orgId, Name = orgName, CreatedAt = now, UpdatedAt = now });
        db.Vendors.Add(new Vendor { Id = vendorId, OrganizationId = orgId, Name = vendorName, CreatedAt = now, UpdatedAt = now });
        db.VendorPortalLinks.Add(new VendorPortalLink
        {
            Id = linkId,
            VendorId = vendorId,
            Token = token,
            IsActive = isActive,
            ExpiresAt = expiresAt,
            MaxUploads = maxUploads,
            UploadCount = uploadCount,
            CreatedAt = now,
        });
        await db.SaveChangesAsync();

        return new SeededLink(orgId, vendorId, linkId, token, orgName, vendorName);
    }

    /// <summary>A one-off host with the rate limiter ENABLED (the shared fixture disables it).</summary>
    private CustomWebApplicationFactory RateLimitedFactory() =>
        new(Fixture.ConnectionString, new Dictionary<string, string?> { ["RateLimiting:Enabled"] = "true" });

    // ============================================================================================
    // AC1 — token validation + no tenant-data leak
    // ============================================================================================

    [Fact]
    public async Task Valid_token_returns_portal_info()
    {
        var seeded = await SeedLinkAsync(maxUploads: 20);
        var client = CreateClient();

        var resp = await client.GetAsync($"/api/portal/{seeded.Token}");

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var data = await Data(resp);
        data.GetProperty("vendorName").GetString().Should().Be(seeded.VendorName);
        data.GetProperty("orgName").GetString().Should().Be(seeded.OrgName);
        data.GetProperty("isActive").GetBoolean().Should().BeTrue();
        data.GetProperty("maxUploads").GetInt32().Should().Be(20);
        data.GetProperty("uploadCount").GetInt32().Should().Be(0);
    }

    [Fact]
    public async Task Valid_token_with_future_expiry_returns_portal_info()
    {
        // Pins the third arm of `link.ExpiresAt is DateTime exp && exp < UtcNow` — flipping the
        // comparison to `>` would 410 a valid future-dated link.
        var seeded = await SeedLinkAsync(expiresAt: DateTime.UtcNow.AddDays(7));
        var client = CreateClient();

        var resp = await client.GetAsync($"/api/portal/{seeded.Token}");

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Unknown_token_is_404_and_leaks_no_tenant_data()
    {
        // A real link exists, but we query a completely different (unknown) token.
        var seeded = await SeedLinkAsync();
        var client = CreateClient();

        var resp = await client.GetAsync($"/api/portal/does-not-exist-{Guid.NewGuid():N}");

        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await ErrorCode(resp)).Should().Be("vendor.portal_token_invalid");
        var raw = await resp.Content.ReadAsStringAsync();
        raw.Should().NotContain(seeded.OrgName).And.NotContain(seeded.VendorName);
    }

    [Fact]
    public async Task Deactivated_link_is_404_and_leaks_no_tenant_data()
    {
        var seeded = await SeedLinkAsync(isActive: false);
        var client = CreateClient();

        var resp = await client.GetAsync($"/api/portal/{seeded.Token}");

        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await ErrorCode(resp)).Should().Be("vendor.portal_token_invalid");
        var raw = await resp.Content.ReadAsStringAsync();
        raw.Should().NotContain(seeded.OrgName).And.NotContain(seeded.VendorName);
    }

    [Fact]
    public async Task Expired_link_is_410_and_leaks_no_tenant_data()
    {
        var seeded = await SeedLinkAsync(expiresAt: DateTime.UtcNow.AddDays(-1));
        var client = CreateClient();

        var resp = await client.GetAsync($"/api/portal/{seeded.Token}");

        resp.StatusCode.Should().Be(HttpStatusCode.Gone);
        (await ErrorCode(resp)).Should().Be("vendor.portal_token_expired");
        var raw = await resp.Content.ReadAsStringAsync();
        raw.Should().NotContain(seeded.OrgName).And.NotContain(seeded.VendorName);
    }

    // ============================================================================================
    // AC4 — magic-byte validation + multipart-form handling
    // ============================================================================================

    [Fact]
    public async Task Upload_with_spoofed_content_type_is_rejected_on_bytes()
    {
        var seeded = await SeedLinkAsync();
        var client = CreateClient();

        // Plain-text bytes, a .pdf name, and an application/pdf Content-Type — the lie doesn't help.
        var resp = await UploadAsync(client, seeded.Token, TextBytes(), "evil.pdf", "application/pdf");

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ErrorCode(resp)).Should().Be("document.unsupported_format");

        await using var db = CreateSystemDb();
        (await db.Documents.IgnoreQueryFilters().CountAsync(d => d.VendorId == seeded.VendorId))
            .Should().Be(0); // nothing persisted for a rejected file
    }

    [Fact]
    public async Task Upload_with_unsupported_magic_bytes_is_rejected()
    {
        // GIF magic bytes carried under a .jpg name + image/jpeg — not in the supported set
        // (PDF / JPEG / PNG). Validator should reject on bytes, not on filename or Content-Type.
        var seeded = await SeedLinkAsync();
        var client = CreateClient();

        var resp = await UploadAsync(client, seeded.Token, FileWith(0x47, 0x49, 0x46, 0x38, 0x39, 0x61), "x.jpg", "image/jpeg");

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ErrorCode(resp)).Should().Be("document.unsupported_format");
    }

    [Fact]
    public async Task Upload_with_no_file_field_returns_400()
    {
        var seeded = await SeedLinkAsync();
        var client = CreateClient();

        // A valid multipart body that carries other fields but omits the "file" field — mirrors a
        // real client that filled in metadata and forgot to attach the document. (An entirely
        // empty MultipartFormDataContent would emit just a closing boundary, which the form parser
        // treats as malformed and 500s — not the contract we want to assert here.)
        var form = new MultipartFormDataContent { { new StringContent("other"), "documentType" } };
        var resp = await client.PostAsync($"/api/portal/{seeded.Token}/upload", form);

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ErrorCode(resp)).Should().Be("validation.file");
    }

    [Fact]
    public async Task Upload_with_zero_byte_file_returns_400()
    {
        var seeded = await SeedLinkAsync();
        var client = CreateClient();

        var resp = await UploadAsync(client, seeded.Token, [], "empty.pdf", "application/pdf");

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ErrorCode(resp)).Should().Be("validation.file");
    }

    [Fact]
    public async Task Upload_with_non_multipart_body_returns_400()
    {
        var seeded = await SeedLinkAsync();
        var client = CreateClient();

        var resp = await client.PostAsync(
            $"/api/portal/{seeded.Token}/upload",
            new StringContent("not a form", Encoding.UTF8, "text/plain"));

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ErrorCode(resp)).Should().Be("validation.form");
    }

    // ============================================================================================
    // AC5 — valid upload is scoped to the link's org; status is link-scoped
    // ============================================================================================

    [Fact]
    public async Task Valid_upload_succeeds_and_is_scoped_to_the_links_org()
    {
        var a = await SeedLinkAsync();
        var b = await SeedLinkAsync(); // a different org/vendor
        var client = CreateClient();

        var resp = await UploadAsync(client, a.Token, PdfBytes(), "coi.pdf", "application/pdf");

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var uploadId = (await Data(resp)).GetProperty("uploadId").GetGuid();

        await using var db = CreateSystemDb();
        var doc = await db.Documents.IgnoreQueryFilters().SingleAsync(d => d.Id == uploadId);
        doc.OrganizationId.Should().Be(a.OrgId);
        doc.VendorId.Should().Be(a.VendorId);
        doc.UploadedBy.Should().Be("vendor_portal");
        doc.ContentType.Should().Be("application/pdf");
        // The other org/vendor must not have received the document.
        doc.OrganizationId.Should().NotBe(b.OrgId);
        (await db.Documents.IgnoreQueryFilters().CountAsync(d => d.VendorId == b.VendorId)).Should().Be(0);

        // Quota counter advanced (independent of the AC3 deactivation tests, which only assert
        // the boundary). A regression that forgot `link.UploadCount += 1;` would slip past the
        // doc assertions above but fail here.
        var link = await db.VendorPortalLinks.SingleAsync(l => l.Id == a.LinkId);
        link.UploadCount.Should().Be(1);
        link.IsActive.Should().BeTrue();
    }

    [Fact]
    public async Task Status_is_scoped_to_the_uploads_link_and_not_exposed_via_another_link()
    {
        var a = await SeedLinkAsync();
        var b = await SeedLinkAsync(); // a different org/vendor
        var client = CreateClient();

        var uploadId = (await Data(await UploadAsync(client, a.Token, PdfBytes(), "coi.pdf", "application/pdf")))
            .GetProperty("uploadId").GetGuid();

        // The owning link sees its own upload...
        var ownResp = await client.GetAsync($"/api/portal/{a.Token}/status/{uploadId}");
        ownResp.StatusCode.Should().Be(HttpStatusCode.OK);
        (await Data(ownResp)).GetProperty("extractionStatus").GetString().Should().Be("Pending");

        // ...but another org's link cannot read it.
        var crossResp = await client.GetAsync($"/api/portal/{b.Token}/status/{uploadId}");
        crossResp.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await ErrorCode(crossResp)).Should().Be("document.not_found");
    }

    [Fact]
    public async Task Status_with_unknown_token_returns_404()
    {
        var client = CreateClient();

        var resp = await client.GetAsync($"/api/portal/does-not-exist-{Guid.NewGuid():N}/status/{Guid.NewGuid()}");

        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await ErrorCode(resp)).Should().Be("vendor.portal_token_invalid");
    }

    [Fact]
    public async Task Status_with_known_token_but_unknown_uploadId_returns_404()
    {
        var seeded = await SeedLinkAsync();
        var client = CreateClient();

        var resp = await client.GetAsync($"/api/portal/{seeded.Token}/status/{Guid.NewGuid()}");

        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await ErrorCode(resp)).Should().Be("document.not_found");
    }

    [Theory]
    [InlineData("deactivated")]
    [InlineData("expired")]
    public async Task Status_remains_queryable_after_the_link_is_no_longer_usable(string scenario)
    {
        // The status endpoint intentionally does NOT short-circuit on `IsActive`/`ExpiresAt` —
        // vendors can poll their past uploads even after the link is revoked or has expired.
        // This test pins BOTH arms of that asymmetry against info/upload (which check the link
        // state) so a future "tighten the status check" change has to think about it explicitly.
        var seeded = await SeedLinkAsync();
        var client = CreateClient();

        var uploadId = (await Data(await UploadAsync(client, seeded.Token, PdfBytes(), "1.pdf", "application/pdf")))
            .GetProperty("uploadId").GetGuid();

        // After the upload, make the link unusable in the scenario's way.
        await using (var db = CreateSystemDb())
        {
            var link = await db.VendorPortalLinks.SingleAsync(l => l.Id == seeded.LinkId);
            switch (scenario)
            {
                case "deactivated": link.IsActive = false; break;
                case "expired": link.ExpiresAt = DateTime.UtcNow.AddDays(-1); break;
                default: throw new InvalidOperationException($"unknown scenario '{scenario}'");
            }
            await db.SaveChangesAsync();
        }

        // Sanity: info/upload now refuse the link, proving the scenario was applied.
        (await client.GetAsync($"/api/portal/{seeded.Token}")).StatusCode
            .Should().BeOneOf(HttpStatusCode.NotFound, HttpStatusCode.Gone);

        // Status STILL returns the upload (the intentional asymmetry under test).
        var resp = await client.GetAsync($"/api/portal/{seeded.Token}/status/{uploadId}");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        (await Data(resp)).GetProperty("extractionStatus").GetString().Should().Be("Pending");
    }

    // ============================================================================================
    // AC3 — MaxUploads quota + auto-deactivation
    // ============================================================================================

    [Fact]
    public async Task Reaching_MaxUploads_deactivates_link_and_refuses_further_uploads()
    {
        var seeded = await SeedLinkAsync(maxUploads: 2);
        var client = CreateClient();

        (await UploadAsync(client, seeded.Token, PdfBytes(), "1.pdf", "application/pdf"))
            .StatusCode.Should().Be(HttpStatusCode.OK);
        (await UploadAsync(client, seeded.Token, PdfBytes(), "2.pdf", "application/pdf"))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        // The second upload reached the cap → the link auto-deactivates.
        await using (var db = CreateSystemDb())
        {
            var link = await db.VendorPortalLinks.SingleAsync(l => l.Id == seeded.LinkId);
            link.UploadCount.Should().Be(2);
            link.IsActive.Should().BeFalse();
        }

        // A third upload is refused because the link is now inactive.
        var third = await UploadAsync(client, seeded.Token, PdfBytes(), "3.pdf", "application/pdf");
        third.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await ErrorCode(third)).Should().Be("vendor.portal_token_invalid");

        await using var db2 = CreateSystemDb();
        (await db2.Documents.IgnoreQueryFilters().CountAsync(d => d.VendorId == seeded.VendorId)).Should().Be(2);
    }

    [Fact]
    public async Task Concurrent_uploads_at_the_cap_boundary_only_let_one_succeed()
    {
        // Pre-fix: two requests both read UploadCount=0 with cap=1, both pass the check-then-act
        // quota check, both upload to blob, both insert a Document — the cap was exceeded by 1.
        // Post-fix: the atomic UPDATE-with-WHERE reservation in UploadViaPortal serializes the
        // two requests at the row-level lock and only one increments past the boundary.
        var seeded = await SeedLinkAsync(maxUploads: 1, uploadCount: 0);
        var client = CreateClient();

        var t1 = UploadAsync(client, seeded.Token, PdfBytes(), "a.pdf", "application/pdf");
        var t2 = UploadAsync(client, seeded.Token, PdfBytes(), "b.pdf", "application/pdf");
        var responses = await Task.WhenAll(t1, t2);

        // Exactly one wins. The loser is refused — the *which* refusal depends on timing:
        //   - 429 vendor.portal_quota_exceeded if the loser passed the initial-read check (link
        //     still active) and only lost the atomic UPDATE-WHERE reservation.
        //   - 404 vendor.portal_token_invalid if the loser read *after* the winner committed,
        //     saw IsActive=false from the deactivation, and short-circuited at the initial check.
        // Either is a correct refusal; the safety invariant is that the cap is not exceeded.
        responses.Count(r => r.StatusCode == HttpStatusCode.OK).Should().Be(1);
        var refused = responses.Single(r => r.StatusCode != HttpStatusCode.OK);
        refused.StatusCode.Should().BeOneOf(HttpStatusCode.TooManyRequests, HttpStatusCode.NotFound);
        (await ErrorCode(refused)).Should().BeOneOf("vendor.portal_quota_exceeded", "vendor.portal_token_invalid");

        await using var db = CreateSystemDb();
        var link = await db.VendorPortalLinks.SingleAsync(l => l.Id == seeded.LinkId);
        link.UploadCount.Should().Be(1, "the cap of 1 must never be exceeded, even under concurrency");
        link.IsActive.Should().BeFalse();
        (await db.Documents.IgnoreQueryFilters().CountAsync(d => d.VendorId == seeded.VendorId))
            .Should().Be(1, "the losing request must not persist a Document");
    }

    [Fact]
    public async Task Upload_to_an_active_link_already_at_quota_returns_429_and_deactivates()
    {
        // An active link whose count already equals MaxUploads (e.g. cap lowered after issuance).
        var seeded = await SeedLinkAsync(maxUploads: 2, uploadCount: 2);
        var client = CreateClient();

        var resp = await UploadAsync(client, seeded.Token, PdfBytes(), "x.pdf", "application/pdf");

        resp.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
        (await ErrorCode(resp)).Should().Be("vendor.portal_quota_exceeded");

        await using var db = CreateSystemDb();
        var link = await db.VendorPortalLinks.SingleAsync(l => l.Id == seeded.LinkId);
        link.IsActive.Should().BeFalse();
        (await db.Documents.IgnoreQueryFilters().CountAsync(d => d.VendorId == seeded.VendorId)).Should().Be(0);
    }

    // ============================================================================================
    // AC2 — rate limits (require the limiter, which the shared fixture disables)
    //
    // Each test below spins up its own RateLimitedFactory so partition counters start fresh and
    // don't leak across tests. The fixed window is 1 hour — well beyond the loop wall-clock —
    // so window rollover is not a flake source today. If Window is ever shortened, these tests
    // must move to a TimeProvider-driven test-clock.
    // ============================================================================================

    [Fact]
    public async Task Exceeding_the_portal_token_rate_limit_returns_429()
    {
        // MaxUploads is high so the quota never fires inside the window — we isolate the 10/hr
        // per-token limiter. 10 uploads to the same token are allowed; the 11th is throttled.
        var seeded = await SeedLinkAsync(maxUploads: 100);
        await using var factory = RateLimitedFactory();
        var client = factory.CreateClient();

        for (var i = 0; i < 10; i++)
        {
            var ok = await UploadAsync(client, seeded.Token, PdfBytes(), $"{i}.pdf", "application/pdf");
            ok.StatusCode.Should().Be(HttpStatusCode.OK, "the first 10 uploads are within the 10/hr token limit");
        }

        var throttled = await UploadAsync(client, seeded.Token, PdfBytes(), "11.pdf", "application/pdf");
        throttled.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
        // #45: the rate-limit rejection must surface as a distinct
        // envelope code from the quota-exceeded 429 below — clients
        // must be able to tell "retry next hour" (rate limit, this
        // path) from "never retry, link permanently exhausted" (quota,
        // vendor.portal_quota_exceeded).
        (await ErrorCode(throttled)).Should().Be("rate_limit.exceeded");

        // The 11th request was rejected by the limiter middleware *before* the handler ran, so the
        // upload count is still 10 (no 11th document was created).
        await using var db = CreateSystemDb();
        (await db.VendorPortalLinks.SingleAsync(l => l.Id == seeded.LinkId)).UploadCount.Should().Be(10);
    }

    [Fact]
    public async Task Exceeding_the_portal_ip_rate_limit_returns_429()
    {
        // The ip limiter (30/hr) partitions on `Connection.RemoteIpAddress`, falling back to the
        // literal string "unknown" when null — which it is under TestServer. All 31 requests
        // therefore share one ip partition. We use 31 DISTINCT tokens so the per-token limiter
        // (10/hr) never trips — the only thing that can throttle the 31st request is the shared
        // ip partition. Invalid tokens 404 at the handler but still consume an ip permit (the
        // limiter runs before the handler).
        //
        // If a future host change populates Connection.RemoteIpAddress in tests (e.g. wiring
        // UseForwardedHeaders against a test header), this test must be updated to pin the
        // partition key explicitly rather than relying on the null fallback.
        await using var factory = RateLimitedFactory();
        var client = factory.CreateClient();

        for (var i = 0; i < 30; i++)
        {
            var resp = await client.PostAsync($"/api/portal/ip-{i}-{Guid.NewGuid():N}/upload", null);
            resp.StatusCode.Should().Be(HttpStatusCode.NotFound, "the first 30 requests are within the 30/hr ip limit");
        }

        var throttled = await client.PostAsync($"/api/portal/ip-31-{Guid.NewGuid():N}/upload", null);
        throttled.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
        // #45: same rate-limit envelope from the ip partition as from
        // the token partition — both reset hourly and look identical
        // to the client.
        (await ErrorCode(throttled)).Should().Be("rate_limit.exceeded");
    }

    [Fact]
    public async Task Portal_upload_with_mixed_case_path_is_still_rate_limited()
    {
        // ASP.NET routing matches MapPost("/{token}/upload") case-insensitively, so the upload
        // handler is reachable via /Upload, /UPLOAD, etc. The IsPortalUpload gate in Program.cs
        // therefore must also be case-insensitive — an ordinal compare on "/upload" would let an
        // attacker bypass BOTH portal rate limits by varying the path case while still hitting
        // the upload code path. This test pins the gate's case-insensitivity against regression.
        var seeded = await SeedLinkAsync(maxUploads: 100);
        await using var factory = RateLimitedFactory();
        var client = factory.CreateClient();
        var url = $"/api/portal/{seeded.Token}/Upload"; // capital U

        for (var i = 0; i < 10; i++)
        {
            var ok = await client.PostAsync(url, UploadForm(PdfBytes(), $"{i}.pdf", "application/pdf"));
            ok.StatusCode.Should().Be(HttpStatusCode.OK);
        }

        var throttled = await client.PostAsync(url, UploadForm(PdfBytes(), "11.pdf", "application/pdf"));
        throttled.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
    }

    [Fact]
    public async Task Portal_GET_endpoints_are_not_rate_limited()
    {
        // The IsPortalUpload gate intentionally excludes GETs (info + status). If a regression
        // broadened the gate (e.g. "lock everything down on the portal" rewriting it to match any
        // verb on the prefix), a vendor refreshing the page or polling status would burn the
        // 10/hr token budget. We interleave info + status GETs at 35 iterations each (70 total) —
        // past BOTH portal limits (10/hr token, 30/hr ip) — and assert none get a 429.
        var seeded = await SeedLinkAsync(maxUploads: 50);
        await using var factory = RateLimitedFactory();
        var client = factory.CreateClient();

        for (var i = 0; i < 35; i++)
        {
            var info = await client.GetAsync($"/api/portal/{seeded.Token}");
            info.StatusCode.Should().Be(HttpStatusCode.OK,
                $"GET /api/portal/{{token}} iteration {i + 1}/35 should not be rate-limited");

            // Status against a random uploadId 404s at the handler — we only care it isn't 429.
            var status = await client.GetAsync($"/api/portal/{seeded.Token}/status/{Guid.NewGuid()}");
            status.StatusCode.Should().Be(HttpStatusCode.NotFound,
                $"GET /api/portal/{{token}}/status iteration {i + 1}/35 should not be rate-limited");
        }
    }
}
