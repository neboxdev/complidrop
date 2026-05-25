# 0008. Reminder worker uses a per-(org, sendDate) Postgres advisory lock to coordinate across replicas

- **Status:** accepted
- **Date:** 2026-05-25
- **Deciders:** Ruben G.

## Context

`ReminderBackgroundService` runs in every API replica. Each replica's host fires the worker hourly (`Task.Delay` to the next top of UTC hour), each replica's tick checks every org's local-08:00 window, and any org whose window opens that UTC hour has its reminders processed.

Today the API runs as a single Container Apps instance, so two ticks never race. The moment we scale to two replicas (or to a per-region deploy that includes a US-hosted region), two workers will reach their `pg_try_advisory_lock`-free code path at the same UTC hour:

1. Both ticks query the same orgs.
2. Both pre-load the same empty `alreadySent` HashSet for `(reminder, doc, sendDate)`.
3. Both call `email.SendAsync(recipient, ...)` — **Resend receives two API calls and sends two emails to the same human.**
4. One replica's `SaveChangesAsync` wins the per-recipient unique-index race (see [ADR 0002](0002-reminder-dedupe-is-per-recipient.md)); the other gets a `DbUpdateException`, caught and detached (per the change-tracker fix in #7).

So the log is correct (one row per send), but the *send* has already happened twice. The DB index protects log integrity but cannot recall an email that's already at Resend. ADR 0002 flagged this in its Negative consequence and deferred to "an advisory lock or a coordinator row" if/when we scale out. Ticket [#25](https://github.com/neboxdev/complidrop/issues/25) is that follow-up.

The hourly tick is the natural coordination boundary: each `(org, local-day)` is processed at most once per local day under the `IsLocalSendWindow` gate, so a lock keyed on `(org, local-day)` is both sufficient and minimal — it serialises only the work that actually contests, leaving disjoint orgs free to run on different replicas in parallel.

## Decision

Adopt a **Postgres session-scoped advisory lock per `(orgId, localDate)` tuple**, acquired around each org's per-tick work and released in `finally`. Defence-in-depth: the per-recipient unique index from ADR 0002 stays in place and catches the (astronomically rare) hash-collision race.

Concretely, in `ReminderBackgroundService.ProcessHourlyTickAsync`:

1. Pin one DB connection for the entire tick (`await db.Database.OpenConnectionAsync(ct)`). Session-scoped advisory locks live on the connection, so all subsequent `SaveChangesAsync` calls in the per-org loop must use the same connection as the lock acquisition.
2. For each org in its local-08:00 window:
   - Compute `lockKey = "reminder:{orgId}:{localDate:yyyyMMdd}"`.
   - Run `SELECT pg_try_advisory_lock(hashtextextended($1, 0))` on the pinned connection.
   - If the result is `false`, another replica owns this `(org, day)` — log at debug and `continue`. The next replica's loop will move on to other orgs, which is the whole point of granular locking.
   - If `true`, run the existing per-reminder, per-doc loop unchanged.
   - In `finally`, run `SELECT pg_advisory_unlock(hashtextextended($1, 0))` so the lock is released even if the per-doc loop throws.
3. Close the pinned connection in the outer `finally`. Npgsql's pool reset (`DISCARD ALL` by default) releases any still-held session locks as a safety net, so a missed `pg_advisory_unlock` in an error path can't leak a lock across pool checkouts.

The lock key uses `hashtextextended('reminder:{orgId}:{date}', 0)` server-side rather than a CLR-side hash:

- Stable across .NET versions, GC moves, and process restarts (CLR `string.GetHashCode` is intentionally randomised per-process from .NET Core 2.1 onwards).
- One value space (`bigint`) shared with any future advisory-lock callers in this app, so the key namespace is observable in `pg_locks` without per-caller decoding.
- Collision probability in a 2^63 space is negligible at our scale (org count × days × forever ≪ √2^63). On collision, the worst case is that two genuinely disjoint `(org, day)` tuples falsely block each other for one hour; the dedupe HashSet then handles them on the next tick. No data corruption, no duplicate sends.

The `try`/`catch DbUpdateException` block on the per-doc save stays as-is. Its frequency drops from "happens on every multi-replica tick" to "essentially zero", but the defensive handling is the right shape for the residual collision case.

## Consequences

### Positive

- **Multi-replica safe.** Scaling the API horizontally (replica count, per-region deploys, blue/green) cannot cause duplicate reminder sends. The deployment topology and the worker's correctness are decoupled.
- **No schema change.** No new table, no migration, no rollout sequencing. The change ships in a single PR alongside the worker edit.
- **Granular.** Two replicas can process two different orgs in the same tick in parallel. The lock only serialises true contention.
- **Auto-released on failure.** Both the `finally`-based `pg_advisory_unlock` and Npgsql's pool `DISCARD ALL` release the lock, so a missed unlock in an exception path cannot wedge the org for the rest of the day. The next hourly tick re-attempts cleanly.
- **Consistent with existing prior art.** `ExtractionWorker` already uses `FOR UPDATE SKIP LOCKED` for the same "two workers, one row" coordination shape — both are Postgres-native, both are reviewed under [ADR 0005](0005-testcontainers-for-integration-tests.md)'s Testcontainers harness.

### Negative

