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
    private string? _priorDbConnEnv;

    public CustomWebApplicationFactory Factory { get; private set; } = null!;

    public string ConnectionString => _container.GetConnectionString();

    public async Task InitializeAsync()
    {
        await _container.StartAsync();

        // DbContextFactory (used by MultiTenancyTests) reads this env var first; the API host
        // gets the same value via CustomWebApplicationFactory's in-memory config. Capture any
        // prior value so DisposeAsync can restore it — leaving a dead-container connection string
        // (or clobbering a real one) in process-global state would be a footgun for later code.
        _priorDbConnEnv = Environment.GetEnvironmentVariable("ConnectionStrings__Database");
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

    /// <summary>Wipes all data, re-seeds system compliance templates, and clears any in-memory test doubles.</summary>
    public async Task ResetAsync()
    {
        await _respawner.ResetAsync(_respawnConnection);

        await using var sys = new SystemDbContext(
            new DbContextOptionsBuilder<SystemDbContext>().UseNpgsql(ConnectionString).Options);
        await ComplianceTemplateSeed.EnsureAsync(sys);

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
        // Each disposal is independent — a throw in one must not skip the others, and the env-var
        // restore is the most important: leaving the container's (now-dead) conn string in
        // process-global state breaks any subsequent test that reads ConnectionStrings__Database.
        // try/finally chains let the first exception surface while still running the rest.
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
                try
                {
                    await _container.DisposeAsync();
                }
                finally
                {
                    // Restore the process-global env var we mutated in InitializeAsync.
                    Environment.SetEnvironmentVariable("ConnectionStrings__Database", _priorDbConnEnv);
                }
            }
        }
    }
}
