using System.Reflection;
using CompliDrop.Api.Data;
using CompliDrop.Api.Data.Seed;
using CompliDrop.Api.Entities;
using CompliDrop.Api.Migrations;
using CompliDrop.Api.Tests.TestHelpers;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Npgsql;

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

        // The rename target ("Caterer") collides with the boot-seeded "Caterer" under #251's partial
        // unique index on live system templates. Run under a transaction that DROPS the guard and
        // ROLLS BACK — restoring the index and undoing the seeded test rows (no leaked duplicate),
        // since Postgres DDL is transactional. (The boot "Caterer" lives outside this transaction and
        // is untouched.)
        await using var conn = new NpgsqlConnection(Fixture.ConnectionString);
        await conn.OpenAsync();
        await using var tx = await conn.BeginTransactionAsync();
        try
        {
            await ExecAsync(conn, tx, "DROP INDEX \"IX_ComplianceTemplates_Name_SystemUnique\"");

            // A legacy SYSTEM template (old name) + a TENANT template that happens to share the name.
            await using (var db = new SystemDbContext(
                new DbContextOptionsBuilder<SystemDbContext>().UseNpgsql(conn).Options))
            {
                await db.Database.UseTransactionAsync(tx);
                db.ComplianceTemplates.Add(new ComplianceTemplate
                {
                    Id = systemId, OrganizationId = ComplianceTemplateSeed.SystemOrgId,
                    Name = "General Sub Contractor", IsSystemTemplate = true, CreatedAt = now,
                });
                db.Organizations.Add(new Organization { Id = tenantOrgId, Name = "Tenant", CreatedAt = now, UpdatedAt = now });
                db.ComplianceTemplates.Add(new ComplianceTemplate
                {
                    Id = tenantTemplateId, OrganizationId = tenantOrgId,
                    Name = "General Sub Contractor", IsSystemTemplate = false, CreatedAt = now,
                });
                await db.SaveChangesAsync();
            }

            // Re-execute the migration's Up() effect (UpdateData → this parameter-free equivalent,
            // with the load-bearing IsSystemTemplate guard).
            await ExecAsync(conn, tx,
                "UPDATE \"ComplianceTemplates\" SET \"Name\" = 'Caterer' " +
                "WHERE \"Name\" = 'General Sub Contractor' AND \"IsSystemTemplate\" = true;");

            (await ScalarStringAsync(conn, tx, $"SELECT \"Name\" FROM \"ComplianceTemplates\" WHERE \"Id\" = '{systemId}'"))
                .Should().Be("Caterer", "the system row is renamed");
            (await ScalarStringAsync(conn, tx, $"SELECT \"Name\" FROM \"ComplianceTemplates\" WHERE \"Id\" = '{tenantTemplateId}'"))
                .Should().Be("General Sub Contractor", "a tenant row sharing the name must NOT be renamed");
        }
        finally
        {
            await tx.RollbackAsync(); // restores the dropped index and undoes the seeded test rows
        }
    }

    private static async Task ExecAsync(NpgsqlConnection conn, NpgsqlTransaction tx, string sql)
    {
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task<string?> ScalarStringAsync(NpgsqlConnection conn, NpgsqlTransaction tx, string sql)
    {
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        return (string?)await cmd.ExecuteScalarAsync();
    }
}

/// <summary>
/// Pure unit test that inspects the migration's OWN operations (no DB), so a regression
/// that dropped the load-bearing <c>IsSystemTemplate</c> key guard from the UpdateData
/// renames — which a hand-written-SQL re-derivation could not catch — fails the build.
/// </summary>
public sealed class RenameSystemTemplatesToVenueTypesGuardTests
{
    [Fact]
    public void Up_renames_are_scoped_to_system_templates_and_drop_the_orphan_rule()
    {
        var migration = new RenameSystemTemplatesToVenueTypes();
        var builder = new MigrationBuilder(activeProvider: null);
        // Up is protected — invoke via reflection to capture the real operations.
        typeof(RenameSystemTemplatesToVenueTypes)
            .GetMethod("Up", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(migration, [builder]);

        var renames = builder.Operations.OfType<UpdateDataOperation>().ToList();
        renames.Should().HaveCount(5, "one rename per seeded system checklist");
        renames.Should().OnlyContain(
            op => op.Table == "ComplianceTemplates"
                  && op.KeyColumns.Contains("Name")
                  && op.KeyColumns.Contains("IsSystemTemplate") // the guard
                  && op.Columns.Contains("Name"),
            "every rename must be keyed on IsSystemTemplate so a user's same-named checklist is left alone");

        // The orphan additional_insured cleanup is present + IsSystemTemplate-scoped.
        builder.Operations.OfType<SqlOperation>()
            .Should().ContainSingle(op =>
                op.Sql.Contains("additional_insured") && op.Sql.Contains("IsSystemTemplate"));
    }
}
