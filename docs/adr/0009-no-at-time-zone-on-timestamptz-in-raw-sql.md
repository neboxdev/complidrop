# 0009. Raw SQL against `timestamptz` columns uses bare `now()` / `DateTime.UtcNow`, never `AT TIME ZONE`

- **Status:** accepted
- **Date:** 2026-05-25
- **Deciders:** Ruben G.

## Context

`ExtractionWorker.ClaimNextAsync` writes hand-written SQL against the `Documents` table to atomically claim the next document via `UPDATE … FOR UPDATE SKIP LOCKED`. The original implementation looked like:

```sql
UPDATE "Documents"
SET "ProcessingStartedAt" = now() at time zone 'utc',
    "UpdatedAt" = now() at time zone 'utc'
WHERE "Id" = (
  SELECT "Id" FROM "Documents"
  WHERE …
    AND ("ExtractionStatus" = 'Processing'
         AND "ProcessingStartedAt" < now() at time zone 'utc' - interval '5 minutes')
  …
)
```

`ProcessingStartedAt` and `UpdatedAt` are `timestamp with time zone` (Npgsql default for `DateTime`). `now()` is `timestamptz`. But `now() at time zone 'utc'` is `timestamp without time zone` — a naive timestamp representing the UTC wall-clock value of the current moment.

The mismatch forces Postgres to bridge between `timestamptz` and `timestamp without time zone` on every comparison and every write. The bridge uses the **session** `TimeZone` setting:

- **Comparison**: `timestamptz_col < naive_value` casts the naive value back to timestamptz by interpreting it in the session TZ.
- **Write**: assigning a naive value into a timestamptz column interprets the naive value in the session TZ to derive the stored absolute moment.

Today every connection runs with session TZ = UTC (Neon and `postgres:17-alpine` default; Npgsql sets no session TZ). The bridge is a no-op and the SQL behaves correctly. But the dependency is silent — there is no test, contract, or runtime check enforcing it.

What activates the latent bug:

- A connection-string change adding `Options=-c TimeZone=…`.
- A Postgres role default (`ALTER ROLE … SET TIMEZONE = …`).
- An accidental `SET TIME ZONE` from application code.
- A future managed-Postgres provider with a non-UTC default.

The data-corruption modes if any of those happen on a connection that subsequently runs the worker:

- **Read drift**: the 5-minute reclaim threshold shifts by the session offset. Under `America/New_York` (UTC-4/-5) it shifts hours into the future and fresh claims are wrongly reclaimed — two workers extract the same document. Under `Asia/Tokyo` (UTC+9) it shifts hours into the past and stale claims are never reclaimed — zombies stick forever.
- **Write drift**: `ProcessingStartedAt` and `UpdatedAt` are stored hours off from real-now. The zombie threshold then chains off the wrong stored value, compounding the read drift on the next tick.

