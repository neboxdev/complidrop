using System.Text.Json;
using CompliDrop.Api.Data;
using CompliDrop.Api.Data.Seed;
using CompliDrop.Api.Entities;
using CompliDrop.Api.Services;
using CompliDrop.Api.Tests.TestHelpers;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace CompliDrop.Api.Tests;

/// <summary>
/// Pins the #400 seed changes: the Caterer checklist now requires liquor liability (bar / alcohol
/// service) and the Security Service checklist now requires general liability, plus the additive
/// idempotent reconcile that lets <see cref="ComplianceTemplateSeed.EnsureAsync"/> back-fill those
/// new rules onto ALREADY-SEEDED system templates (the seed was historically insert-only, so a
/// rule added to a definition would be inert in prod without the reconcile).
///
/// The system templates are shared across the integration collection (Respawn ignores the
/// ComplianceTemplates / ComplianceRules tables — see IntegrationTestFixture), so the reconcile
/// test mutates them only inside a rolled-back transaction, exactly like SystemTemplateDedupTests.
/// </summary>
public sealed class ComplianceTemplateSeedTests(IntegrationTestFixture fixture) : IntegrationTestBase(fixture)
{
    // ---- structural: the seeded system templates carry the #400 rules ----

    [Fact]
    public async Task Seeded_Caterer_template_requires_liquor_liability_at_one_million()
    {
        await using var db = CreateSystemDb();
        var caterer = await db.ComplianceTemplates.IgnoreQueryFilters().Include(t => t.Rules)
            .FirstAsync(t => t.IsSystemTemplate && t.Name == ComplianceTemplateSeed.SampleVendorTemplateName);

        caterer.Rules.Should().ContainSingle(r =>
            r.DocumentType == "coi" && r.FieldName == "liquor_liability_limit"
            && r.Operator == "min_value" && r.ExpectedValue == "1000000");
    }

    [Fact]
    public async Task Seeded_Security_Service_template_requires_general_liability_at_one_million()
    {
        await using var db = CreateSystemDb();
        var security = await db.ComplianceTemplates.IgnoreQueryFilters().Include(t => t.Rules)
            .FirstAsync(t => t.IsSystemTemplate && t.Name == "Security Service");

        security.Rules.Should().ContainSingle(r =>
            r.DocumentType == "coi" && r.FieldName == "general_liability_limit"
            && r.Operator == "min_value" && r.ExpectedValue == "1000000");
    }

    // ---- the additive idempotent reconcile onto already-seeded system templates ----

