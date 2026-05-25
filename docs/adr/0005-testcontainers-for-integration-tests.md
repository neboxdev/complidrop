# 0005. Testcontainers + Respawn + WebApplicationFactory for API integration tests

- **Status:** accepted
- **Date:** 2026-05-25
- **Deciders:** Ruben G.

## Context

Integration tests at the API boundary (HTTP in, real Postgres out) need a database. The MVP test surface — auth flows, multi-tenant query filters, the vendor portal, the Stripe and Resend webhook signature paths, the extraction worker, and the reminder worker — only catches the bugs we care about (filter bypass, signature replay, idempotency, audit-log shape, transaction boundaries) if the test hits real Postgres. Mocking `DbContext` defeats the point — the bugs live in EF's tracking, FK enforcement, and the audit interceptor's interaction with both.

Pre-ticket #2 the project ran integration tests against either a developer-local Postgres (via a `ConnectionStrings__Database` env var) or skipped the whole suite in CI if the env var was unset. That worked in the short term but had two real problems:

1. **CI silently skipped tests** when the secret was unset, so a PR could be green with zero integration coverage.
2. **Developer setup was non-trivial** — fresh clones could not run `dotnet test` end-to-end without provisioning a Postgres instance and copying its connection string.

We also needed cross-test isolation: each `[Fact]` should start with a known DB state without paying the cost of re-running migrations or re-creating the container. Reseeding all tables between tests via `DELETE FROM` per-table is brittle (FK order) and slow at scale.

## Decision

Adopt **Testcontainers** for the throwaway Postgres instance, **Respawn** for between-test cleanup, and **`WebApplicationFactory<Program>`** for hosting the API under test. Concretely:

- **One container per test collection** — `IntegrationTestFixture` (an `IAsyncLifetime` + xUnit `ICollectionFixture`) starts a `postgres:17-alpine` container in `InitializeAsync`, applies EF Core migrations against it, boots `CustomWebApplicationFactory`, and seeds the system compliance templates. The collection is named `"integration"` and is the only place where this fixture lives. All HTTP-level integration tests inherit `IntegrationTestBase` (which declares `[Collection("integration")]`) so they share that one container.
- **Serial execution within the collection** — xUnit runs all tests in one collection serially by default. We rely on this; per-test isolation comes from Respawn, not from parallelism.
- **`CustomWebApplicationFactory`** overrides configuration in-memory (test DB connection string, deterministic `Jwt:Secret`, `Cookies:Secure=false`, `RateLimiting:Enabled=false`, webhook secrets), removes the DB-polling background workers (`ExtractionWorker`, `ReminderBackgroundService`) so tests drive them deterministically, and swaps the external-IO services (`IBlobStorageService`, `IEmailService`, `IOcrService`, `IExtractionClient`) for in-memory fakes registered as both the concrete type (for handle access) and the interface.
- **Respawn for cleanup** — `IntegrationTestFixture.ResetAsync` calls `Respawner.ResetAsync` against the container, then re-seeds system compliance templates and clears the in-memory fake state. `IntegrationTestBase.InitializeAsync` invokes it before every test. The `__EFMigrationsHistory` table is in `TablesToIgnore` so the schema and migration record survive.
- **CI dependency on Docker** — GitHub Actions runs the integration job on `ubuntu-latest` which has Docker pre-installed; Windows developers run Docker Desktop. The CI workflow no longer gates the integration suite on a secret; if Testcontainers can't reach Docker, the job fails (which is what we want).

The API host receives the container connection string via `CustomWebApplicationFactory`'s in-memory configuration override; the fixture itself never mutates process-global state (an earlier iteration published `ConnectionStrings__Database` to support a legacy `DbContextFactory`-based test path, removed in ticket #13).

## Consequences

### Positive

- **Single-step `dotnet test`** — every developer with Docker installed can clone the repo and run the full suite without provisioning a DB or copying secrets.
- **CI has no "skipped because secret missing" failure mode** — the integration job either runs or fails loudly.
- **Per-test isolation is cheap** — Respawn's strategy (computing a delete graph against actual FK metadata) is faster than per-test container teardown or per-test `dotnet ef database update`.
- **Real-Postgres semantics** — global query filters, `FOR UPDATE SKIP LOCKED`, unique-index enforcement, and the audit interceptor all behave exactly as in production, because they *are* in production code paths.
- **`Program.cs` runs unmodified** — `WebApplicationFactory<Program>` hosts the actual entry-point. Configuration is overridden in-memory but composition root, middleware, endpoint mapping, and startup seed are exercised.

