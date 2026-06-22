# Concurrency & multi-instance audit — 2026-06-22 (#243)

Adversarial audit of the concurrency invariants of background processing and the write paths, under
multi-instance (Railway rolling deploy) and crash-timing conditions. Method (per epic #235): each
failure class gets a written verdict — a disproof (SAFE) citing the exact code path that makes it
safe, or a failing test → fix / ticket.

**Scope:** `ExtractionWorker` claim loop + zombie reclaim, `ReminderBackgroundService` multi-instance
ticking, rolling-deploy + startup auto-migrate overlap, the `IIdempotencyService` middleware, quota
check-then-act (portal `MaxUploads`, per-org cost ceiling), and same-row write races (manual edits vs
extraction completion).

## Result: 1 confirmed latent gap (ticketed, deferred); everything else SAFE.

| # | Class | Verdict | Evidence / ticket |
|---|---|---|---|
| 1 | ExtractionWorker double-process (slow-but-alive claim reclaimed) | SAFE | attempt timeout clamped `[60,240]s` < 300s zombie window, 60s margin; every external await honors the attempt CTS |
| 2 | ExtractionWorker crash between claim and status write | SAFE | atomic `UPDATE…FOR UPDATE SKIP LOCKED…RETURNING`; 5-min zombie reclaim; `MaxClaims=15` backstop; shutdown requeue undoes the claim |
| 3 | ReminderBackgroundService simultaneous ticks / late tick | SAFE | per-`(org, local-day)` pg advisory lock + `(ReminderId,DocumentId,SendDate,RecipientEmail)` unique index + at-least-once w/ deterministic Resend Idempotency-Key (ADR 0025/0015/0008) |
| 4 | Rolling-deploy startup auto-migrate overlap | SAFE | EF `MigrateAsync` acquires a Postgres advisory lock; fail-fast on bad migration; drift guard (ADR 0016) |
| 5 | Per-instance in-memory state (lockout, rate-limit) | SAFE / acceptable | lockout is a pure fn over the DB-persisted `FailedLoginAttempts`; rate-limit counters per-instance = bounded over-allow, a DoS backstop not a correctness control |
| 6 | Idempotency middleware — **concurrent** same-key double-submit | **GAP → [#336](https://github.com/neboxdev/complidrop/issues/336)** | check-then-store is sequential-retry-safe but not concurrent-safe; doc upload can create 2 `Document`s. Substantial fix (insert-first reservation) deferred to its own ticket; unifies with #333 |
| 7 | Quota check-then-act (`MaxUploads`, cost ceiling) | SAFE / acceptable | `MaxUploads` is an atomic conditional `UPDATE…WHERE UploadCount<MaxUploads`; cost `RecordSpend` is an atomic CASE-WHEN `ExecuteUpdate` (no lost increments); the `CanSpend`→`RecordSpend` window over-enforces by ≤(concurrency × ~$0.01), the safe direction |
| 8 | Same-row write race (manual edit #229 vs extraction completion) | SAFE | last-writer-wins on canonical inputs written atomically per transaction; verdict always recomputed from a consistent `(ExtractionFields + typed columns)` snapshot; re-extraction overwrite is by-design (ADR 0017) |

The default integration harness (`CustomWebApplicationFactory`) removes the background workers and
disables rate limiting, so each verdict below cites either source-level reasoning or the targeted
test that drives the real path (claim SQL run directly, two parallel `DbContext` scopes, advisory
lock on a side connection, `Task.WhenAll` endpoint calls).

---

## 1. ExtractionWorker — claim loop & zombie reclaim

`BackgroundServices/ExtractionWorker.cs`. The claim is a single raw statement (`ClaimSql`):
`UPDATE "Documents" SET ExtractionStatus='Processing', ProcessingStartedAt=now(), ProcessingAttempts+=1
WHERE Id = (SELECT Id … WHERE Pending OR (Processing AND ProcessingStartedAt < now()-interval '5 minutes')
ORDER BY CreatedAt FOR UPDATE SKIP LOCKED LIMIT 1) RETURNING Id` — atomic in Postgres without an
explicit transaction; `SKIP LOCKED` guarantees two instances never claim the same row.

**Double-process of a slow-but-alive worker (the #243 headline question): SAFE.** The per-attempt
timeout `AttemptTimeout` is clamped into `[60, 240]s` (`AttemptTimeoutCeilingSeconds = 240 = 300 − 60`).
`ProcessClaimedAsync` wraps the attempt in a linked CTS that `CancelAfter(AttemptTimeout)`, and every
blocking await inside `ProcessDocumentAsync` honors that token: blob download (`CopyToAsync(buffer, ct)`),
OCR (`DocumentAiOcrService` → `SendAsync(req, ct)` on the `"google"` HttpClient, itself capped at a
2-minute `HttpClient.Timeout`), and the LLM (`GeminiExtractionClient`/`AnthropicExtractionClient` →
`SendAsync(req, ct)` on 2-minute clients). So a wedged attempt is cancelled at ≤240s — which also
releases its row lock — strictly before the 300s zombie threshold could let a second worker reclaim it.
No double LLM spend, no duplicate `PersistSuccess`. Pinned by `Wedged_attempt_is_timed_out_and_requeued_as_a_counted_failure`,
`Wedged_OCR_stage_is_also_timed_out_and_requeued`, and the zombie-threshold theories
(`Stale/Fresh_processing_document_*_the_zombie_threshold_*` incl. non-UTC sessions).

**Crash between claim and status write: SAFE.** The claim already flipped the row to `Processing` with
`ProcessingStartedAt=now()` atomically, so a crash anywhere after leaves a reclaimable zombie that the
5-minute window recovers. `ProcessingAttempts` advanced but `FailedAttempts` did not, so the retry
budget (`MaxAttempts=5`) is not burned by interruptions; the `MaxClaims=15` backstop fails a document
that kills the process every claim (so the failure handler never runs). Graceful shutdown mid-attempt
(`RequeueInterruptedAsync`) returns the row to `Pending` AND decrements the claim it didn't really run,
on a fresh 10s token (not the cancelled stopping token). Pinned by `Graceful_shutdown_mid_attempt_requeues_without_counting_a_failure`,
`Document_past_the_claims_backstop_is_failed_up_front_without_reprocessing`.

## 2. ReminderBackgroundService — multi-instance ticking

`BackgroundServices/ReminderBackgroundService.cs`. Three layers make a simultaneous two-instance tick,
a late/restart tick, and a crash-between-send-and-commit all safe:

1. **Per-`(org, local-day)` session-scoped Postgres advisory lock** (`pg_try_advisory_lock(hashtextextended(key,0))`
   on a connection pinned for the whole tick — ADR 0008). A second instance ticking the same org/day
   gets `false` and skips; disjoint orgs still parallelize. A transient acquire failure is caught and
   the org is skipped (next tick retries), never killing the whole tick. Released in `finally`, with
   Npgsql `DISCARD ALL` on pool return as the final net. Pinned by
   `Held_advisory_lock_on_a_side_connection_causes_the_tick_to_skip_the_org`,
   `Releasing_the_advisory_lock_lets_a_subsequent_tick_process_the_org`,
   `Cancellation_during_tick_releases_the_advisory_lock_for_next_tick`.
2. **`(ReminderId, DocumentId, SendDate, RecipientEmail)` unique index** + the per-recipient
   `alreadyServed` set: even if the lock were bypassed, a duplicate insert hits `23505`, caught and
   logged (`ChangeTracker.Clear()` recovery so one doc's conflict doesn't cascade to the rest of the tick).
3. **At-least-once send with a deterministic Resend `Idempotency-Key`** (`BuildSendIdempotencyKey`,
   ADR 0025): the email is sent *before* the `ReminderLog` row commits, so a crash in between leaves no
   record and the next qualifying tick re-attempts — but it recomputes the *same* key, so Resend dedupes
   it server-side (24h TTL) rather than double-delivering. A *recorded* failure salts the key with the
   attempt's `SentAt` so a genuine retry is never served a cached error. Late/missed-08:00 ticks are
   absorbed by the open-ended catch-up window (`IsLocalSendWindow` `>=`, ends at local midnight). DST is
   the #244 audit's domain; the dedupe seam here is covered by the ADR-0015 tz-edit guard.

## 3. Rolling-deploy + startup auto-migrate overlap

`DatabaseMigrator.MigrateAndGuardAsync` + `Program.cs` startup block. Two overlapping containers during
a Railway deploy can both call `MigrateAsync` — safe, because EF Core acquires a Postgres **advisory
lock** before applying and records applied migrations in `__EFMigrationsHistory`: one applies, the other
waits then sees nothing pending. A bad migration throws and aborts boot (fail-fast → the old container
keeps serving). With `AutoMigrate=false`, the **drift guard** refuses to start a stale-schema container
(the #226 outage signature). Pinned by `DatabaseMigratorIntegrationTests` (`AutoMigrate_is_idempotent_when_schema_already_current`,
`AutoMigrate_propagates_a_failed_migration_for_fail_fast`, drift-guard cases). **SAFE.**

Per-instance in-memory state: login lockout is a *pure function* (`AuthLockout.ComputeLockoutDuration`)
over the DB-persisted `User.FailedLoginAttempts`, so it is consistent across instances. The ASP.NET
rate-limiter partitions live in per-instance memory, so a 2-instance deploy transiently allows up to 2×
the configured limit — acceptable degradation for a DoS/cost backstop (not a correctness invariant), and
the durable controls (cost ceiling, `MaxUploads`) are DB-atomic regardless. **Acceptable.**

## 4. Idempotency middleware — concurrent double-submit  ⟶  [#336](https://github.com/neboxdev/complidrop/issues/336)

`Services/IdempotencyService.cs`, applied inline in `DocumentEndpoints.UploadDocument`,
`BillingEndpoints` checkout, and `SampleEndpoints` seed. The pattern is **check-then-store**:
`TryGetAsync` (return cached on hit) → run handler (side effects) → `StoreAsync` (insert under the
`(OrganizationId, Key)` unique index).

- **Sequential retry: SAFE** (the dominant real case — pinned by
  `DocumentEndpointsTests.Same_idempotency_key_replays_without_creating_a_duplicate`).
- **Concurrent same-key double-submit: GAP.** Two in-flight identical POSTs both miss `TryGetAsync`,
  both run the handler, and both side effects execute; the second `StoreAsync` then no-ops or 500s, but
  the duplicate already landed. For doc upload that means **2 `Document`s** (no business unique
  constraint catches it). Billing creates ≤2 harmless Stripe sessions. **Sample is SAFE** — the
  `IX_Documents_OrganizationId_SampleUnique` partial unique index is the concurrent backstop the other
  routes lack (`SampleEndpointsTests` 4-way `Task.WhenAll` race test proves it).

**Severity minor / not currently client-reachable as a dupe:** the frontend mints a fresh
`crypto.randomUUID()` key *per upload click* (`useDocuments.ts`), so a UI double-click sends *different*
keys and legitimately creates two docs; the same-key collision needs a deliberately-crafted concurrent
client. **Deferred to [#336](https://github.com/neboxdev/complidrop/issues/336)** (`bug`, → #48): the
fix is an insert-first reservation that changes the shared idempotency contract (what does the loser
return?) across three endpoints — a public-behavior decision that warrants its own ticket and unifies
with the open [#333](https://github.com/neboxdev/complidrop/issues/333) (portal upload, same root
class). A parked repro `Concurrent_same_idempotency_key_creates_only_one_document` is added (skipped,
referencing #336) as the ready-made proving test.

## 5. Quota check-then-act races

- **Portal `MaxUploads`: SAFE.** Enforced by an atomic conditional `UPDATE … WHERE UploadCount < MaxUploads`
  under the row lock inside the upload transaction (not a racy pre-check) — established by the #242
  tenancy audit and unchanged.
- **Per-org cost ceiling: SAFE / acceptable.** `RecordSpendAsync` is a single atomic `ExecuteUpdate`
  with a server-side CASE-WHEN (monotonic re-anchor), so two concurrent workers cannot lose an increment
  — pinned by `CostTrackingServiceTests.Concurrent_RecordSpends_do_not_lose_increments` and
  `A_stale_stamped_writer_cannot_roll_the_anchor_backwards`. The `CanSpendAsync` (read) → `RecordSpendAsync`
  (write) window is a check-then-act, but the overshoot is bounded by (concurrent extractions × ~$0.01)
  and over-enforcement is the safe direction for a cost backstop, never runaway spend.

## 6. Same-row write race — manual edit (#229) vs extraction completion

`DocumentEndpoints.UpdateFields` and `ExtractionWorker.PersistSuccess` both write the canonical
compliance inputs (`ExtractionFields` JSON + the typed `GeneralLiabilityLimit/EffectiveDate/ExpirationDate`
columns) and the `DocumentField` display rows. There is no optimistic-concurrency token, so it is
last-writer-wins — but **SAFE**, because:

- The canonical inputs are written **atomically within a single `SaveChangesAsync` transaction** on each
  side, and the verdict is recomputed from them (`EvaluateAsync`/`EvaluateForSystemAsync`) — so the
  persisted verdict is always computed from a fully-`(W)` or fully-`(U)` snapshot, never a torn mix.
- The verdict explicitly does **not** read the `DocumentField` rows (they are display-only;
  `UpdateFields` comment at the canonical-inputs block). A precise interleave of the worker's
  `RemoveRange` + the user's row edits can transiently desync the display rows from the JSON mirror, but
  that is cosmetic and self-heals on the next edit / extraction.
- Re-extraction overwriting manual edits is **intended** (ADR 0017): `Reextract` resets the doc to
  `Pending`, and a manual edit made during that in-flight re-extraction being superseded is the designed
  contract, not a correctness bug.

## 7. Regression tests

The concurrency invariants were already densely covered (see `ExtractionWorkerTests`,
`ReminderBackgroundServiceTests`, `DatabaseMigratorIntegrationTests`, `CostTrackingServiceTests`,
`SampleEndpointsTests`); this audit confirmed coverage rather than finding untested SAFE paths. Added:
- `DocumentEndpointsTests.Concurrent_same_idempotency_key_creates_only_one_document` — the parked
  (`Skip`) repro for [#336](https://github.com/neboxdev/complidrop/issues/336): fires two same-key
  uploads via `Task.WhenAll` and asserts exactly one `Document`. Skipped (asserts the post-fix contract)
  until #336 lands the insert-first reservation; it is the ready-made proving test.