    [Fact]
    public async Task EnsureAsync_backfills_missing_rules_onto_seeded_system_templates_without_touching_tenants_or_duplicating()
    {
        await using var conn = new NpgsqlConnection(Fixture.ConnectionString);
        await conn.OpenAsync();
        await using var tx = await conn.BeginTransactionAsync();
        try
        {
            var tenantOrgId = Guid.NewGuid();
            var tenantTemplateId = Guid.NewGuid();

            // Recreate the PRE-#400 seeded state inside the rolled-back transaction: the system
            // Caterer / Security Service rows exist but LACK the newly-added rules.
            await ExecAsync(conn, tx, """
                DELETE FROM "ComplianceRules" cr USING "ComplianceTemplates" ct
                WHERE cr."ComplianceTemplateId" = ct."Id" AND ct."IsSystemTemplate" = true
                  AND ((ct."Name" = 'Caterer' AND cr."FieldName" = 'liquor_liability_limit')
                    OR (ct."Name" = 'Security Service' AND cr."FieldName" = 'general_liability_limit'));
                """);

            // A tenant-owned template that shares a seed name ("Caterer") with its OWN rule set —
            // the reconcile must never load or touch it (IsSystemTemplate = false).
            await using (var seed = TxContext(conn))
            {
                await seed.Database.UseTransactionAsync(tx);
                seed.Organizations.Add(new Organization
                {
                    Id = tenantOrgId, Name = "Tenant", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
                });
                seed.ComplianceTemplates.Add(new ComplianceTemplate
                {
                    Id = tenantTemplateId, OrganizationId = tenantOrgId, Name = "Caterer",
                    IsSystemTemplate = false, CreatedAt = DateTime.UtcNow
                });
                seed.ComplianceRules.Add(new ComplianceRule
                {
                    Id = Guid.NewGuid(), ComplianceTemplateId = tenantTemplateId,
                    DocumentType = "coi", FieldName = "general_liability_limit", Operator = "min_value",
                    ExpectedValue = "500000", SortOrder = 1
                });
                await seed.SaveChangesAsync();
            }

            // Precondition: the deletes landed.
            (await ScalarAsync(conn, tx, CatererLiquorCountSql)).Should().Be(0, "precondition: the system Caterer lacks the liquor rule");
            (await ScalarAsync(conn, tx, SecurityGlCountSql)).Should().Be(0, "precondition: the system Security Service lacks the GL rule");

            // First reconcile: back-fills the two missing rules onto the ALREADY-SEEDED system rows
            // (not a whole-template insert — the templates already exist).
            await using (var run1 = TxContext(conn))
            {
                await run1.Database.UseTransactionAsync(tx);
                await ComplianceTemplateSeed.EnsureAsync(run1);
            }

            (await ScalarAsync(conn, tx, CatererLiquorCountSql)).Should().Be(1, "the reconcile back-fills the missing liquor rule");
            (await ScalarAsync(conn, tx, SecurityGlCountSql)).Should().Be(1, "the reconcile back-fills the missing GL rule");
            (await ScalarStringAsync(conn, tx, CatererLiquorExpectedValueSql))
                .Should().Be("1000000", "the re-added rule carries the seeded $1M threshold");

            // The tenant template that shares the name is UNTOUCHED — still exactly its one own rule,
            // and it never gained the system Caterer's liquor rule.
            (await ScalarAsync(conn, tx, $"SELECT count(*) FROM \"ComplianceRules\" WHERE \"ComplianceTemplateId\" = '{tenantTemplateId}'"))
                .Should().Be(1, "a tenant-owned template is never reconciled");
            (await ScalarAsync(conn, tx, $"SELECT count(*) FROM \"ComplianceRules\" WHERE \"ComplianceTemplateId\" = '{tenantTemplateId}' AND \"FieldName\" = 'liquor_liability_limit'"))
                .Should().Be(0, "the tenant template did not gain the system Caterer's liquor rule");

            // Second reconcile: idempotent — the rules are present, so it adds NOTHING (no duplicates).
            await using (var run2 = TxContext(conn))
            {
                await run2.Database.UseTransactionAsync(tx);
                await ComplianceTemplateSeed.EnsureAsync(run2);
            }

            (await ScalarAsync(conn, tx, CatererLiquorCountSql)).Should().Be(1, "a repeat reconcile adds no duplicate liquor rule");
            (await ScalarAsync(conn, tx, SecurityGlCountSql)).Should().Be(1, "a repeat reconcile adds no duplicate GL rule");
        }
        finally
        {
            await tx.RollbackAsync(); // restore the shared system seed for the rest of the collection
        }
    }

    // ---- discriminating behaviour: the seeded Caterer liquor rule actually grades ----

