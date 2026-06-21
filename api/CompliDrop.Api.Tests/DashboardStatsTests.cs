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
