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

    private static async Task<HttpResponseMessage> UploadWithKeyAsync(HttpClient client, string token, string idempotencyKey)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, $"/api/portal/{token}/upload")
        {
            Content = UploadForm(PdfBytes(), "coi.pdf", "application/pdf")
        };
        req.Headers.Add("Idempotency-Key", idempotencyKey);
        return await client.SendAsync(req);
    }

    private static async Task<Guid> UploadIdOf(HttpResponseMessage resp) =>
        (await Data(resp)).GetProperty("uploadId").GetGuid();

    private static async Task<string?> ErrorCode(HttpResponseMessage resp) =>
        (await resp.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("error").GetProperty("code").GetString();

    private static async Task<JsonElement> Data(HttpResponseMessage resp) =>
        (await resp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("data");

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
    public async Task Soft_deleted_vendor_with_a_stale_active_link_returns_404_not_500()
    {
        // #269 defense in depth: pre-fix deletions (or any path that misses the
        // deactivate-on-delete write) leave the link active while the vendor Include
        // materializes null. Both portal endpoints must die at the null-guard, not NRE.
        var seeded = await SeedLinkAsync();
        await using (var db = CreateSystemDb())
            await db.Vendors.Where(v => v.Id == seeded.VendorId)
                .ExecuteUpdateAsync(s => s.SetProperty(v => v.DeletedAt, DateTime.UtcNow));

        var info = await CreateClient().GetAsync($"/api/portal/{seeded.Token}");
        info.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await ErrorCode(info)).Should().Be("vendor.portal_token_invalid");

        var upload = await UploadAsync(CreateClient(), seeded.Token, PdfBytes(), "coi.pdf", "application/pdf");
        upload.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await ErrorCode(upload)).Should().Be("vendor.portal_token_invalid");
    }

    [Fact]
    public async Task Soft_deleted_org_with_an_active_link_returns_404_and_accepts_no_uploads()
    {
        // Account deletion never touches vendor rows or portal links (#269). The org
        // filter nulls the ThenInclude — and a direct POST that skips the info page must
        // not keep inserting documents into the deleted tenant.
        var seeded = await SeedLinkAsync();
        await using (var db = CreateSystemDb())
            await db.Organizations.Where(o => o.Id == seeded.OrgId)
                .ExecuteUpdateAsync(s => s.SetProperty(o => o.DeletedAt, DateTime.UtcNow));

        var info = await CreateClient().GetAsync($"/api/portal/{seeded.Token}");
        info.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await ErrorCode(info)).Should().Be("vendor.portal_token_invalid");

        var upload = await UploadAsync(CreateClient(), seeded.Token, PdfBytes(), "coi.pdf", "application/pdf");
        upload.StatusCode.Should().Be(HttpStatusCode.NotFound);
        await using var db2 = CreateSystemDb();
        (await db2.Documents.IgnoreQueryFilters().CountAsync(d => d.OrganizationId == seeded.OrgId))
            .Should().Be(0, "a deleted tenant must not accumulate new documents");
    }

    [Fact]
    public async Task Status_endpoint_stops_serving_a_soft_deleted_tenants_documents()
    {
        // #269: GetStatus deliberately tolerates IsActive=false (post-quota polling), but a
        // dead vendor/org must 404 — otherwise a deleted tenant's document status stays
        // publicly queryable through any old token.
        var seeded = await SeedLinkAsync();
        Guid docId;
        await using (var db = CreateSystemDb())
        {
            docId = Guid.NewGuid();
            db.Documents.Add(new Document
            {
                Id = docId, OrganizationId = seeded.OrgId, VendorId = seeded.VendorId,
                OriginalFileName = "d.pdf", BlobStorageUrl = "blob://d", FileSizeBytes = 1,
                ContentType = "application/pdf", DocumentType = "coi",
                CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
            });
            await db.SaveChangesAsync();
        }
        (await CreateClient().GetAsync($"/api/portal/{seeded.Token}/status/{docId}"))
            .StatusCode.Should().Be(HttpStatusCode.OK, "sanity: live tenant serves status");

        await using (var db = CreateSystemDb())
            await db.Vendors.Where(v => v.Id == seeded.VendorId)
                .ExecuteUpdateAsync(s => s.SetProperty(v => v.DeletedAt, DateTime.UtcNow));

        var resp = await CreateClient().GetAsync($"/api/portal/{seeded.Token}/status/{docId}");

        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await ErrorCode(resp)).Should().Be("vendor.portal_token_invalid");
    }

    [Fact]
    public async Task Dead_tenant_with_an_expired_link_answers_404_not_410()
    {
        // The null-guard runs BEFORE the expiry check: a 410 would acknowledge the token
        // was once valid for a now-deleted tenant (and invite "request a new link").
        var seeded = await SeedLinkAsync(expiresAt: DateTime.UtcNow.AddDays(-1));
        await using (var db = CreateSystemDb())
            await db.Organizations.Where(o => o.Id == seeded.OrgId)
                .ExecuteUpdateAsync(s => s.SetProperty(o => o.DeletedAt, DateTime.UtcNow));

        var info = await CreateClient().GetAsync($"/api/portal/{seeded.Token}");

        info.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await ErrorCode(info)).Should().Be("vendor.portal_token_invalid");
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
    public async Task Upload_accepts_a_heic_photo_and_stores_it_as_jpeg()
    {
        // The iPhone vendor case (#220): a HEIC capture uploads through the portal, is transcoded to
        // JPEG on ingest, and reaches the extraction queue scoped to the link's org.
        var seeded = await SeedLinkAsync();
        var client = CreateClient();

        var resp = await UploadAsync(client, seeded.Token, HeicPhotoBytes(), "coi.heic", "image/heic");

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var uploadId = (await Data(resp)).GetProperty("uploadId").GetGuid();
        await using var db = CreateSystemDb();
        var doc = await db.Documents.IgnoreQueryFilters().SingleAsync(d => d.Id == uploadId);
        doc.OrganizationId.Should().Be(seeded.OrgId);
        doc.ContentType.Should().Be("image/jpeg");
        doc.OriginalFileName.Should().Be("coi.heic");
        doc.ExtractionStatus.Should().Be(ExtractionStatus.Pending);
    }

    [Fact]
    public async Task Upload_of_an_undecodable_heic_is_400_and_consumes_no_quota_or_storage()
    {
        // Transcode runs BEFORE the blob upload + quota reservation, so a photo we can't decode
        // costs the vendor no permit and leaves no orphaned blob/document. (#220)
        var seeded = await SeedLinkAsync(maxUploads: 3, uploadCount: 0);
        var client = CreateClient();
        // A valid HEIC magic-byte header (passes validation) but a body the decoder can't read.
        var brokenHeic = FileWith(0x00, 0x00, 0x00, 0x18, 0x66, 0x74, 0x79, 0x70, 0x68, 0x65, 0x69, 0x63);

        var resp = await UploadAsync(client, seeded.Token, brokenHeic, "broken.heic", "image/heic");

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ErrorCode(resp)).Should().Be("document.unreadable_image");
        await using var db = CreateSystemDb();
        (await db.VendorPortalLinks.SingleAsync(l => l.Id == seeded.LinkId)).UploadCount.Should().Be(0);
        (await db.Documents.IgnoreQueryFilters().CountAsync(d => d.VendorId == seeded.VendorId)).Should().Be(0);
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
    public async Task Rate_limit_and_quota_429_codes_are_distinguishable_to_clients()
    {
        // #45 followup — pin the discriminator contract the AC asks for: a
        // client receiving a 429 from /api/portal/{token}/upload must be
        // able to tell "retry next hour" (rate limit) from "never retry,
        // link permanently exhausted" (quota) by inspecting `error.code`.
        // The two existing tests assert the two codes separately; this
        // test pins them as a CONTRASTING PAIR so a future rename of
        // either code (e.g. namespace-collapse to vendor.* on both sides)
        // can't silently dissolve the discriminator. The whole reason #45
        // exists is the pre-PR ambiguity between an empty 429 (limiter)
        // and an enveloped 429 (handler); this test makes regression of
        // that disambiguation a build break.

        // Quota path — runs under the SHARED fixture (rate limiter
        // disabled by default), so only the handler's quota check fires.
        var quotaSeeded = await SeedLinkAsync(maxUploads: 1, uploadCount: 1);
        var quotaClient = CreateClient();
        var quotaResp = await UploadAsync(
            quotaClient, quotaSeeded.Token, PdfBytes(), "q.pdf", "application/pdf");
        quotaResp.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
        var quotaCode = await ErrorCode(quotaResp);
        quotaCode.Should().Be("vendor.portal_quota_exceeded");

        // Rate-limit path — runs under RateLimitedFactory so the limiter
        // can actually reject. MaxUploads is high so the quota stays
        // under-budget; only the 10/hr token limiter trips on request 11.
        var rateSeeded = await SeedLinkAsync(maxUploads: 100);
        await using var rateFactory = RateLimitedFactory();
        var rateClient = rateFactory.CreateClient();
        for (var i = 0; i < 10; i++)
        {
            (await UploadAsync(
                rateClient, rateSeeded.Token, PdfBytes(), $"{i}.pdf", "application/pdf"))
                .StatusCode.Should().Be(HttpStatusCode.OK);
        }
        var rateResp = await UploadAsync(
            rateClient, rateSeeded.Token, PdfBytes(), "11.pdf", "application/pdf");
        rateResp.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
        var rateCode = await ErrorCode(rateResp);
        rateCode.Should().Be("rate_limit.exceeded");

        // The load-bearing assertion: the two 429 paths emit DIFFERENT
        // codes. A future refactor that aliased the codes (or renamed
        // one without the other) would fail here even if the per-test
        // assertions above continued passing under partial updates.
        rateCode.Should().NotBe(quotaCode,
            "the two 429 paths must remain distinguishable by error.code; #45 exists to " +
            "prevent the pre-PR ambiguity between an empty limiter-429 and an enveloped " +
            "handler-429 from re-emerging through code-aliasing.");
    }

    [Fact]
    public async Task Rate_limit_envelope_carries_the_full_documented_shape()
    {
        // #45 followup — pin every promised field of the rate-limit
        // envelope so a future regression that drops `data`, changes
        // `error.message`, or strips `correlationId` surfaces as a
        // build break instead of a silent client-contract change.
        //
        // The frontend's ApiEnvelope<T> type already expects:
        //   { data: T | null, error: { code, message, correlationId? } | null }
        // and the canonical 500-path (ExceptionHandlingMiddleware) emits
        // exactly that shape. Pre-#45 followup this hook emitted only
        // `data + error.{code, message}` with no correlationId — silently
        // diverging from the only other middleware-level error envelope
        // in the codebase. The followup added correlationId + switched
        // from a hand-rolled string literal to JsonSerializer to keep
        // the envelope in lockstep with the canonical shape.
        var seeded = await SeedLinkAsync(maxUploads: 100);
        await using var factory = RateLimitedFactory();
        var client = factory.CreateClient();
        for (var i = 0; i < 10; i++)
        {
            (await UploadAsync(
                client, seeded.Token, PdfBytes(), $"{i}.pdf", "application/pdf"))
                .StatusCode.Should().Be(HttpStatusCode.OK);
        }

        var throttled = await UploadAsync(
            client, seeded.Token, PdfBytes(), "11.pdf", "application/pdf");

        throttled.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
        throttled.Content.Headers.ContentType?.MediaType.Should().Be(
            "application/json",
            "Content-Type must match the rest of the API so clients can `await res.json()` " +
            "without sniffing.");

        var body = await throttled.Content.ReadFromJsonAsync<JsonElement>();

        // data: null (envelope contract — never `undefined` or missing).
        body.TryGetProperty("data", out var data).Should().BeTrue(
            "the envelope must always carry a `data` field — never omit it.");
        data.ValueKind.Should().Be(JsonValueKind.Null,
            "on the error path `data` is always JSON null, never an object.");

        // error.{code,message,correlationId} — the three documented fields.
        body.TryGetProperty("error", out var error).Should().BeTrue();
        error.GetProperty("code").GetString().Should().Be("rate_limit.exceeded");
        error.GetProperty("message").GetString().Should().Be(
            "Too many requests. Please try again later.",
            "the message string is part of the documented response contract; if it " +
            "changes (e.g. for localization), update this assertion + ADR 0004 in " +
            "the same PR.");
        // correlationId may be null when no CorrelationIdMiddleware ran, but the
        // field itself must always be PRESENT on the error envelope to match
        // ExceptionHandlingMiddleware's 500-path shape.
        error.TryGetProperty("correlationId", out var corr).Should().BeTrue(
            "every error envelope (this hook + ExceptionHandlingMiddleware) must expose " +
            "a `correlationId` field. The frontend ApiEnvelope<T> type at " +
            "frontend/src/lib/api.ts reads it for log correlation.");

        // The envelope MUST NOT leak the internal policy name. The
        // partition (`portal-token` vs `portal-ip` vs `auth-strict`) is
        // implementation detail and irrelevant to the client; a future
        // refactor that surfaced it would defeat the universal-code
        // contract.
        var raw = await throttled.Content.ReadAsStringAsync();
        raw.Should().NotContain("portal-token");
        raw.Should().NotContain("portal-ip");
        raw.Should().NotContain("auth-strict");
    }

    [Fact]
    public async Task Portal_GET_endpoints_allow_generous_polling()
    {
        // Portal READS (info + status) are deliberately NOT capped per-token — a vendor refreshing
        // the page or polling status must not burn the 10/hr upload token budget. They DO carry a
        // generous 240/hr PER-IP backstop so a single IP can't flood the public read routes (#242),
        // but legitimate polling stays well under it. We interleave info + status GETs at 35
        // iterations each (70 total) — past BOTH upload limits (10/hr token, 30/hr ip) and within the
        // 240/hr read-IP backstop — and assert none get a 429.
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

    [Fact]
    public async Task Portal_read_and_upload_per_ip_limits_are_independent()
    {
        // Regression for the per-IP key collision (#242 review): the upload (30/hr) and read (240/hr)
        // per-IP buckets MUST use separate keys. If they shared one key, the first upload would open a
        // 30-cap bucket and a vendor's status polling would 429 at ~30 reads. Interleave one upload +
        // 40 reads from one client (one IP) and assert every read succeeds: 40 is under the 240 read
        // cap but well over the 30 upload cap a shared bucket would wrongly impose.
        var seeded = await SeedLinkAsync(maxUploads: 100);
        await using var factory = RateLimitedFactory();
        var client = factory.CreateClient();

        (await UploadAsync(client, seeded.Token, PdfBytes(), "u.pdf", "application/pdf"))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        for (var i = 0; i < 40; i++)
        {
            var info = await client.GetAsync($"/api/portal/{seeded.Token}");
            info.StatusCode.Should().Be(HttpStatusCode.OK,
                $"read {i + 1}/40 must not be throttled by the upload's per-IP bucket — the buckets are independent");
        }
    }

    // ============================================================================================
    // #333 — idempotency: a double-submit must not duplicate the Document or burn a second permit
    // ============================================================================================

    [Fact]
    public async Task Repeat_upload_with_the_same_idempotency_key_creates_one_document_and_burns_one_permit()
    {
        var seeded = await SeedLinkAsync(maxUploads: 5);
        var client = CreateClient();
        var key = Guid.NewGuid().ToString("N");

        var first = await UploadWithKeyAsync(client, seeded.Token, key);
        var second = await UploadWithKeyAsync(client, seeded.Token, key);

        first.StatusCode.Should().Be(HttpStatusCode.OK);
        second.StatusCode.Should().Be(HttpStatusCode.OK);
        (await UploadIdOf(second)).Should().Be(await UploadIdOf(first), "the repeat replays the winner's document");

        await using var db = CreateSystemDb();
        (await db.Documents.IgnoreQueryFilters().CountAsync(d => d.VendorId == seeded.VendorId)).Should().Be(1);
        (await db.VendorPortalLinks.IgnoreQueryFilters().Where(l => l.Id == seeded.LinkId)
            .Select(l => l.UploadCount).FirstAsync())
            .Should().Be(1, "the repeat upload must not burn a second permit");
    }

    [Fact]
    public async Task Concurrent_same_key_uploads_create_one_document_and_burn_one_permit()
    {
        var seeded = await SeedLinkAsync(maxUploads: 5);
        var key = Guid.NewGuid().ToString("N");

        // Separate clients so the two POSTs truly race; the (org, key) unique index + the co-commit make
        // exactly one win — and because the permit reservation is in the same transaction, the loser's
        // conflict rolls its increment back, so only one permit is consumed.
        var responses = await Task.WhenAll(
            UploadWithKeyAsync(CreateClient(), seeded.Token, key),
            UploadWithKeyAsync(CreateClient(), seeded.Token, key));

        responses.Should().OnlyContain(r => r.StatusCode == HttpStatusCode.OK);
        var ids = await Task.WhenAll(responses.Select(UploadIdOf));
        ids.Should().OnlyContain(id => id == ids[0], "both racers resolve to the single winning document");

        await using var db = CreateSystemDb();
        (await db.Documents.IgnoreQueryFilters().CountAsync(d => d.VendorId == seeded.VendorId)).Should().Be(1);
        (await db.VendorPortalLinks.IgnoreQueryFilters().Where(l => l.Id == seeded.LinkId)
            .Select(l => l.UploadCount).FirstAsync())
            .Should().Be(1, "the losing racer's permit increment rolls back with its conflicted transaction");
    }

    [Fact]
    public async Task A_repeat_replays_even_when_it_would_otherwise_be_the_last_permit()
    {
        // Pins that the idempotency replay sits BEFORE the MaxUploads cap check: a retry of the upload
        // that took the LAST permit must replay the winner, not 429 (the burned-permit-on-retry bug, at
        // the boundary). maxUploads:1 — the first upload spends the only permit.
        var seeded = await SeedLinkAsync(maxUploads: 1);
        var client = CreateClient();
        var key = Guid.NewGuid().ToString("N");

        var first = await UploadWithKeyAsync(client, seeded.Token, key);
        var second = await UploadWithKeyAsync(client, seeded.Token, key);

        first.StatusCode.Should().Be(HttpStatusCode.OK);
        second.StatusCode.Should().Be(HttpStatusCode.OK, "the repeat replays the winner, it does not tip over the cap");
        (await UploadIdOf(second)).Should().Be(await UploadIdOf(first));

        await using var db = CreateSystemDb();
        (await db.Documents.IgnoreQueryFilters().CountAsync(d => d.VendorId == seeded.VendorId)).Should().Be(1);
        (await db.VendorPortalLinks.IgnoreQueryFilters().Where(l => l.Id == seeded.LinkId)
            .Select(l => l.UploadCount).FirstAsync()).Should().Be(1);
    }

    [Fact]
    public async Task An_oversize_client_key_degrades_to_no_dedupe_never_a_500()
    {
        // The untrusted-route guard: a client key longer than the honored bound is ignored (the upload
        // proceeds without dedupe), never overflowing IdempotencyRecord.Key (varchar 200) into a 500.
        var seeded = await SeedLinkAsync(maxUploads: 5);
        var client = CreateClient();
        var oversize = new string('a', 200);

        var first = await UploadWithKeyAsync(client, seeded.Token, oversize);
        var second = await UploadWithKeyAsync(client, seeded.Token, oversize);

        first.StatusCode.Should().Be(HttpStatusCode.OK);
        second.StatusCode.Should().Be(HttpStatusCode.OK);

        await using var db = CreateSystemDb();
        (await db.Documents.IgnoreQueryFilters().CountAsync(d => d.VendorId == seeded.VendorId))
            .Should().Be(2, "an oversize key is ignored — both uploads proceed (no dedupe), neither 500s");
        (await db.VendorPortalLinks.IgnoreQueryFilters().Where(l => l.Id == seeded.LinkId)
            .Select(l => l.UploadCount).FirstAsync())
            .Should().Be(2, "no dedupe means each upload burns its own permit");
    }

    [Fact]
    public async Task Distinct_idempotency_keys_create_two_documents_and_burn_two_permits()
    {
        // Sanity: idempotency never OVER-dedupes — two genuinely distinct uploads still both land.
        var seeded = await SeedLinkAsync(maxUploads: 5);
        var client = CreateClient();

        (await UploadWithKeyAsync(client, seeded.Token, Guid.NewGuid().ToString("N"))).StatusCode.Should().Be(HttpStatusCode.OK);
        (await UploadWithKeyAsync(client, seeded.Token, Guid.NewGuid().ToString("N"))).StatusCode.Should().Be(HttpStatusCode.OK);

        await using var db = CreateSystemDb();
        (await db.Documents.IgnoreQueryFilters().CountAsync(d => d.VendorId == seeded.VendorId)).Should().Be(2);
        (await db.VendorPortalLinks.IgnoreQueryFilters().Where(l => l.Id == seeded.LinkId)
            .Select(l => l.UploadCount).FirstAsync()).Should().Be(2);
    }

    [Fact]
    public async Task The_same_client_key_on_two_links_in_one_org_does_not_collide()
    {
        // ADR 0032 namespaces the stored key as "portal:{token}:{clientKey}" so two DIFFERENT links in the
        // SAME org can't dedupe against each other — the (OrganizationId, Key) index alone wouldn't keep
        // them apart, the token segment does. A regression that dropped {token} would make link B's upload
        // replay link A's document (a cross-link lost upload). Every other #333 test mints a fresh org per
        // link via SeedLinkAsync, so this is the only one that exercises the namespacing within one org.
        var orgId = Guid.NewGuid();
        var now = DateTime.UtcNow;
        var (vA, linkA, tokenA) = (Guid.NewGuid(), Guid.NewGuid(), $"tok-{Guid.NewGuid():N}");
        var (vB, linkB, tokenB) = (Guid.NewGuid(), Guid.NewGuid(), $"tok-{Guid.NewGuid():N}");
        await using (var db = CreateSystemDb())
        {
            db.Organizations.Add(new Organization { Id = orgId, Name = $"Org-{orgId:N}", CreatedAt = now, UpdatedAt = now });
            db.Subscriptions.Add(new Subscription
            {
                Id = Guid.NewGuid(), OrganizationId = orgId, Plan = "pro", Status = "active",
                HasVendorPortal = true, CreatedAt = now, UpdatedAt = now,
            });
            db.Vendors.Add(new Vendor { Id = vA, OrganizationId = orgId, Name = "Vendor A", CreatedAt = now, UpdatedAt = now });
            db.Vendors.Add(new Vendor { Id = vB, OrganizationId = orgId, Name = "Vendor B", CreatedAt = now, UpdatedAt = now });
            db.VendorPortalLinks.Add(new VendorPortalLink { Id = linkA, VendorId = vA, Token = tokenA, IsActive = true, MaxUploads = 5, UploadCount = 0, CreatedAt = now });
            db.VendorPortalLinks.Add(new VendorPortalLink { Id = linkB, VendorId = vB, Token = tokenB, IsActive = true, MaxUploads = 5, UploadCount = 0, CreatedAt = now });
            await db.SaveChangesAsync();
        }

        var key = Guid.NewGuid().ToString("N"); // the SAME client key sent to both links
        var respA = await UploadWithKeyAsync(CreateClient(), tokenA, key);
        var respB = await UploadWithKeyAsync(CreateClient(), tokenB, key);

        respA.StatusCode.Should().Be(HttpStatusCode.OK);
        respB.StatusCode.Should().Be(HttpStatusCode.OK);
        (await UploadIdOf(respB)).Should().NotBe(await UploadIdOf(respA),
            "the token-namespaced key keeps two links from deduping against each other within one org");

        await using var verify = CreateSystemDb();
        (await verify.Documents.IgnoreQueryFilters().CountAsync(d => d.VendorId == vA)).Should().Be(1);
        (await verify.Documents.IgnoreQueryFilters().CountAsync(d => d.VendorId == vB)).Should().Be(1);
    }
}