    [Theory]
    [InlineData(null, ComplianceStatus.NonCompliant)]      // liquor liability absent
    [InlineData("500000", ComplianceStatus.NonCompliant)]  // liquor liability below the $1M minimum
    [InlineData("1000000", ComplianceStatus.Compliant)]    // liquor liability exactly at the minimum
    [InlineData("2000000", ComplianceStatus.Compliant)]    // liquor liability above the minimum
    public async Task Seeded_Caterer_template_grades_liquor_liability_discriminatingly(
        string? liquorValue, ComplianceStatus expected)
    {
        // Every OTHER Caterer rule is made to PASS (GL 2M ≥ 1M, a future expiration, workers-comp
        // present), so liquor liability is the SOLE differentiator. If the liquor rule were absent
        // from the seed, all four cases would grade Compliant and the two NonCompliant rows here
        // would fail — this is the test that proves the seeded rule is present AND governs.
        var orgId = Guid.NewGuid();
        var vendorId = Guid.NewGuid();
        var docId = Guid.NewGuid();
        var now = DateTime.UtcNow;

        await using (var setup = CreateSystemDb())
        {
            var catererId = (await setup.ComplianceTemplates
                .FirstAsync(t => t.IsSystemTemplate && t.Name == ComplianceTemplateSeed.SampleVendorTemplateName)).Id;

            setup.Organizations.Add(new Organization { Id = orgId, Name = $"Org-{orgId:N}", CreatedAt = now, UpdatedAt = now });
            setup.Vendors.Add(new Vendor
            {
                Id = vendorId, OrganizationId = orgId, Name = "Caterer Co",
                ComplianceTemplateId = catererId, CreatedAt = now, UpdatedAt = now,
            });

            var fields = new Dictionary<string, object> { ["workers_comp_limit"] = "1000000" };
            if (liquorValue is not null) fields["liquor_liability_limit"] = liquorValue;

            setup.Documents.Add(new Document
            {
                Id = docId, OrganizationId = orgId, VendorId = vendorId,
                DocumentType = "coi", OriginalFileName = "coi.pdf",
                GeneralLiabilityLimit = 2_000_000m, ExpirationDate = now.AddYears(1),
                ExtractionFields = JsonSerializer.SerializeToDocument(fields),
                CreatedAt = now, UpdatedAt = now,
            });
            await setup.SaveChangesAsync();
        }

        var user = new FakeCurrentUser { UserId = Guid.NewGuid(), OrganizationId = orgId };
        await using var appDb = CreateAppDb(user);
        await using var sysDb = CreateSystemDb();
        var status = await new ComplianceCheckService(
                appDb, sysDb, new FixedTimeProvider(now), NullLogger<ComplianceCheckService>.Instance)
            .EvaluateForSystemAsync(docId, default);

        status.Should().Be(expected);
    }

    // ---- helpers ----

    private static SystemDbContext TxContext(NpgsqlConnection conn) =>
        new(new DbContextOptionsBuilder<SystemDbContext>().UseNpgsql(conn).Options);

    private const string CatererLiquorCountSql =
        "SELECT count(*) FROM \"ComplianceRules\" cr JOIN \"ComplianceTemplates\" ct ON ct.\"Id\" = cr.\"ComplianceTemplateId\" " +
        "WHERE ct.\"IsSystemTemplate\" = true AND ct.\"Name\" = 'Caterer' " +
        "AND cr.\"DocumentType\" = 'coi' AND cr.\"FieldName\" = 'liquor_liability_limit' AND cr.\"Operator\" = 'min_value'";

    private const string SecurityGlCountSql =
        "SELECT count(*) FROM \"ComplianceRules\" cr JOIN \"ComplianceTemplates\" ct ON ct.\"Id\" = cr.\"ComplianceTemplateId\" " +
        "WHERE ct.\"IsSystemTemplate\" = true AND ct.\"Name\" = 'Security Service' " +
        "AND cr.\"DocumentType\" = 'coi' AND cr.\"FieldName\" = 'general_liability_limit' AND cr.\"Operator\" = 'min_value'";

    private const string CatererLiquorExpectedValueSql =
        "SELECT cr.\"ExpectedValue\" FROM \"ComplianceRules\" cr JOIN \"ComplianceTemplates\" ct ON ct.\"Id\" = cr.\"ComplianceTemplateId\" " +
        "WHERE ct.\"IsSystemTemplate\" = true AND ct.\"Name\" = 'Caterer' AND cr.\"FieldName\" = 'liquor_liability_limit'";

    private static async Task ExecAsync(NpgsqlConnection conn, NpgsqlTransaction tx, string sql)
    {
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task<long> ScalarAsync(NpgsqlConnection conn, NpgsqlTransaction tx, string sql)
    {
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        return (long)(await cmd.ExecuteScalarAsync())!;
    }

    private static async Task<string?> ScalarStringAsync(NpgsqlConnection conn, NpgsqlTransaction tx, string sql)
    {
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        return (await cmd.ExecuteScalarAsync()) as string;
    }
}
