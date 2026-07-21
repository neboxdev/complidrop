using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CompliDrop.Api.Entities;
using CompliDrop.Api.Tests.TestHelpers;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using static CompliDrop.Api.Tests.TestHelpers.UploadFixtures;

namespace CompliDrop.Api.Tests;

/// <summary>
/// The structural pin for <c>PlanDocumentScope.CountsTowardLimit</c> (#367).
///
/// Three surfaces read the plan-limit population: the dashboard upload fence
/// (<c>DocumentEndpoints.UploadDocument</c>), the vendor-portal upload fence
/// (<c>VendorPortalEndpoints.UploadViaPortal</c>, the #261 mirror), and the Settings usage tile
/// (<c>BillingEndpoints.GetSubscription</c>). #367 WAS these three disagreeing — the expression had
/// been hand-copied and only the dashboard copy excluded the sample-demo document, so a capped org's
/// own upload succeeded while a real vendor's upload was refused a document early, and the tile
/// reported a number neither gate enforced.
///
/// These tests assert agreement on ONE seeded org state rather than re-testing each predicate in
/// isolation, so re-introducing the drift fails here even if each surface stays internally coherent.
/// Mirrors the way <c>DocumentSupersession</c>'s in-memory export mirror is pinned equal to its
/// predicate.
/// </summary>
public sealed class PlanLimitConsistencyTests(IntegrationTestFixture fixture) : IntegrationTestBase(fixture)
{
    private static async Task<JsonElement> Error(HttpResponseMessage resp) =>
        (await resp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error");

    private async Task SeedDocumentAsync(Guid orgId, bool isSample = false, DateTime? deletedAt = null)
    {
        await using var db = CreateSystemDb();
        db.Documents.Add(new Document
        {
            Id = Guid.NewGuid(),
            OrganizationId = orgId,
            OriginalFileName = "seed.pdf",
            BlobStorageUrl = "blob://seed",
            FileSizeBytes = 1,
            ContentType = "application/pdf",
            DocumentType = "coi",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
            DeletedAt = deletedAt,
            IsSample = isSample,
        });
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task The_settings_tile_reports_exactly_what_the_dashboard_fence_enforces()
    {
        // One real document + one sample + one soft-deleted, against DocumentLimit=2. The enforced
        // count is 1, so a dashboard upload MUST succeed — and the tile must say 1, not 2 or 3.
        // Pre-#367 the tile said 2 while the fence counted 1: a tile reading "2 / 2 full" over an
        // upload that still worked.
        var auth = await RegisterAndLoginAsync();
        await SetPortalEntitlementAsync(auth.OrgId, on: false, documentLimit: 2);
        await SeedDocumentAsync(auth.OrgId);
        await SeedDocumentAsync(auth.OrgId, isSample: true);
        await SeedDocumentAsync(auth.OrgId, deletedAt: DateTime.UtcNow);

        var tile = await auth.Client.GetFromJsonAsync<JsonElement>("/api/billing/subscription");
        var reported = tile.GetProperty("data").GetProperty("documentsUsed").GetInt32();
        reported.Should().Be(1, "the tile must count only documents that occupy a paid slot");

        var upload = await auth.Client.PostAsync("/api/documents/upload",
            UploadForm(PdfBytes(), "real.pdf", "application/pdf"));
        upload.StatusCode.Should().Be(HttpStatusCode.Created,
            "the fence counts the same 1 of 2 slots the tile just reported, so a slot is free");

        var after = await auth.Client.GetFromJsonAsync<JsonElement>("/api/billing/subscription");
        after.GetProperty("data").GetProperty("documentsUsed").GetInt32()
            .Should().Be(reported + 1, "the accepted upload must move the tile by exactly one");
    }

    [Fact]
    public async Task A_portal_upload_and_the_dashboard_fence_share_one_cap()
    {
        // The cross-fence pin: one org, one cap, both ingress paths. DocumentLimit=2 filled by one
        // real document plus a sample means both fences see 1 of 2 used — so the PORTAL upload is
        // admitted, and the DASHBOARD upload that follows is refused, because the portal's document
        // took the last real slot. Pre-#367 the portal refused while the dashboard admitted; this
        // arrangement makes that asymmetry unrepresentable in either direction.
        //
        // The org is built with RegisterAndLoginAsync (not SeedLinkAsync) precisely so an
        // authenticated client and the portal link belong to the SAME org — SeedLinkAsync mints an
        // org with no user, which is why an earlier draft of this test could only reach the portal.
        var auth = await RegisterAndLoginAsync();
        await SetPortalEntitlementAsync(auth.OrgId, on: true, documentLimit: 2);
        var token = await SeedLinkForOrgAsync(auth.OrgId);
        await SeedDocumentAsync(auth.OrgId);
        await SeedDocumentAsync(auth.OrgId, isSample: true);

        var portalUpload = await CreateClient().PostAsync(
            $"/api/portal/{token}/upload", UploadForm(PdfBytes(), "coi.pdf", "application/pdf"));
        portalUpload.StatusCode.Should().Be(HttpStatusCode.OK,
            "the sample occupies no slot, so the portal has one free");

        await using (var db = CreateSystemDb())
        {
            (await db.Documents.IgnoreQueryFilters()
                    .CountAsync(d => d.OrganizationId == auth.OrgId && d.DeletedAt == null && !d.IsSample))
                .Should().Be(2, "the cap is now genuinely full of REAL documents");
        }

        var dashboardUpload = await auth.Client.PostAsync(
            "/api/documents/upload", UploadForm(PdfBytes(), "mine.pdf", "application/pdf"));
        dashboardUpload.StatusCode.Should().Be(HttpStatusCode.Forbidden,
            "the dashboard fence must honour the document the PORTAL just accepted");
        (await Error(dashboardUpload)).GetProperty("code").GetString().Should().Be("plan.limit_reached");

        var second = await CreateClient().PostAsync(
            $"/api/portal/{token}/upload", UploadForm(PdfBytes(), "coi2.pdf", "application/pdf"));
        second.StatusCode.Should().Be(HttpStatusCode.Forbidden,
            "and the portal fence refuses at the same document — the sample bought no extra slot");
        (await Error(second)).GetProperty("code").GetString().Should().Be("vendor.portal_document_limit_reached");
    }

    /// <summary>Seeds a vendor + active portal link into an EXISTING org and returns the token.</summary>
    private async Task<string> SeedLinkForOrgAsync(Guid orgId)
    {
        var vendorId = Guid.NewGuid();
        var token = $"tok-{Guid.NewGuid():N}";
        var now = DateTime.UtcNow;
        await using var db = CreateSystemDb();
        db.Vendors.Add(new Vendor
        {
            Id = vendorId,
            OrganizationId = orgId,
            Name = $"Vendor-{vendorId:N}",
            CreatedAt = now,
            UpdatedAt = now,
        });
        db.VendorPortalLinks.Add(new VendorPortalLink
        {
            Id = Guid.NewGuid(),
            VendorId = vendorId,
            Token = token,
            IsActive = true,
            MaxUploads = 20,
            UploadCount = 0,
            CreatedAt = now,
        });
        await db.SaveChangesAsync();
        return token;
    }

    [Fact]
    public async Task The_dashboard_total_still_counts_the_sample_while_the_billing_tile_does_not()
    {
        // Pins the DELIBERATE asymmetry that ADR 0028 Amendment 1 and the reviewers.md do-not-flag
        // list now assert in prose: /dashboard/stats totalDocuments answers "what is in my account"
        // (the sample is genuinely there and labelled), while /billing/subscription documentsUsed
        // answers "what do I owe for". Without this test the next consistency-minded refactor —
        // exactly the class of change #367 was — could point totalDocuments at
        // PlanDocumentScope.CountsTowardLimit and contradict the ADR with the suite still green.
        var auth = await RegisterAndLoginAsync();
        await SeedDocumentAsync(auth.OrgId);
        await SeedDocumentAsync(auth.OrgId, isSample: true);

        var stats = await auth.Client.GetFromJsonAsync<JsonElement>("/api/dashboard/stats");
        var billing = await auth.Client.GetFromJsonAsync<JsonElement>("/api/billing/subscription");

        stats.GetProperty("data").GetProperty("totalDocuments").GetInt32()
            .Should().Be(2, "the dashboard total counts the sample on purpose (ADR 0028 Amendment 1)");
        billing.GetProperty("data").GetProperty("documentsUsed").GetInt32()
            .Should().Be(1, "but it occupies no paid slot, so the billing tile excludes it");
    }
}
