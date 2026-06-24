# 0034. Dev/prod environment isolation, surfaced by a redacted startup banner

- **Status:** accepted
- **Date:** 2026-06-24
- **Deciders:** Ruben G.

## Context

For most of the project's life the local dev environment was **the production environment** (#271).
The local `user-secrets` `ConnectionStrings:Database` resolved to the same Neon database Railway prod
serves, and `AzureStorage:ConnectionString` to the same `complidropstorage` account. The
Organizations table held the founder's prod demo org, real signups, and every local test org side by
side. The danger was not theoretical and not one bug — it was a whole class:

- **A local `dotnet run` auto-migrates prod.** `Database:AutoMigrate` defaults on
  ([ADR 0016](0016-apply-ef-migrations-on-startup.md)), so booting a branch with a pending migration
  applies it to the *production* schema before any deploy review.
- **The Railway `ExtractionWorker` is a permanent second queue consumer.** Local pipeline experiments
  are unreproducible — prod claims `Pending` docs within seconds even when the local API is dead
  (`FOR UPDATE SKIP LOCKED`). This was the root cause of the retracted #243/#259 "zombie reclaim"
  observations.
- **Local test data lands in prod**, and reminder-eligible test rows get **real emails** from prod's
  Resend — against a dataset that is a clone of real vendor/user addresses.

The founder originally deferred #271 (pre-market, solo, 0 users — testing in prod was an accepted
mode) with explicit re-arm triggers: the first cold-email batch, the first external org, or any
pipeline experiment. With cold-email outreach approaching, the trigger fired.

The data-bearing secrets were rotated out of band first: `ConnectionStrings:Database` → a dedicated
**Neon `dev` branch** (a copy-on-write clone, near-free), `AzureStorage` → **Azurite**, `Resend:ApiKey`
removed (email-silent), Stripe already `sk_test_`. What remained was the *durable* guard: nothing at
boot named which database / storage / Stripe mode / email sender the process had resolved, so the
misconfiguration had been invisible for weeks. Naming the resolved targets is what prevents a
recurrence.

## Decision

A composition-root helper **`StartupEnvironmentBanner`** (sibling to `DatabaseMigrator` and
`RateLimitingGate`) logs a **redacted, one-line summary** of the data-bearing / outward-facing targets
at boot — DB host + database name, blob target (Azurite vs named account), email mode (live/silent),
Stripe mode (test/live) — invoked in the `Program.cs` startup scope **immediately before**
`DatabaseMigrator.MigrateAndGuardAsync`, so the DB host is named on the line directly above
"Applying N migrations".

Three properties define it:

1. **The banner is INFO in every environment.** In prod it is an operational sanity line (confirms
   which Neon branch / account / Stripe mode prod serves); in dev it is the at-a-glance "am I pointed
   at the right place?" check.
2. **The misconfig guard is a Development-only WARNING, not a boot abort.** In Development the banner
   additionally logs a loud warning for each target that looks like a **live/production** resource: a
   `sk_live_`/`rk_live_` Stripe key, a present `Resend:ApiKey`, or a real (non-Azurite) Azure account.
   Those same values are *correct* in prod, so the warning is gated on `IsDevelopment()`. It **warns
   and continues** — deliberately pointing local at a prod resource for a one-off is a legitimate,
   founder-sanctioned mode, and a hard fail would be hostile to it. This mirrors `RateLimitingGate`'s
   force-on-but-don't-crash posture.
3. **No secret ever reaches a log line.** The banner prints hostnames, account names, and key *modes*
   only — never the DB password, storage account key, or any API key. The DB host is read via
   `NpgsqlConnectionStringBuilder` (host + database properties only); the blob account name via a
   hand-rolled segment scan that can only ever return `AccountName` (never `AccountKey`/SAS); Stripe
   by key *prefix*; email mode via the shared `ResendSettings.WouldSend` predicate (the same gate
   `IEmailService.IsEnabled` uses, so the banner's "LIVE/silent" label can never drift from the actual
   send behaviour). Same invariant as the
   [ADR 0026](0026-environment-aware-required-config-validation.md) validator family, pinned by
   `StartupEnvironmentBannerTests`.

**`Database:AutoMigrate` stays ON in Development.** The original hazard was auto-migrating *prod*. Now
that dev is an isolated, throwaway Neon branch, auto-migrating *it* on boot is desirable (no manual
`dotnet ef database update` step). Setting `AutoMigrate=false` in Development — option 2 of #271's fix
list, which we *considered* — would instead trip the [ADR 0016](0016-apply-ef-migrations-on-startup.md)
drift guard and refuse to boot on any pending migration: friction with no remaining safety benefit
once the database is isolated. The residual "am I about to migrate the right DB?" need is met by the
banner naming the host first.

## Consequences

### Positive
- The #271 silent-shared-environment failure mode cannot recur unnoticed: every boot states the
  resolved targets, and a dev box wired to a live resource warns loudly.
- The banner doubles as a prod operational signal (which Neon branch / Stripe mode is live) at zero
  extra cost.
- Pure, statically-testable helper following the established `RateLimitingGate` / `DatabaseMigrator`
  shape; redaction is unit-pinned.

### Negative
- The banner is a point-in-time snapshot of *configuration*, not a guarantee of *isolation* — it
  reports what the connection string says, and cannot detect a dev branch that was itself created
  against the wrong project. It is a visibility guard, not an enforcement boundary.
- The Development warning is advisory; an operator can ignore it. That is the deliberate trade for not
  blocking a sanctioned local-points-at-prod session.

### Neutral
- No new config surface and no behavior change outside logging. The prod-debris cleanup left by the
  shared-environment era is tracked separately (#228; protect "The Garden Hall").

## Alternatives considered

### Option A — Hard-fail the boot in Development when a target looks live
Rejected: contradicts the founder's accepted "deliberately test against prod for a one-off" mode and
would be hostile to it. A loud warning conveys the same signal without removing the choice.

### Option B — Detect "this is the prod DB" and warn on the database too
Rejected as brittle: dev and prod are both `ep-*.neon.tech` Neon hosts with no reliable distinguishing
marker, and hardcoding the prod host into the repo leaks an infra identifier and rots. Naming the host
so a human (or the banner reader) can tell is the robust boundary; the live-resource warnings cover the
targets that *do* have a reliable live signal (Stripe/Resend/Azure).

### Option C — Documentation only (no code)
Rejected: docs did not prevent the original weeks-long invisibility. The boot line is the durable part;
the docs ([docs/dev-environment.md](../dev-environment.md)) complement it.

## References

- Tickets: [#271](https://github.com/neboxdev/complidrop/issues/271), [#228](https://github.com/neboxdev/complidrop/issues/228) (prod-debris sweep)
- ADRs: [0016](0016-apply-ef-migrations-on-startup.md) (boot-time migration + drift guard), [0026](0026-environment-aware-required-config-validation.md) (never-echo-a-secret config validation)
- Code: `api/CompliDrop.Api/StartupEnvironmentBanner.cs`, `api/CompliDrop.Api/Program.cs`
- Docs: `docs/dev-environment.md`