### Negative

- **Docker is a hard prerequisite** — the suite cannot run on a machine without a Docker daemon. This is documented in `README` and the API project's onboarding notes; CI provides it for free.
- **First test in a fresh process is slow** — container start + migrations + host boot is ~3–5 seconds. Once warm, individual tests run in tens of ms. We accept the cold-start cost because it only hits once per `dotnet test` invocation.

### Neutral

- **Pure-unit tests stay parallel** — only `[Collection("integration")]` tests serialize. Tests under `CompliDrop.Api.Tests` that don't touch the fixture (e.g. `PasswordHasherTests`, `TokenServiceTests`, `SvixWebhookVerifierTests`) parallelise normally.
- **System-template seed survives across resets via FK cascade** — `Organizations`, `ComplianceTemplates`, and `ComplianceRules` are in Respawn's `TablesToIgnore`. After Respawn's pass, a single `DELETE FROM "Organizations" WHERE "Id" <> sysOrgId` runs in `ResetAsync`; the `ON DELETE CASCADE` FKs from `ComplianceTemplate → Organization` and `ComplianceRule → ComplianceTemplate` wipe any tenant templates and rules without touching the system rows. The seed only runs once (at host boot in `InitializeAsync`); subsequent resets skip it entirely. Saves ~50ms per integration test (was a documented follow-up; landed in the same PR as the ADR after a second pass on the harness).
- **`MultiTenancyTests` was originally pre-fixture** and self-managed cleanup against a now-removed `DbContextFactory` helper. Ticket #13 migrated it onto `IntegrationTestBase` so all integration tests share one reset strategy.

## Alternatives considered

### Option A — keep the developer-local Postgres + CI secret

Continue with `ConnectionStrings__Database` pointing at a developer-provided DB; CI uses a service container. Rejected: the CI path silently skipped if the secret was missing (the real bug that surfaced in #2), and the developer-local path was a non-trivial setup hurdle for a one-developer project that may eventually onboard contributors. The skip-gate was the original sin; ripping it out forced us to commit to a self-provisioned DB strategy either way.

### Option B — EF Core in-memory provider

Drop Postgres in tests, use the in-memory provider. Rejected: the in-memory provider does not enforce FK constraints, does not implement `FOR UPDATE SKIP LOCKED`, does not support raw SQL the codebase uses (e.g. the extraction-queue advisory locks), and has different `DateTime` handling for `timestamptz`. Every one of those gaps is exactly where production bugs hide. The integration suite would be worth less than the time it takes to run.

### Option C — Pgsql daemon spawned in `IntegrationTestFixture`

Skip Docker, ship the Postgres binaries with the test project or `apt install postgresql` on CI. Rejected: per-OS install + path complexity (Windows binaries vs Linux), no per-job isolation between parallel CI runs without manually managing data dirs and ports, no clean teardown story. Testcontainers solves all of this with one `await container.StartAsync()`.

### Option D — Per-test container

Spawn a fresh Postgres container per `[Fact]`. Rejected: ~3s cold-start per test × dozens of tests = minutes per run. The whole point of per-collection sharing + Respawn is to keep cold-start a single tax.

### Option E — Per-test transaction rollback

Wrap each test in a `BEGIN; …; ROLLBACK;` so changes never commit. Rejected: the API host opens its own DB connections (not under test control); a wrapping transaction on a side connection wouldn't isolate those. EF's change tracker also gets confused if the test code uses a different `DbContext` than the host. Respawn's wipe-and-reseed is the conventional approach for this exact pattern.

## References

- Tickets: [#2](https://github.com/neboxdev/complidrop/issues/2) (harness foundation), [#13](https://github.com/neboxdev/complidrop/issues/13) (this ADR + harness polish)
- Fixture: `api/CompliDrop.Api.Tests/TestHelpers/IntegrationTestFixture.cs`
- Base class: `api/CompliDrop.Api.Tests/TestHelpers/IntegrationTestBase.cs`
- Host factory: `api/CompliDrop.Api.Tests/TestHelpers/CustomWebApplicationFactory.cs`
- Collection definition: `api/CompliDrop.Api.Tests/IntegrationCollection.cs`
- External: Testcontainers for .NET (https://dotnet.testcontainers.org), Respawn (https://github.com/jbogard/Respawn)
