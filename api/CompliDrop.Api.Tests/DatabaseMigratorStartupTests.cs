using System.Net;
using CompliDrop.Api.Data;
using CompliDrop.Api.Tests.TestHelpers;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Testcontainers.PostgreSql;

namespace CompliDrop.Api.Tests;

/// <summary>
/// Proves the <see cref="DatabaseMigrator"/> is actually WIRED into the host's startup
/// (<c>Program.cs</c>) — not just correct in isolation. The helper-level
/// <see cref="DatabaseMigratorIntegrationTests"/> can't catch a mis-wire (wrong DbContext, a config
/// key typo, or a stray try/catch swallowing the abort) because the shared
/// <see cref="IntegrationTestFixture"/> pre-migrates its container before booting, so the host's
/// startup call always hits the no-op branch.
///
/// These tests boot the REAL host (via <see cref="CustomWebApplicationFactory"/>) against a fresh,
/// un-migrated container so the wired startup path does real work. Each test owns its own container
/// (xUnit constructs the class per test method).
/// </summary>
public sealed class DatabaseMigratorStartupTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _container =
        new PostgreSqlBuilder("postgres:17-alpine").Build();

    public Task InitializeAsync() => _container.StartAsync();

    public Task DisposeAsync() => _container.DisposeAsync().AsTask();

    [Fact]
    public void Host_aborts_boot_on_schema_drift_when_autoMigrate_is_off()
    {
        // AutoMigrate off + an un-migrated DB: Program.cs must invoke the drift guard and let it
        // abort startup. If the wiring were wrong (guard not called, throw swallowed, wrong config
        // key read so it defaulted on and migrated) the host would boot instead of throwing.
        using var factory = new CustomWebApplicationFactory(
            _container.GetConnectionString(),
            new Dictionary<string, string?> { ["Database:AutoMigrate"] = "false" });

        var act = () => factory.CreateClient();

        act.Should().Throw<Exception>("startup must abort rather than serve a stale schema")
            .Which.ToString().Should().Contain("schema drift");
    }

    [Fact]
    public async Task Host_auto_migrates_a_fresh_database_on_boot_and_serves_ready()
    {
        // Default config (AutoMigrate unset → on): booting the host against a fresh container must
        // apply migrations as part of startup, then serve. Proves the wired apply-on-boot path.
        using var factory = new CustomWebApplicationFactory(_container.GetConnectionString());

        using var client = factory.CreateClient();
        var ready = await client.GetAsync("/health/ready");
        ready.StatusCode.Should().Be(HttpStatusCode.OK, "the host must boot and report ready after auto-migrating");

        // And the schema was actually brought current by the boot — nothing pending, and the full
        // User entity (the columns that 500'd login in #226) materializes.
        await using var db = new AppDbContext(
            new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(_container.GetConnectionString()).Options,
            new FakeCurrentUser());
        (await db.Database.GetPendingMigrationsAsync()).Should().BeEmpty("startup auto-migrate must apply every migration");
        (await db.Users.FirstOrDefaultAsync()).Should().BeNull();
    }
}
