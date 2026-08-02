using System.Reflection;
using CompliDrop.Api.Entities;
using CompliDrop.Api.Migrations;
using CompliDrop.Api.Tests.TestHelpers;
using FluentAssertions;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Npgsql;

namespace CompliDrop.Api.Tests.Migrations;

/// <summary>
/// Pins the #459 / ADR 0052 migration that gives extraction TRUST its own column.
///
/// Two angles, the RenameSystemTemplatesToVenueTypes shape:
///   1. A pure operations test (no DB) that the migration is ADDITIVE ONLY — it adds a column and writes
///      that brand-new column, and touches nothing pre-existing. Migrations auto-apply at boot and fail
///      fast (ADR 0016), against a schema an older container may still be serving, so "additive" is a
///      safety property of the deploy, not a style preference.
///   2. A DB test that the backfill reproduces the PRE-#459 coverage-exclusion predicate exactly, over the
///      whole status x IsManuallyVerified grid — the one thing the change may not regress. It replays the
///      migration's OWN SQL, read back out of the operation, so the test cannot drift from the migration
///      the way a hand-copied statement would.
/// </summary>
public sealed class AddDocumentExtractionTrustGuardTests
{
    private static List<MigrationOperation> UpOperations()
    {
        var migration = new AddDocumentExtractionTrust();
        var builder = new MigrationBuilder(activeProvider: null);
        typeof(AddDocumentExtractionTrust)
            .GetMethod("Up", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(migration, [builder]);
        return [.. builder.Operations];
    }

    [Fact]
    public void Up_adds_one_column_with_a_readable_store_default()
    {
        var add = UpOperations().OfType<AddColumnOperation>().Should().ContainSingle().Subject;

        add.Table.Should().Be("Documents");
        add.Name.Should().Be(nameof(Document.ExtractionTrust));
        add.IsNullable.Should().BeFalse();
        // The store default is load-bearing: during a deploy overlap the OLD container still INSERTs
        // Documents without this column. EF's implicit default for a required text column is "", which no
        // enum member can read — every such row would then throw on materialization.
        add.DefaultValue.Should().Be(nameof(ExtractionTrust.Trusted));
    }

    [Fact]
    public void Up_is_additive_only()
    {
        var ops = UpOperations();

        // Nothing destructive, and nothing that narrows or retypes a populated column. Any of these in a
        // boot-applied migration is a production risk of a different order than adding a column.
        Type[] forbidden =
        [
            typeof(DropColumnOperation), typeof(DropTableOperation), typeof(DropIndexOperation),
            typeof(AlterColumnOperation), typeof(RenameColumnOperation), typeof(RenameTableOperation),
        ];
        ops.Select(o => o.GetType()).Should().NotIntersectWith(forbidden);

        // The only data statement writes the brand-new column and reads two values already on the same
        // row. A SET clause naming anything else would be mutating pre-existing data.
        var sql = ops.OfType<SqlOperation>().Should().ContainSingle().Subject.Sql;
        sql.Should().Contain("UPDATE \"Documents\"");
        sql.Should().Contain("SET \"ExtractionTrust\" = 'Distrusted'");
        sql.Should().NotContain("DELETE");
        sql.Should().NotContain("DROP");
        // One assignment only: the migration must not smuggle a second column into the SET clause.
        sql.Split("SET ")[1].Split("WHERE")[0].Should().NotContain(",");
    }
}

public sealed class AddDocumentExtractionTrustBackfillTests(IntegrationTestFixture fixture)
    : IntegrationTestBase(fixture)
{
    /// <summary>The pre-#459 read-time exclusion predicate, spelled out here ONCE as the specification the
    /// backfill has to reproduce. Deliberately a copy of the code that no longer exists rather than a call
    /// into anything current: the point of the test is that the new stored state agrees with the OLD
    /// behaviour, so re-deriving it from today's code would make the assertion vacuous.</summary>
    private static bool WasExcludedBeforeTheTrustColumn(ExtractionStatus status, bool isManuallyVerified) =>
        status == ExtractionStatus.ManualRequired
        || (status == ExtractionStatus.Failed && !isManuallyVerified);

    [Fact]
    public async Task The_backfill_reproduces_the_pre_459_exclusion_predicate_over_the_whole_grid()
    {
        // Every (status, IsManuallyVerified) combination a Document can be in when the migration lands.
        var grid = (from status in Enum.GetValues<ExtractionStatus>()
                    from verified in new[] { false, true }
                    select (status, verified)).ToList();

        var orgId = Guid.NewGuid();
        var seeded = new List<(Guid Id, ExtractionStatus Status, bool Verified)>();

        await using var conn = new NpgsqlConnection(Fixture.ConnectionString);
        await conn.OpenAsync();
        await using var tx = await conn.BeginTransactionAsync();
        try
        {
            await ExecAsync(conn, tx,
                $"""
                 INSERT INTO "Organizations" ("Id", "Name", "TimeZone", "CreatedAt", "UpdatedAt")
                 VALUES ('{orgId}', 'Backfill Org', 'America/New_York', now(), now());
                 """);

            foreach (var (status, verified) in grid)
            {
                var id = Guid.NewGuid();
                seeded.Add((id, status, verified));
                // Inserted WITHOUT "ExtractionTrust", so each row takes the column's store default —
                // exactly the state every pre-migration row is in the instant the column appears.
                await ExecAsync(conn, tx,
                    $"""
                     INSERT INTO "Documents"
                       ("Id", "OrganizationId", "OriginalFileName", "BlobStorageUrl", "FileSizeBytes",
                        "ContentType", "DocumentType", "ExtractionStatus", "ComplianceStatus",
                        "IsManuallyVerified", "IsSample", "ProcessingAttempts", "FailedAttempts",
                        "CreatedAt", "UpdatedAt")
                     VALUES
                       ('{id}', '{orgId}', 'doc.pdf', 'memory://x', 1,
                        'application/pdf', 'coi', '{status}', 'Compliant',
                        {(verified ? "true" : "false")}, false, 0, 0,
                        now(), now());
                     """);
            }

            (await ScalarStringAsync(conn, tx,
                    $"SELECT \"ExtractionTrust\" FROM \"Documents\" WHERE \"Id\" = '{seeded[0].Id}'"))
                .Should().Be(nameof(ExtractionTrust.Trusted),
                    "precondition: a row written without the column takes the store default");

            await ExecAsync(conn, tx, BackfillSql());

            foreach (var (id, status, verified) in seeded)
            {
                var expected = WasExcludedBeforeTheTrustColumn(status, verified)
                    ? ExtractionTrust.Distrusted
                    : ExtractionTrust.Trusted;
                (await ScalarStringAsync(conn, tx, $"SELECT \"ExtractionTrust\" FROM \"Documents\" WHERE \"Id\" = '{id}'"))
                    .Should().Be(expected.ToString(),
                        $"a {status} document with IsManuallyVerified={verified} was "
                        + (expected == ExtractionTrust.Distrusted ? "excluded" : "in-force-eligible")
                        + " before the trust column, and the seed may not change that");
            }
        }
        finally
        {
            await tx.RollbackAsync();
        }
    }

    /// <summary>The migration's own backfill statement, read out of the operation rather than re-typed —
    /// so editing the migration without editing this test cannot leave the assertion passing against a
    /// statement nothing runs.</summary>
    private static string BackfillSql()
    {
        var migration = new AddDocumentExtractionTrust();
        var builder = new MigrationBuilder(activeProvider: null);
        typeof(AddDocumentExtractionTrust)
            .GetMethod("Up", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(migration, [builder]);
        return builder.Operations.OfType<SqlOperation>().Single().Sql;
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
