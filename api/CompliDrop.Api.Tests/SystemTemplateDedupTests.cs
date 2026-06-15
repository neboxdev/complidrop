using CompliDrop.Api.Data;
using CompliDrop.Api.Data.Seed;
using CompliDrop.Api.Entities;
using CompliDrop.Api.Tests.TestHelpers;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace CompliDrop.Api.Tests;

/// <summary>
/// Tests the #251 fix: the partial unique index that stops a future seed/rename mismatch from
/// duplicating a system template, and the one-time dedupe SQL that cleans up the existing prod
/// duplicates. The dedupe is exercised under a rolled-back transaction that first drops the guard
/// index (Postgres DDL is transactional), since the live index otherwise forbids re-creating the
/// duplicate state.
/// </summary>
public sealed class SystemTemplateDedupTests(IntegrationTestFixture fixture) : IntegrationTestBase(fixture)
{
    [Fact]
    public async Task Unique_guard_rejects_a_second_live_system_template_with_the_same_name()
    {
        // The seed installs "Caterer" at boot. A second live IsSystemTemplate row with that name must
        // fail loudly at the DB rather than silently doubling the suggested-checklists list.
        await using var db = CreateSystemDb();
        db.ComplianceTemplates.Add(new ComplianceTemplate
        {
            Id = Guid.NewGuid(),
            OrganizationId = ComplianceTemplateSeed.SystemOrgId,
            Name = "Caterer",
            IsSystemTemplate = true,
            CreatedAt = DateTime.UtcNow
        });

        var act = async () => await db.SaveChangesAsync();

        var ex = (await act.Should().ThrowAsync<DbUpdateException>()).Which;
        ex.InnerException.Should().BeOfType<PostgresException>()
            .Which.SqlState.Should().Be(PostgresErrorCodes.UniqueViolation);
    }

    [Fact]
    public async Task Org_templates_may_reuse_a_system_template_name()
    {
        // The guard is scoped to IsSystemTemplate — a tenant naming their own template "Caterer" is
        // legitimate and must not collide with the seeded system one.
        await using var db = CreateSystemDb();
        var orgId = Guid.NewGuid();
        db.Organizations.Add(new Organization { Id = orgId, Name = "Org", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow });
        db.ComplianceTemplates.Add(new ComplianceTemplate
        {
            Id = Guid.NewGuid(), OrganizationId = orgId, Name = "Caterer", IsSystemTemplate = false, CreatedAt = DateTime.UtcNow
        });

        var act = async () => await db.SaveChangesAsync();

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task Dedupe_keeps_one_survivor_repoints_vendors_and_removes_orphaned_rules_and_checks()
    {
        await using var conn = new NpgsqlConnection(Fixture.ConnectionString);
        await conn.OpenAsync();
        await using var tx = await conn.BeginTransactionAsync();
        try
        {
            // Drop the guard so the pre-fix duplicate state can be recreated; the rollback restores it.
            await ExecAsync(conn, tx, "DROP INDEX \"IX_ComplianceTemplates_Name_SystemUnique\"");

            var orgId = Guid.NewGuid();
            var survivorId = Guid.NewGuid();
            var dupeId = Guid.NewGuid();
            var survivorRuleId = Guid.NewGuid();
            var dupeRuleId = Guid.NewGuid();
            var vendorA = Guid.NewGuid(); // references the survivor
            var vendorB = Guid.NewGuid(); // references the dupe, owns a document evaluated against the dupe rule
            var docId = Guid.NewGuid();
            const string name = "ZZ-Dedupe-Test";

            await using (var db = new SystemDbContext(
                new DbContextOptionsBuilder<SystemDbContext>().UseNpgsql(conn).Options))
            {
                await db.Database.UseTransactionAsync(tx);
                db.Organizations.Add(new Organization { Id = orgId, Name = "DedupeOrg", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow });
                // Survivor created EARLIER so the CreatedAt tie-break (equal vendor ref counts) picks it.
                db.ComplianceTemplates.Add(new ComplianceTemplate { Id = survivorId, OrganizationId = ComplianceTemplateSeed.SystemOrgId, Name = name, IsSystemTemplate = true, CreatedAt = DateTime.UtcNow.AddMinutes(-10) });
                db.ComplianceTemplates.Add(new ComplianceTemplate { Id = dupeId, OrganizationId = ComplianceTemplateSeed.SystemOrgId, Name = name, IsSystemTemplate = true, CreatedAt = DateTime.UtcNow });
                db.ComplianceRules.Add(new ComplianceRule { Id = survivorRuleId, ComplianceTemplateId = survivorId, DocumentType = "coi", Operator = "required", SortOrder = 0 });
                db.ComplianceRules.Add(new ComplianceRule { Id = dupeRuleId, ComplianceTemplateId = dupeId, DocumentType = "coi", Operator = "required", SortOrder = 0 });
                db.Vendors.Add(new Vendor { Id = vendorA, OrganizationId = orgId, Name = "A", ComplianceTemplateId = survivorId, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow });
                db.Vendors.Add(new Vendor { Id = vendorB, OrganizationId = orgId, Name = "B", ComplianceTemplateId = dupeId, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow });
                db.Documents.Add(new Document { Id = docId, OrganizationId = orgId, VendorId = vendorB, OriginalFileName = "d.pdf", BlobStorageUrl = "blob://d", FileSizeBytes = 1, ContentType = "application/pdf", DocumentType = "coi", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow });
                db.ComplianceChecks.Add(new ComplianceCheck { Id = Guid.NewGuid(), DocumentId = docId, ComplianceRuleId = dupeRuleId, IsPassed = false, CheckedAt = DateTime.UtcNow });
                await db.SaveChangesAsync();
            }

            await ExecAsync(conn, tx, SystemTemplateDedup.DedupeSql);

            // Exactly one template named ZZ-Dedupe-Test remains, and it's the survivor.
            (await ScalarAsync(conn, tx, $"SELECT count(*) FROM \"ComplianceTemplates\" WHERE \"Name\" = '{name}'"))
                .Should().Be(1);
            (await ScalarAsync(conn, tx, $"SELECT count(*) FROM \"ComplianceTemplates\" WHERE \"Id\" = '{survivorId}'"))
                .Should().Be(1, "the earlier-created survivor is kept");
            // Both vendors now point at the survivor.
            (await ScalarAsync(conn, tx, $"SELECT count(*) FROM \"Vendors\" WHERE \"ComplianceTemplateId\" = '{survivorId}'"))
                .Should().Be(2, "the dupe's vendor is repointed onto the survivor");
            // The dropped copy's rule is gone (cascade) and its orphaned check was removed first (FK RESTRICT).
            (await ScalarAsync(conn, tx, $"SELECT count(*) FROM \"ComplianceRules\" WHERE \"Id\" = '{dupeRuleId}'")).Should().Be(0);
            (await ScalarAsync(conn, tx, $"SELECT count(*) FROM \"ComplianceRules\" WHERE \"Id\" = '{survivorRuleId}'")).Should().Be(1);
            (await ScalarAsync(conn, tx, $"SELECT count(*) FROM \"ComplianceChecks\" WHERE \"DocumentId\" = '{docId}'")).Should().Be(0);
            // The document itself survives — only its stale check rows were cleared.
            (await ScalarAsync(conn, tx, $"SELECT count(*) FROM \"Documents\" WHERE \"Id\" = '{docId}'")).Should().Be(1);
        }
        finally
        {
            await tx.RollbackAsync(); // restores the dropped index and undoes the seed
        }
    }

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
}
