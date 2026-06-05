# 0016. Apply EF migrations on API startup (auto-migrate by default), with a boot-time drift guard

- **Status:** accepted
- **Date:** 2026-06-05
- **Deciders:** Ruben G.

## Context

On 2026-06-05 production login (`POST /api/auth/login`) returned HTTP 500 for **every** user — a full auth outage (#226). Root cause was schema drift: the prod Neon database was **9 migrations behind `main`**. A Railway redeploy shipped code whose `User` entity SELECTs columns (`SecurityStamp`, `EmailVerifiedAt`, `HasCompletedOnboarding`) that did not exist in the prod `Users` table, so `db.Users.FirstOrDefaultAsync(...)` in `Login` threw Npgsql `42703 column does not exist` *before* password verification → 500 on every login attempt.

The systemic cause: `Program.cs` had **no** `Database.Migrate()` call and the Railway deploy pipeline had **no** migration step. A deploy shipped new code but never updated the schema, so **every migration-adding merge was a latent outage** that detonated on the next redeploy. Migrations were being applied by hand (`dotnet ef database update` against the prod connection string) — easy to forget, and forgetting is silent until traffic hits the missing column.

This is a single-instance Railway deployment against a managed Neon Postgres. The database always exists ahead of deploy; migrations create/alter the *schema*, not the database.

## Decision

The API **brings its own schema current at boot** before serving traffic, via a small composition-root helper `DatabaseMigrator` (sibling to `RateLimitingGate`), invoked in the startup scope of `Program.cs` immediately before the system-template seed:

- **`Database:AutoMigrate` config flag, default `true`** (and unparseable → `true`). When on, the booting host applies pending migrations itself (`MigrateAsync`). Migrations belong to `AppDbContext` (generated with `--context AppDbContext`), so the helper runs against `appDb.Database`.
- **Fail-fast.** A failed migration throws, aborting boot. On Railway the old container keeps serving while the new one fails its health check — strictly better than a new container serving 500s on a half-applied schema. The migration step is deliberately **not** wrapped in the best-effort try/catch that guards the seed.
- **Boot-time drift guard.** After (or instead of) auto-migrate, if migrations remain pending the helper throws `InvalidOperationException` and the process refuses to start. This makes the *other* legitimate deploy shape safe too: an operator may set `Database:AutoMigrate=false` and run `dotnet ef database update` as an external Railway release step — if that step is skipped or fails, the container detects the drift and refuses to take traffic rather than serving a stale schema.

Net invariant: **once startup completes, the running schema matches the code — or the process never started.**

EF Core 10 takes an exclusive `__EFMigrationsHistory` advisory lock during `Migrate()`, so auto-migrate is safe even if Railway briefly overlaps two containers during a deploy: one applies, the other waits and then sees nothing pending.

## Consequences

### Positive
- A migration-adding merge now updates the prod schema automatically on deploy — no manual step, no silent latent outage. Directly closes the #226 failure mode.
- Deploy + migrate is atomic from the operator's perspective; a bad migration fails the deploy loudly instead of corrupting the serving fleet.
- Supports both deploy shapes (self-migrate *and* external release command) with the same code; the drift guard is the safety net for the latter.
- Most "unfailable" option for a solo operator: the default needs zero pipeline configuration.

### Negative
- A migration that is slow or that fails part-way blocks/aborts boot. At current scale (tiny tables, additive + guarded-idempotent backfills) this is a non-issue; the same migrations applied cleanly by hand. If a future migration is genuinely long-running, prefer the external-release-command shape (`AutoMigrate=false`) so it runs outside the boot path. (The default Npgsql command timeout applies; revisit if a backfill approaches it.)
- The booting instance needs DDL privileges on the database. The existing Neon connection string already has them.
- A DB outage *at boot* now prevents startup (migrate can't connect) rather than starting a degraded host. This is acceptable/desirable — the app can't function without its DB anyway — and matches the fail-fast intent.

### Neutral
- Multi-instance scale-out is not a concern today (Railway runs ~1). If we ever scale past one instance, the EF history lock still serializes migrations; the external-release-command shape remains available as a cleaner separation.
- The startup seed (`ComplianceTemplateSeed`) stays best-effort and unchanged; it now runs after the schema is guaranteed current.

## Alternatives considered

### Option A — Railway release command only (no auto-migrate)
Run `dotnet ef database update` (or a `migrations bundle`) as a Railway pre-deploy/release step. Cleaner separation of "migrate" from "serve", but requires Railway pipeline config plus the EF tooling/bundle in the deploy image, and a misconfigured/missing step reintroduces silent drift. Rejected as the *default* for immediacy and solo-operator robustness — but explicitly **kept available**: `Database:AutoMigrate=false` + the drift guard supports exactly this shape safely.

### Option B — Readiness probe compares applied vs assembly migrations
Have `/health/ready` return 503 when migrations are pending. Rejected as the primary mechanism: with auto-migrate the schema is already current before the app accepts connections, so the check would be redundant in the default path, and changing the semantics of the existing UptimeRobot/Railway probe carries more risk than a startup abort. A startup abort is strictly stronger (the process never serves) and is where the guard lives.

### Option C — Do nothing / keep migrating by hand
The status quo that caused the outage. Rejected.

## References

- Tickets: [#226](https://github.com/neboxdev/complidrop/issues/226)
- ADRs: [0005](0005-testcontainers-for-integration-tests.md) (the integration-test container pattern the drift/apply tests reuse)
- Code: `api/CompliDrop.Api/DatabaseMigrator.cs`, `api/CompliDrop.Api/Program.cs` (startup scope), `api/CompliDrop.Api/RateLimitingGate.cs` (the composition-root-helper precedent)
