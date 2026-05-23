using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using CompliDrop.Api.Entities;
using CompliDrop.Api.Tests.TestHelpers;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;

namespace CompliDrop.Api.Tests;

/// <summary>
/// Integration tests for the PUBLIC, unauthenticated vendor portal endpoints
/// (<see cref="CompliDrop.Api.Endpoints.VendorPortalEndpoints"/>): token info, upload, and status.
/// This is the highest-risk attack surface (project rule: "treat inputs as untrusted"), so the
/// tests assert token validation + tenant-leak avoidance, rate limits (token 10/hr, ip 30/hr),
/// the <c>MaxUploads</c> quota with auto-deactivation, magic-byte file validation, and org scoping.
///
/// The shared fixture boots the host with <c>RateLimiting:Enabled=false</c>, so the two rate-limit
/// tests spin up a one-off host with the limiter enabled (same pattern as ResendWebhookTests). Each
/// gets a fresh host → fresh partition state, so limiter counters never leak across tests.
/// </summary>
public sealed class VendorPortalEndpointsTests(IntegrationTestFixture fixture) : IntegrationTestBase(fixture)
{
    // A real %PDF magic-byte header, padded past the validator's 8-byte minimum.
    private static readonly byte[] PdfBytes = FileWith(0x25, 0x50, 0x44, 0x46);
    // Plain text ("hello wd") — matches no supported type, used to test the spoofed-Content-Type path.
    private static readonly byte[] TextBytes = FileWith(0x68, 0x65, 0x6C, 0x6C, 0x6F, 0x20, 0x77, 0x64);

    private static byte[] FileWith(params byte[] header)
    {
        var buf = new byte[64];
        Array.Copy(header, buf, header.Length);
        return buf;
    }

    private static MultipartFormDataContent UploadForm(byte[] bytes, string fileName, string contentType)
    {
        var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(bytes);
        file.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        form.Add(file, "file", fileName);
        return form;
    }

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

    // ---- AC1: token validation + no tenant-data leak --------------------------------------------

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

    // ---- AC4: magic-byte validation ------------------------------------------------------------

    [Fact]
    public async Task Upload_with_spoofed_content_type_is_rejected_on_bytes()
    {
        var seeded = await SeedLinkAsync();
        var client = CreateClient();

        // Plain-text bytes, a .pdf name, and an application/pdf Content-Type — the lie doesn't help.
        var resp = await UploadAsync(client, seeded.Token, TextBytes, "evil.pdf", "application/pdf");

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ErrorCode(resp)).Should().Be("document.unsupported_format");

        await using var db = CreateSystemDb();
        (await db.Documents.IgnoreQueryFilters().CountAsync(d => d.VendorId == seeded.VendorId))
            .Should().Be(0); // nothing persisted for a rejected file
    }

    // ---- AC4 + AC5: valid upload is scoped to the link's org -----------------------------------

    [Fact]
    public async Task Valid_upload_succeeds_and_is_scoped_to_the_links_org()
    {
        var a = await SeedLinkAsync();
        var b = await SeedLinkAsync(); // a different org/vendor
        var client = CreateClient();

        var resp = await UploadAsync(client, a.Token, PdfBytes, "coi.pdf", "application/pdf");

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
    }

    [Fact]
    public async Task Status_is_scoped_to_the_uploads_link_and_not_exposed_via_another_link()
    {
        var a = await SeedLinkAsync();
        var b = await SeedLinkAsync(); // a different org/vendor
        var client = CreateClient();

        var uploadId = (await Data(await UploadAsync(client, a.Token, PdfBytes, "coi.pdf", "application/pdf")))
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

    // ---- AC3: MaxUploads quota + auto-deactivation ---------------------------------------------

    [Fact]
    public async Task Reaching_MaxUploads_deactivates_link_and_refuses_further_uploads()
    {
        var seeded = await SeedLinkAsync(maxUploads: 2);
        var client = CreateClient();

        (await UploadAsync(client, seeded.Token, PdfBytes, "1.pdf", "application/pdf"))
            .StatusCode.Should().Be(HttpStatusCode.OK);
        (await UploadAsync(client, seeded.Token, PdfBytes, "2.pdf", "application/pdf"))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        // The second upload reached the cap → the link auto-deactivates.
        await using (var db = CreateSystemDb())
        {
            var link = await db.VendorPortalLinks.SingleAsync(l => l.Id == seeded.LinkId);
            link.UploadCount.Should().Be(2);
            link.IsActive.Should().BeFalse();
        }

        // A third upload is refused because the link is now inactive.
        var third = await UploadAsync(client, seeded.Token, PdfBytes, "3.pdf", "application/pdf");
        third.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await ErrorCode(third)).Should().Be("vendor.portal_token_invalid");

        await using var db2 = CreateSystemDb();
        (await db2.Documents.IgnoreQueryFilters().CountAsync(d => d.VendorId == seeded.VendorId)).Should().Be(2);
    }

    [Fact]
    public async Task Upload_to_an_active_link_already_at_quota_returns_429_and_deactivates()
    {
        // An active link whose count already equals MaxUploads (e.g. cap lowered after issuance).
        var seeded = await SeedLinkAsync(maxUploads: 2, uploadCount: 2);
        var client = CreateClient();

        var resp = await UploadAsync(client, seeded.Token, PdfBytes, "x.pdf", "application/pdf");

        resp.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
        (await ErrorCode(resp)).Should().Be("vendor.portal_quota_exceeded");

        await using var db = CreateSystemDb();
        var link = await db.VendorPortalLinks.SingleAsync(l => l.Id == seeded.LinkId);
        link.IsActive.Should().BeFalse();
        (await db.Documents.IgnoreQueryFilters().CountAsync(d => d.VendorId == seeded.VendorId)).Should().Be(0);
    }

    // ---- AC2: rate limits (require the limiter, which the shared fixture disables) --------------

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
            var ok = await UploadAsync(client, seeded.Token, PdfBytes, $"{i}.pdf", "application/pdf");
            ok.StatusCode.Should().Be(HttpStatusCode.OK, "the first 10 uploads are within the 10/hr token limit");
        }

        var throttled = await UploadAsync(client, seeded.Token, PdfBytes, "11.pdf", "application/pdf");
        throttled.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);

        // The 11th request was rejected by the limiter middleware *before* the handler ran, so the
        // upload count is still 10 (no 11th document was created).
        await using var db = CreateSystemDb();
        (await db.VendorPortalLinks.SingleAsync(l => l.Id == seeded.LinkId)).UploadCount.Should().Be(10);
    }

    [Fact]
    public async Task Exceeding_the_portal_ip_rate_limit_returns_429()
    {
        // The ip limiter (30/hr) partitions on client IP, which is constant for the test host. We
        // use 31 DISTINCT tokens so the per-token limiter (10/hr) never trips — the only thing that
        // can throttle the 31st request is the shared ip partition. Invalid tokens 404 at the
        // handler but still consume an ip permit (the limiter runs before the handler).
        await using var factory = RateLimitedFactory();
        var client = factory.CreateClient();

        for (var i = 0; i < 30; i++)
        {
            var resp = await client.PostAsync($"/api/portal/ip-{i}-{Guid.NewGuid():N}/upload", null);
            resp.StatusCode.Should().Be(HttpStatusCode.NotFound, "the first 30 requests are within the 30/hr ip limit");
        }

        var throttled = await client.PostAsync($"/api/portal/ip-31-{Guid.NewGuid():N}/upload", null);
        throttled.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
    }
}
