using CompliDrop.Api.Data;
using CompliDrop.Api.Data.Seed;
using CompliDrop.Api.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Respawn;
using Respawn.Graph;
using Testcontainers.PostgreSql;

namespace CompliDrop.Api.Tests.TestHelpers;

/// <summary>
/// Shared per-collection fixture. Starts a throwaway Postgres container, applies EF
/// migrations, boots the API host once, and provides a Respawn-based reset between tests.
/// The API host receives the container's connection string via
/// <see cref="CustomWebApplicationFactory"/>'s in-memory configuration override; the harness
/// itself never mutates process-global state.
/// </summary>
public sealed class IntegrationTestFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("postgres:17-alpine")
        .Build();

    private Respawner _respawner = null!;
    private NpgsqlConnection _respawnConnection = null!;

    public CustomWebApplicationFactory Factory { get; private set; } = null!;

    public string ConnectionString => _container.GetConnectionString();

    public async Task InitializeAsync()
    {
        await _container.StartAsync();

        // Migrations belong to AppDbContext — apply them to the fresh container DB.
        await using (var migrate = new AppDbContext(
            new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(ConnectionString).Options,
            new FakeCurrentUser()))
        {
            await migrate.Database.MigrateAsync();
        }

        // Boot the host; its startup seeds the system compliance templates.
        Factory = new CustomWebApplicationFactory(ConnectionString);
        _ = Factory.CreateClient();

        // Respawn: wipe tenant data between tests, but keep schema + migration history + the
        // system seed. Organizations + ComplianceTemplates + ComplianceRules are pulled out of
        // Respawn's wipe set because they hold the seeded system rows (one Organization row at
        // SystemOrgId, five IsSystemTemplate templates with their rules). Respawn cannot
        // row-level ignore, so we keep the whole table and run custom DELETE SQL after the
        // Respawn pass to wipe only the tenant rows — see ResetAsync. Net effect: skip
        // ComplianceTemplateSeed.EnsureAsync's ~50ms-per-test reseed cost while keeping the
        // same final state.
        _respawnConnection = new NpgsqlConnection(ConnectionString);
        await _respawnConnection.OpenAsync();
        _respawner = await Respawner.CreateAsync(_respawnConnection, new RespawnerOptions
        {
            DbAdapter = DbAdapter.Postgres,
            SchemasToInclude = ["public"],
            TablesToIgnore =
            [
                new Table("__EFMigrationsHistory"),
                new Table("Organizations"),
                new Table("ComplianceTemplates"),
                new Table("ComplianceRules"),
            ],
        });
    }

    /// <summary>Wipes tenant data and clears any in-memory test doubles. System seed survives.</summary>
    public async Task ResetAsync()
    {
        await _respawner.ResetAsync(_respawnConnection);

        // Single targeted DELETE — the system org row stays, every tenant org row goes. The
        // ON DELETE CASCADE FK from ComplianceTemplate → Organization (and ComplianceRule →
        // ComplianceTemplate) cascades through, wiping tenant templates + their rules in one
        // round trip without us having to enumerate the dependent tables.
        await using (var wipe = _respawnConnection.CreateCommand())
        {
            wipe.CommandText = """
                DELETE FROM "Organizations" WHERE "Id" <> @sysOrgId;
                """;
            var param = wipe.CreateParameter();
            param.ParameterName = "@sysOrgId";
            param.Value = ComplianceTemplateSeed.SystemOrgId;
            wipe.Parameters.Add(param);
            await wipe.ExecuteNonQueryAsync();
        }

        // The FakeEmailService is a host singleton, so its captured sends persist across tests
        // unless we explicitly reset it. Tests that assert on Sends.Count would otherwise see
        // residue from earlier tests in the same fixture.
        if (Factory.Services.GetService<IEmailService>() is FakeEmailService email)
            email.Reset();

        // Same for the extraction/OCR fakes: clear call counts and restore default behavior so a
        // test that flips ThrowOnExtract or IsEnabled doesn't leak that into the next one.
        Factory.Services.GetService<FakeExtractionClient>()?.Reset();
        Factory.Services.GetService<FakeOcrService>()?.Reset();
    }

    public async Task DisposeAsync()
    {
        // Chain the three disposals so a throw in one still runs the others. C# 'finally' will
        // replace an earlier exception with a later one if both throw — acceptable here because
        // all three are idempotent best-effort cleanups; the test runner only needs to know
        // something went wrong on shutdown, not the precise ordering.
        try
        {
            if (_respawnConnection is not null)
                await _respawnConnection.DisposeAsync();
        }
        finally
        {
            try
            {
                if (Factory is not null)
                    await Factory.DisposeAsync();
            }
            finally
            {
                await _container.DisposeAsync();
            }
        }
    }
}
