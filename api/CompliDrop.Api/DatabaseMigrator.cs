using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;

namespace CompliDrop.Api;

/// <summary>
/// Brings the database schema to the assembly's current migration set at boot, or refuses to
/// start if it can't. A composition-root helper (like <see cref="RateLimitingGate"/>), not
/// request-pipeline middleware — kept at the project root so the folder structure doesn't imply
/// otherwise.
/// </summary>
/// <remarks>
/// Exists because of the 2026-06-05 login outage (#226): a deploy shipped code whose <c>User</c>
/// entity SELECTed columns that hadn't been migrated into prod, so every <c>Users</c> query threw
/// <c>42703 column does not exist</c> and login 500'd. <c>Program.cs</c> had no
/// <c>Database.Migrate()</c> and the Railway pipeline had no migration step, so any
/// migration-adding merge was a latent outage that detonated on the next redeploy.
/// <para/>
/// Two supported deploy shapes, both made safe here:
/// <list type="number">
///   <item><b>Auto-migrate (default).</b> <c>Database:AutoMigrate=true</c> → the booting container
///   applies pending migrations itself. EF Core acquires a migration lock (a Postgres advisory
///   lock, via Npgsql) before applying and records applied migrations in <c>__EFMigrationsHistory</c>,
///   so this is safe even if Railway briefly overlaps two containers during a deploy: one applies,
///   the other waits and then sees nothing pending. A bad migration throws → boot aborts
///   (fail-fast) → the old container keeps serving instead of the new one serving 500s.</item>
///   <item><b>External release command.</b> <c>Database:AutoMigrate=false</c> → a Railway
///   pre-deploy step runs <c>dotnet ef database update</c> before the container takes traffic. The
///   <em>drift guard</em> below is the safety net: if that step was skipped/failed and migrations
///   are still pending, the container refuses to start rather than serving a stale schema.</item>
/// </list>
/// Net invariant: once <see cref="MigrateAndGuardAsync"/> returns, the schema is current — or the
/// process never started.
/// </remarks>
public static class DatabaseMigrator
{
    /// <summary>
    /// Whether the booting host should apply pending migrations itself. Reads
    /// <c>Database:AutoMigrate</c>; defaults to <c>true</c> (and treats an unparseable value as
    /// <c>true</c>) so the safe state — "schema kept current automatically" — is the one a missing
    /// or typoed config lands on.
    /// </summary>
    /// <remarks>
    /// Unlike <see cref="RateLimitingGate.ShouldEnable"/>, an explicit <c>false</c> is honored in
    /// every environment — disabling auto-migrate is a legitimate choice when an external release
    /// command owns migrations (deploy shape 2). The boot-time safety net is not "force this on"
    /// but the drift guard in <see cref="MigrateAndGuardAsync"/>, which aborts startup if a
    /// <c>false</c> setting leaves the schema behind. Do not "consistency-fix" this to force-on in
    /// prod — that would break the release-command path.
    /// </remarks>
    public static bool ShouldAutoMigrate(IConfiguration config)
    {
        // Read as a string + bool.TryParse — IConfiguration.GetValue<bool> throws FormatException
        // on values like "yes"/"1"/"on", which would crash startup. Treat unparseable as the safe
        // default (true) so a typo can't silently leave migrations unapplied.
        var raw = config["Database:AutoMigrate"];
        return string.IsNullOrWhiteSpace(raw) || !bool.TryParse(raw, out var parsed) || parsed;
    }

    /// <summary>
    /// Applies pending migrations when <paramref name="autoMigrate"/> is set, and guarantees the
    /// schema is current before returning. Throws (aborting boot) on a failed migration or on
    /// detected drift — never returns against a schema the running code can't query.
    /// </summary>
    /// <remarks>
    /// Migrations belong to <see cref="Data.AppDbContext"/> (generated with
    /// <c>--context AppDbContext</c>); pass <c>appDb.Database</c>. Assumes the database itself
    /// exists (CompliDrop always deploys against an existing Neon database, and tests use a
    /// pre-created container DB) — migrations create the <em>schema</em>, not the database.
    /// </remarks>
    /// <exception cref="InvalidOperationException">
    /// Migrations are pending but <paramref name="autoMigrate"/> is <c>false</c> — i.e. schema
    /// drift the running code would 500 on.
    /// </exception>
    public static async Task MigrateAndGuardAsync(
        DatabaseFacade database,
        bool autoMigrate,
        ILogger logger,
        CancellationToken ct = default)
    {
        var pending = (await database.GetPendingMigrationsAsync(ct)).ToList();

        if (pending.Count == 0)
        {
            logger.LogInformation(
                "Database schema is current — no pending migrations (AutoMigrate={AutoMigrate}).",
                autoMigrate);
            return;
        }

        if (autoMigrate)
        {
            logger.LogInformation(
                "Applying {Count} pending database migration(s): {Migrations}",
                pending.Count, string.Join(", ", pending));

            // Throws on a bad migration — let it propagate so boot aborts (fail-fast) and the old
            // container keeps serving rather than the new one 500'ing on a half-applied schema.
            await database.MigrateAsync(ct);

            logger.LogInformation("Database migrations applied successfully ({Count}).", pending.Count);
            return;
        }

        // AutoMigrate is off but the assembly carries migrations the database doesn't have. This is
        // exactly the drift that 500'd login on 2026-06-05 (#226). Refuse to start so a stale-schema
        // container never takes traffic — the deploy must apply migrations first (release command)
        // or enable Database:AutoMigrate.
        throw new InvalidOperationException(
            $"Database schema drift: {pending.Count} migration(s) in the assembly are not applied to "
            + $"the database ({string.Join(", ", pending)}). Refusing to start so a stale-schema "
            + "container cannot serve traffic. Apply migrations "
            + "(dotnet ef database update --context AppDbContext) or set Database:AutoMigrate=true.");
    }
}
