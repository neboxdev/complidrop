using System.Net.Http.Json;
using System.Text.Json;
using CompliDrop.Api.Entities;
using CompliDrop.Api.Tests.TestHelpers;
using FluentAssertions;
using Xunit;

namespace CompliDrop.Api.Tests;

/// <summary>
/// The dashboard-stats flag that drives the onboarding checklist's "Link sent — waiting for their
/// upload" state (#239 delta 3). It must be tenant-scoped (reached only through the org-filtered
/// Vendors set, since VendorPortalLink has no global tenant filter) and react to link activity.
/// </summary>
[Collection("integration")]
public sealed class DashboardStatsTests(IntegrationTestFixture fixture) : IntegrationTestBase(fixture)
{
    [Fact]
    public async Task anyActivePortalLink_is_true_only_for_the_org_with_an_active_link()
    {
        var orgA = await RegisterAndLoginAsync();
        var orgB = await RegisterAndLoginAsync();

        (await FlagAsync(orgA.Client)).Should().BeFalse();
        (await FlagAsync(orgB.Client)).Should().BeFalse();

        await AddActiveLinkAsync(orgA.OrgId);

        // Org A flips true; org B stays false — the flag is scoped through the tenant-filtered Vendors.
        (await FlagAsync(orgA.Client)).Should().BeTrue();
        (await FlagAsync(orgB.Client)).Should().BeFalse();
    }

    [Fact]
    public async Task anyActivePortalLink_ignores_an_inactive_link()
    {
        var auth = await RegisterAndLoginAsync();
        await AddActiveLinkAsync(auth.OrgId, isActive: false);

        (await FlagAsync(auth.Client)).Should().BeFalse();
    }

    [Fact]
    public async Task complianceRate_excludes_not_yet_evaluated_documents()
    {
        // #318 FP-042: a freshly-uploaded doc sits ComplianceStatus.Pending until the worker grades
        // it. Counting those in the denominator flashed a demoralizing "0%" right after the first
        // upload. Rate = compliant / docs-with-a-verdict, so 2 compliant + 1 pending reads 100%, not 67%.
        var auth = await RegisterAndLoginAsync();
        await SeedDocsAsync(auth.OrgId,
            ComplianceStatus.Compliant,
            ComplianceStatus.Compliant,
            ComplianceStatus.Pending);

        var data = (await auth.Client.GetFromJsonAsync<JsonElement>("/api/dashboard/stats")).GetProperty("data");
        data.GetProperty("complianceRate").GetDouble().Should().Be(100,
            "the Pending (still-being-read) document must be excluded from the compliance-rate denominator");
    }

    private async Task SeedDocsAsync(Guid orgId, params ComplianceStatus[] statuses)
    {
        await using var db = CreateSystemDb();
        var now = DateTime.UtcNow;
        var i = 0;
        foreach (var status in statuses)
            db.Documents.Add(new Document
            {
                Id = Guid.NewGuid(),
                OrganizationId = orgId,
                OriginalFileName = $"doc{i++}.pdf",
                BlobStorageUrl = "memory://x",
                BlobStoragePath = $"path/{Guid.NewGuid():N}",
                FileSizeBytes = 1,
                ContentType = "application/pdf",
                ComplianceStatus = status,
                ExtractionStatus = ExtractionStatus.Completed,
                CreatedAt = now,
                UpdatedAt = now,
            });
        await db.SaveChangesAsync();
    }

    private async Task AddActiveLinkAsync(Guid orgId, bool isActive = true)
    {
        await using var db = CreateSystemDb();
        var vendorId = Guid.NewGuid();
        var now = DateTime.UtcNow;
        db.Vendors.Add(new Vendor { Id = vendorId, OrganizationId = orgId, Name = "V", CreatedAt = now, UpdatedAt = now });
        db.VendorPortalLinks.Add(new VendorPortalLink
        {
            Id = Guid.NewGuid(),
            VendorId = vendorId,
            Token = $"tok-{Guid.NewGuid():N}",
            IsActive = isActive,
            CreatedAt = now,
        });
        await db.SaveChangesAsync();
    }

    private static async Task<bool> FlagAsync(HttpClient client) =>
        (await client.GetFromJsonAsync<JsonElement>("/api/dashboard/stats"))
            .GetProperty("data").GetProperty("anyActivePortalLink").GetBoolean();
}