The fix in [#26](https://github.com/neboxdev/complidrop/issues/26) drops `at time zone 'utc'` and uses bare `now()` (timestamptz) on both sides — comparison and writes become offset-independent.

This ADR generalises that fix from a one-off correction into a project rule, because nothing about the bug is specific to `ExtractionWorker`. Any hand-written raw SQL against any `timestamptz` column in the codebase is exposed to the same failure mode if it reaches for `AT TIME ZONE` for what it imagines is "UTC normalisation".

## Decision

When writing raw SQL (hand-written `UPDATE`/`INSERT`/`SELECT` against `timestamp with time zone` columns):

1. **Read/compare with bare `now()`** — timestamptz on both sides. Never `now() at time zone 'utc'` or any other `AT TIME ZONE` on a timestamptz value when the result feeds back into a timestamptz comparison or assignment.
2. **Write with bare `now()`** for "stamp now" values, or with a `DateTime.UtcNow` parameter from .NET (Npgsql writes it as the absolute timestamptz moment). Never `now() at time zone …` on the right-hand side of a timestamptz assignment.
3. **`AT TIME ZONE` is legitimate** for one purpose only: converting a timestamptz to a wall-clock value in a target zone for *output* (display, calendar-day extraction, a one-shot data migration that backfills a `date` column from a `timestamptz`). The output of `AT TIME ZONE` must not be assigned to or compared against a timestamptz column.

This rule applies to:

- `BackgroundServices/*.cs` raw SQL (today: `ExtractionWorker.ClaimSql`, `ReminderBackgroundService.TryAcquireOrgLockAsync` / `ReleaseOrgLockAsync` — the latter two have no timestamp surface and are clean).
- One-shot data migrations using `migrationBuilder.Sql(...)`.
- Any future `ExecuteSqlRaw` / `ExecuteSqlInterpolated` against timestamptz columns.

It does not apply to EF Core LINQ queries (EF translates `DateTime` parameters correctly via Npgsql's type mapping) or to the migration at `20260525101534_BackfillReminderLogSendDateToOrgLocal.cs`, where `AT TIME ZONE org."TimeZone"` is used to derive a calendar-day `date` value for output — the legitimate use covered by clause (3).

## Consequences

### Positive
- `ExtractionWorker.ClaimSql` is provably session-TZ independent — verified by regression tests under `America/New_York` and `Asia/Tokyo` ([ExtractionWorkerTests.cs](../../api/CompliDrop.Api.Tests/ExtractionWorkerTests.cs) `Claim_under_non_UTC_session_*`).
- The next hand-written raw SQL in the codebase has a written rule to follow rather than rediscovering the bug.
- The rule is small enough to fit on a code-review checklist line: "any new raw SQL touching a `timestamptz` column — does it use `AT TIME ZONE`?".

### Negative
- Adds a project rule a reviewer must remember to check. Mitigated by the rule being narrow (only triggers when both `AT TIME ZONE` and `timestamptz` appear in the same SQL).
- A future contributor reading only `at time zone 'utc'` SQL in some other codebase might assume the conversion is required for "UTC normalisation" and copy the pattern in. The XML doc-comment on `ExtractionWorker.ClaimSql` plus this ADR are the counterweight.

### Neutral
- Does not constrain `DateTimeOffset` or `DateTime.Kind` handling on the CLR side — that is governed by Npgsql's type mapping and is independent of the SQL-text rule.

## Alternatives considered

### Option A — Pin the session TZ to UTC at the connection-pool level

Set `Options=-c TimeZone=UTC` on the connection string, or register an `NpgsqlDataSource` initializer that runs `SET TIME ZONE 'UTC'` on every physical connection.

Rejected because:
- It papers over the dependency rather than removing it. The SQL is still wrong on a fresh connection that hasn't run the initializer yet (race during pool warmup, or a connection borrowed before initializer registration).
- It scopes a defensive pin globally, where the actual fix scopes to the SQL that needs it.
- It does not generalise — a future raw-SQL path with the same bug would still depend on the pool config staying correct.
- The SQL-level fix is one keystroke shorter per occurrence and clearer to read.

### Option B — Use `DateTime.UtcNow` parameters instead of `now()`

Pass `DateTime.UtcNow` from .NET as a parameter on every claim instead of letting Postgres evaluate `now()`.

Rejected because:
- For the comparison side, this would require evaluating "now" twice (once in .NET, once in PG) and they would not agree to the microsecond. The 5-minute window is robust to that, but the asymmetry adds nothing.
- For the write side this is fine and is what EF Core LINQ does by default. The rule above explicitly allows it. We just prefer `now()` for raw SQL where the worker is already executing inside Postgres and a parameter round-trip is unnecessary.

## References

- Tickets: [#26](https://github.com/neboxdev/complidrop/issues/26) (the fix that prompted this ADR), [#60](https://github.com/neboxdev/complidrop/issues/60) (initial project-wide audit confirming every other raw-SQL path is clean or clause-3 legitimate; codifies the rule in `CLAUDE.md` Rules of engagement)
- ADRs: [0007](0007-reminder-log-send-date-is-org-local.md) (sibling — calendar-day extraction via `AT TIME ZONE org."TimeZone"`, the legitimate use covered by clause 3), [0008](0008-reminder-multi-instance-coordination-via-advisory-lock.md) (other raw-SQL path, no timestamp surface, audited clean)
- External: [Postgres docs — Date/Time Types §8.5.1.3](https://www.postgresql.org/docs/current/datatype-datetime.html#DATATYPE-TIMEZONES) on session TimeZone semantics