- **Pinned connection for the whole tick.** Holding one pool connection open for the duration of one tick (sub-second per org × a small org count at MVP) is fine today; once orgs grow into the hundreds and the tick stretches to minutes, the pinned connection becomes more visible. The mitigation is to close the connection between orgs rather than across the whole tick — straightforward future change, not worth doing today for a tick that completes in well under a second on the MVP dataset.
- **Lock key is opaque in `pg_locks`.** `pg_locks` shows the hashed `bigint`, not the human-readable `"reminder:{orgId}:{date}"`. Diagnosing a held lock requires re-hashing the candidate key client-side and matching — surfaced in the ADR so future on-call notes can include the lookup. The alternative (storing the string in a coordinator table) carries its own schema/race surface and isn't worth the ergonomic win at this scale.

### Neutral

- **The ADR 0002 Negative consequence on multi-instance racing is now superseded** by this ADR. ADR 0002's per-recipient dedupe-key decision stands unchanged; only the "needs an advisory lock or a coordinator row" caveat is resolved here.
- **The defensive `DbUpdateException` catch in the worker stays.** Its expected frequency drops to ~0, but the handler is the right shape for the residual collision case (hash collision, or a defensive lock-bypass code path added in the future).
- **`hashtextextended(text, bigint)` is a standard Postgres function** (available since Postgres 10). Our Testcontainers image is `postgres:17-alpine`; production runs on Neon which tracks current Postgres majors. No version-floor concern.

## Alternatives considered

### Option A — single-leader election via a `JobLeader` table

Each replica races to upsert a row with a lease expiry; only the row-holder ticks. Rejected:

- Requires a new entity, a new migration, and a heartbeat loop to refresh the lease.
- Wastes the other replicas' capacity entirely — they never tick even when they could safely process disjoint orgs.
- The lease-expiry tuning (too short → leader thrash; too long → failover lag) is a knob we don't want for a worker whose entire workload is "one hour of reminders per org per day."
- Single point of failure: a buggy leader (e.g. stuck on a slow Resend response) blocks every org's reminders until the lease expires.

### Option B — pin the worker to a dedicated single-instance job-runner deployment

Keep `ReminderBackgroundService` in a separate Container Apps job with `minReplicas = maxReplicas = 1`, let the web replicas scale freely. Rejected:

- Couples worker correctness to a specific deployment topology. The moment someone scales the job to 2 (or sets `maxReplicas > 1` for failover), the original bug resurfaces silently — no test catches it, no log alerts on it.
- Doubles the deployment surface (separate image / config / observability for the job-runner) for a worker that ticks once an hour and idles the rest of the time.
- Fights the cloud-native default of horizontal scaling.

### Option C — transaction-scoped advisory lock (`pg_try_advisory_xact_lock`) wrapping the per-org loop in `BeginTransactionAsync`

Functionally equivalent on lock semantics. Rejected:

- Postgres aborts the whole transaction on a `DbUpdateException`, so the existing per-doc dedupe handler would need savepoints around each `SaveChangesAsync` — significant complication for no behavioural gain.
- Couples the tick's commit boundary to the lock's release. With a session-scoped lock the existing per-doc auto-commit pattern is preserved.

### Option D — CLR-side hash key (`(long)BitConverter.ToInt64(SHA256(...))`)

Hash the lock key in C# and pass the bigint. Rejected:

- The `bigint` value passed in is implementation-detail noise; using Postgres' `hashtextextended` keeps the key visible (as a text literal) in any future ad-hoc query that wants to inspect or reconstruct it.
- Adds a hashing dependency in the worker.
- `string.GetHashCode` would be wrong (randomised per process); the workaround is to bring in another hash, which is just re-implementing what Postgres ships.

## Test coverage

- `Concurrent_ticks_against_the_same_org_send_each_recipient_exactly_one_email` — two `ReminderBackgroundService` instances driven via `Task.WhenAll`, share the same DB and fakes, asserts one email + one log row per recipient. The marquee invariant.
- `Held_advisory_lock_on_a_side_connection_causes_the_tick_to_skip_the_org` — manually acquires `pg_advisory_lock` for the `(org, sendDate)` key on a separate Npgsql connection, runs one tick, asserts the org was skipped entirely (no email, no log row). Pins the lock-acquisition path explicitly so the concurrent test can't false-positive on dedupe alone.
- `Releasing_the_advisory_lock_lets_a_subsequent_tick_process_the_org` — manual acquire, release, tick, assert normal processing. Confirms the acquire/release round-trip works.
- `Different_orgs_can_be_processed_in_the_same_tick_when_one_orgs_lock_is_held_by_another_instance` — two orgs, one's lock held externally; one tick must process the other org and skip the locked one. Pins the granularity (lock is per-org, not global).
- `Lock_is_released_after_each_org_so_a_second_tick_in_the_same_process_processes_normally` — two sequential ticks at the same instant; first sends, second finds dedupe and sends nothing. Pins that the `finally`-based release works.

## References

- Ticket: [#25](https://github.com/neboxdev/complidrop/issues/25) — supersedes the multi-instance Negative consequence in [ADR 0002](0002-reminder-dedupe-is-per-recipient.md).
- Related ADR: [ADR 0002](0002-reminder-dedupe-is-per-recipient.md) (per-recipient dedupe key, the defence-in-depth this lock backstops), [ADR 0007](0007-reminder-log-send-date-is-org-local.md) (`SendDate` is the org-local day, which the lock key uses).
- Prior art in repo: `ExtractionWorker.ClaimNextAsync` uses `FOR UPDATE SKIP LOCKED` for the same "two workers, one row" shape (`api/CompliDrop.Api/BackgroundServices/ExtractionWorker.cs`).
- Postgres docs: [Advisory Locks](https://www.postgresql.org/docs/current/explicit-locking.html#ADVISORY-LOCKS).
