using CompliDrop.Api.Data;
using CompliDrop.Api.Data.Seed;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Respawn;
using Respawn.Graph;
using Testcontainers.PostgreSql;

namespace CompliDrop.Api.Tests.TestHelpers;

/// <summary>
/// Shared per-collection fixture. Starts a throwaway Postgres container, applies EF
/// migrations, boots the API host once, and provides a Respawn-based reset between tests.
/// The container connection string is also published to the <c>ConnectionStrings__Database</c>
/// environment variable so the existing <see cref="DbContextFactory"/>-based tests transparently
/// run against the same container.
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

        // DbContextFactory (used by MultiTenancyTests) reads this env var first; the API host
        // gets the same value via CustomWebApplicationFactory's in-memory config.
        Environment.SetEnvironmentVariable("ConnectionStrings__Database", ConnectionString);

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

        // Respawn: wipe all data between tests, but keep the schema and migration history.
        _respawnConnection = new NpgsqlConnection(ConnectionString);
        await _respawnConnection.OpenAsync();
        _respawner = await Respawner.CreateAsync(_respawnConnection, new RespawnerOptions
        {
            DbAdapter = DbAdapter.Postgres,
            SchemasToInclude = ["public"],
            TablesToIgnore = [new Table("__EFMigrationsHistory")],
        });
    }

    /// <summary>Wipes all data and re-seeds the system compliance templates.</summary>
    public async Task ResetAsync()
    {
        await _respawner.ResetAsync(_respawnConnection);

        await using var sys = new SystemDbContext(
            new DbContextOptionsBuilder<SystemDbContext>().UseNpgsql(ConnectionString).Options);
        await ComplianceTemplateSeed.EnsureAsync(sys);
    }

    public async Task DisposeAsync()
    {
        if (_respawnConnection is not null)
            await _respawnConnection.DisposeAsync();
        if (Factory is not null)
            await Factory.DisposeAsync();
        await _container.DisposeAsync();
    }
}
