using CompliDrop.Api;
using CompliDrop.Api.Data;
using CompliDrop.Api.Tests.TestHelpers;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Testcontainers.PostgreSql;

namespace CompliDrop.Api.Tests;

/// <summary>
/// Schema-touching tests for <see cref="DatabaseMigrator.MigrateAndGuardAsync"/>. These need a
/// fresh, <em>un-migrated</em> database — which the shared <see cref="IntegrationTestFixture"/>
/// can't provide (it pre-applies all migrations before booting the host) — so this class owns its
/// own throwaway Postgres container. xUnit constructs the test class once per test method, so
/// <see cref="InitializeAsync"/> gives every test a pristine container.
/// </summary>
public sealed class DatabaseMigratorIntegrationTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _container =
        new PostgreSqlBuilder("postgres:17-alpine").Build();

    public Task InitializeAsync() => _container.StartAsync();

    public Task DisposeAsync() => _container.DisposeAsync().AsTask();

    // Migrations belong to AppDbContext (generated with --context AppDbContext), so the helper must
    // be exercised through it — SystemDbContext carries no migrations of its own.
    private AppDbContext NewDb() => new(
        new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(_container.GetConnectionString()).Options,
        new FakeCurrentUser());

    [Fact]
    public async Task AutoMigrate_applies_all_pending_migrations_to_a_fresh_database()
    {
        await using var db = NewDb();

        // A fresh container DB has the database but no schema → every assembly migration is pending.
        (await db.Database.GetPendingMigrationsAsync()).Should().NotBeEmpty(
            "a fresh database starts with no applied migrations");

        await DatabaseMigrator.MigrateAndGuardAsync(db.Database, autoMigrate: true, NullLogger.Instance);

        (await db.Database.GetPendingMigrationsAsync()).Should().BeEmpty(
            "auto-migrate must bring the schema fully up to date");
        (await db.Database.GetAppliedMigrationsAsync()).Should().NotBeEmpty();

        // The exact shape that 500'd on 2026-06-05 (#226): materializing the full User entity
        // SELECTs every mapped column (SecurityStamp, EmailVerifiedAt, HasCompletedOnboarding, …).
        // If any column the running code expects is missing, Npgsql throws 42703 here. On an empty,
        // freshly-migrated table it must return null instead.
        var act = async () => await db.Users.FirstOrDefaultAsync();
        await act.Should().NotThrowAsync("the migrated schema must support the current User entity");
        (await db.Users.FirstOrDefaultAsync()).Should().BeNull();
    }

    [Fact]
    public async Task AutoMigrate_is_idempotent_when_schema_already_current()
    {
        await using var db = NewDb();
        await DatabaseMigrator.MigrateAndGuardAsync(db.Database, autoMigrate: true, NullLogger.Instance);
        var appliedAfterFirst = (await db.Database.GetAppliedMigrationsAsync()).ToList();

        // Second run with nothing pending must be a clean no-op (no throw, no schema change).
        var act = async () =>
            await DatabaseMigrator.MigrateAndGuardAsync(db.Database, autoMigrate: true, NullLogger.Instance);
        await act.Should().NotThrowAsync();

        (await db.Database.GetAppliedMigrationsAsync()).Should().BeEquivalentTo(appliedAfterFirst);
    }

    [Fact]
    public async Task Drift_aborts_boot_when_autoMigrate_is_off()
    {
        await using var db = NewDb();

        // AutoMigrate off + an un-migrated DB = the drift that must never reach traffic. The guard
        // throws (aborting boot) instead of letting the stale-schema container serve.
        var act = async () =>
            await DatabaseMigrator.MigrateAndGuardAsync(db.Database, autoMigrate: false, NullLogger.Instance);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*schema drift*");

        // And it must NOT have applied anything as a side effect — drift detection is read-only.
        (await db.Database.GetAppliedMigrationsAsync()).Should().BeEmpty(
            "the drift guard reports drift; it must not silently migrate");
    }

    [Fact]
    public async Task AutoMigrate_off_is_a_no_op_when_schema_is_current()
    {
        await using var db = NewDb();
        // Bring the schema current first (as an external release command would), then boot with
        // auto-migrate disabled: no drift, so the guard must let the host start.
        await DatabaseMigrator.MigrateAndGuardAsync(db.Database, autoMigrate: true, NullLogger.Instance);

        var act = async () =>
            await DatabaseMigrator.MigrateAndGuardAsync(db.Database, autoMigrate: false, NullLogger.Instance);

        await act.Should().NotThrowAsync(
            "a current schema must boot even with auto-migrate off (the release-command deploy shape)");
    }
}
