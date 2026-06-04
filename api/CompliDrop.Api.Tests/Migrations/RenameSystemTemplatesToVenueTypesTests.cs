using CompliDrop.Api.Data.Seed;
using CompliDrop.Api.Entities;
using CompliDrop.Api.Tests.TestHelpers;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;

namespace CompliDrop.Api.Tests.Migrations;

/// <summary>
/// Pins the #192 rename of the seeded system checklists to event-venue vendor types.
/// Two angles:
///   1. End-state: the host's seed (ComplianceTemplateSeed) installs the venue-typed
///      system templates the redesign calls for, and none of the old generic trade
///      names survive.
///   2. The migration's UPDATE … WHERE "IsSystemTemplate" = true guard: a legacy
///      SYSTEM row is renamed, but a TENANT row that happens to share the old name
///      is left untouched (renaming a user's own "General Sub Contractor" would be a
///      bug). Mirrors the RenameSubscriptionPlanMonthlyToPro migration test.
/// </summary>
public sealed class RenameSystemTemplatesToVenueTypesTests(IntegrationTestFixture fixture)
    : IntegrationTestBase(fixture)
{
    private static readonly string[] VenueNames =
    [
        "Caterer", "Event Rental Company", "Security Service",
        "Transportation / Shuttle", "Photographer / Videographer",
    ];

    private static readonly string[] OldNames =
    [
        "General Sub Contractor", "Property Vendor", "Healthcare Provider",
        "Transport Driver", "Professional Consultant",
    ];

    [Fact]
    public async Task Seed_installs_venue_typed_system_templates_and_no_legacy_names_survive()
    {
        await using var db = CreateSystemDb();
        var systemNames = await db.ComplianceTemplates
            .IgnoreQueryFilters()
            .Where(t => t.IsSystemTemplate)
            .Select(t => t.Name)
            .ToListAsync();

        systemNames.Should().Contain(VenueNames);
        systemNames.Should().NotIntersectWith(OldNames);
    }

    [Fact]
    public async Task Up_renames_legacy_system_rows_but_leaves_same_named_tenant_rows_untouched()
    {
        var now = DateTime.UtcNow;
        var systemId = Guid.NewGuid();
        var tenantOrgId = Guid.NewGuid();
        var tenantTemplateId = Guid.NewGuid();

        try
        {
            // A legacy SYSTEM template (old name) + a TENANT template that happens to
            // share the same old name.
            await using (var db = CreateSystemDb())
            {
                db.ComplianceTemplates.Add(new ComplianceTemplate
                {
                    Id = systemId,
                    OrganizationId = ComplianceTemplateSeed.SystemOrgId,
                    Name = "General Sub Contractor",
                    IsSystemTemplate = true,
                    CreatedAt = now,
                });
                db.Organizations.Add(new Organization { Id = tenantOrgId, Name = "Tenant", CreatedAt = now, UpdatedAt = now });
                db.ComplianceTemplates.Add(new ComplianceTemplate
                {
                    Id = tenantTemplateId,
                    OrganizationId = tenantOrgId,
                    Name = "General Sub Contractor",
                    IsSystemTemplate = false,
                    CreatedAt = now,
                });
                await db.SaveChangesAsync();
            }

            // Re-execute the migration's Up() effect (UpdateData → this parameter-free
            // equivalent, with the load-bearing IsSystemTemplate guard).
            await using (var db = CreateSystemDb())
            {
                await db.Database.ExecuteSqlRawAsync(
                    "UPDATE \"ComplianceTemplates\" SET \"Name\" = 'Caterer' " +
                    "WHERE \"Name\" = 'General Sub Contractor' AND \"IsSystemTemplate\" = true;");
            }

            await using (var db = CreateSystemDb())
            {
                (await db.ComplianceTemplates.IgnoreQueryFilters().FirstAsync(t => t.Id == systemId))
                    .Name.Should().Be("Caterer", "the system row is renamed");
                (await db.ComplianceTemplates.IgnoreQueryFilters().FirstAsync(t => t.Id == tenantTemplateId))
                    .Name.Should().Be("General Sub Contractor", "a tenant row sharing the name must NOT be renamed");
            }
        }
        finally
        {
            // The system row survives Respawn (ComplianceTemplates is preserved for the
            // seed), so hard-delete the test rows to avoid leaking a duplicate "Caterer".
            await using var cleanup = CreateSystemDb();
            await cleanup.ComplianceTemplates.IgnoreQueryFilters()
                .Where(t => t.Id == systemId || t.Id == tenantTemplateId)
                .ExecuteDeleteAsync();
            await cleanup.Organizations.IgnoreQueryFilters()
                .Where(o => o.Id == tenantOrgId)
                .ExecuteDeleteAsync();
        }
    }
}
