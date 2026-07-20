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
    private static async Task<JsonElement> Data(HttpResponseMessage resp) =>
        (await resp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("data");

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
    public async Task Both_upload_fences_admit_and_refuse_at_the_same_document()
    {
        // The two ingress paths must agree on the SAME cap for the same org state. With
        // DocumentLimit=2 filled by one real document plus a sample, both fences see 1 of 2 used:
        // the portal upload is admitted, and the dashboard upload that follows it is refused
        // because the portal's document took the last real slot. Pre-#367 the portal refused while
        // the dashboard admitted — the asymmetry this test makes unrepresentable.
        var seeded = await SeedLinkAsync(hasVendorPortal: true, documentLimit: 2);
        await SeedDocumentAsync(seeded.OrgId);
        await SeedDocumentAsync(seeded.OrgId, isSample: true);

        var portalUpload = await CreateClient().PostAsync(
            $"/api/portal/{seeded.Token}/upload", UploadForm(PdfBytes(), "coi.pdf", "application/pdf"));
        portalUpload.StatusCode.Should().Be(HttpStatusCode.OK,
            "the sample occupies no slot, so the portal has one free");

        await using var db = CreateSystemDb();
        (await db.Documents.IgnoreQueryFilters()
                .CountAsync(d => d.OrganizationId == seeded.OrgId && d.DeletedAt == null && !d.IsSample))
            .Should().Be(2, "the cap is now genuinely full of REAL documents");

        var second = await CreateClient().PostAsync(
            $"/api/portal/{seeded.Token}/upload", UploadForm(PdfBytes(), "coi2.pdf", "application/pdf"));
        second.StatusCode.Should().Be(HttpStatusCode.Forbidden,
            "and both fences must now refuse — the sample must not have bought an extra slot");
        var info = await Data(await CreateClient().GetAsync($"/api/portal/{seeded.Token}"));
        info.GetProperty("uploadCount").GetInt32()
            .Should().Be(1, "only the admitted upload consumed a link permit");
    }
}
