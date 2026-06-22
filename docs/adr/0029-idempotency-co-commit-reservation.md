# 0029. Idempotency dedupe record co-commits with the side effect; concurrent loser replays the winner

- **Status:** accepted
- **Date:** 2026-06-22
- **Deciders:** Ruben G. (founder), Claude (implementing #336)

## Context

`IIdempotencyService` deduped mutating POSTs (`POST /api/documents/upload`, `POST /api/billing/checkout`, `POST /api/sample`) with a **check-then-store** pattern: `TryGetAsync` (replay a cached response on hit) → run the handler with its side effects → `StoreAsync` (insert a record under the `(OrganizationId, Key)` unique index).

This is safe for **sequential** retries (a client times out and retries with the same key after the first completed) — the dominant real case. It is **not** safe for two **concurrent** identical POSTs carrying the same `Idempotency-Key`: both miss `TryGetAsync` (neither has stored yet), both run the handler end to end, and **both side effects execute**. The trailing `StoreAsync` then no-ops or 500s on the second insert, but the duplicate side effect already landed. For `POST /api/documents/upload` that means **two `Document`s** (two blobs, two extraction costs, both counting toward the plan limit), with no business unique constraint to catch it. (`POST /api/sample` was already safe — its `IX_Documents_OrganizationId_SampleUnique` partial unique index is the concurrent backstop the other routes lacked; `POST /api/billing/checkout` produces ≤2 harmless Stripe sessions.)

Found by the #235 deep latent-bug hunt — concurrency audit (#243, `docs/audits/concurrency-2026-06-22.md` §4), filed as #336. Severity is minor / not currently client-reachable (the frontend mints a fresh `crypto.randomUUID()` key per upload click, so two clicks carry *different* keys), but it is a real backend-invariant gap and the substrate the public portal upload route (#333) builds on.

Two design questions had to be settled, which is why this is its own ticket + ADR:
1. **How** does the service make the claim atomic across concurrent requests?
2. **What does the losing concurrent request return** (the public-behavior decision)?

## Decision

**1. Co-commit the dedupe record in the same transaction as the side effect** (insert-first reservation, realized as the generalization of the sample endpoint's proven pattern).

The endpoint builds the dedupe record (`idem.BuildRecord(orgId, key, path, status, responseBody)`) and adds it to **its own `DbContext`**, so the record and the side-effect entity (the new `Document` / sample) commit in **one `SaveChangesAsync`**. The `(OrganizationId, Key)` unique index then makes the claim atomic: of two in-flight same-key requests, exactly one `SaveChanges` wins; the other fails with a `23505` unique violation that the endpoint catches via `idem.IsKeyConflict(ex)`. `StoreAsync` is removed from the interface; `TryGetAsync` (fast-path replay) and the new `BuildRecord` / `IsKeyConflict` replace it.

`IsKeyConflict` matches on the **index name** (`IX_IdempotencyRecords_OrganizationId_Key`, surfaced as `PostgresException.ConstraintName`), not merely SqlState `23505`, so it never swallows an unrelated unique violation firing in the same transaction (notably the sample partial index).

**2. The losing concurrent request replays the winner** (returns the winner's exact cached response — same status, same body — not a `409`).

This is achievable with no waiting precisely *because* the claim is co-committed: a `23505` conflict is direct evidence the winner has already committed its record, so the winner's response is readable immediately via `TryGetAsync`. A `409 idempotency.in_progress` is retained only as a defensive fallback for the impossible case that the winner's record can't be read back (only reachable under record GC mid-request, which does not run today).

**3. A committed record is a permanent idempotency claim** — `TryGetAsync` no longer filters by `ExpiresAt`. Clients mint a single-use key per action, so "replay for as long as the row exists" can never mask a legitimate retry, and it closes a corner the unique-index approach would otherwise open: a record must never be "present but ignored," because a same-key insert would then conflict with a row `TryGetAsync` refuses to replay, wedging the key at a `409`. `ExpiresAt` is retained purely as a hint for a future garbage-collection job (out of scope here; records were never GC'd before either).

## Consequences

### Positive
- **Airtight against concurrent duplicates.** The record and the side effect share a transaction, so there is no window in which a side effect commits but its dedupe record does not — the failure mode every separate-transaction reservation scheme has.
- **Seamless loser experience.** The losing racer gets the winner's real response, indistinguishable from a sequential replay; clients need no retry-on-409 logic.
- **One proven pattern, three endpoints.** Generalizes the sample endpoint's `23505`-catch-and-replay (which the audit already cleared as safe) to the shared idempotency key, instead of inventing a separate reservation/lease/zombie-reclaim machine.
- **Strictly safer than before** even for expired keys: an old key now replays rather than (as the stale `StoreAsync` no-op allowed) creating a fresh duplicate after the TTL lapsed.

### Negative
- **The loser does wasted work.** A losing upload still uploads its blob before the `SaveChanges` conflict, then rolls it back (best-effort `TryDeleteBlobAsync`). Acceptable: the concurrent-same-key case is rare (not even client-reachable today), and a transient orphan blob is the same tradeoff the sample endpoint already accepts.
- **Per-endpoint catch logic.** Because the record must co-commit with each endpoint's own side effect, the `23505` catch can't be fully centralized in a wrapper — each of the three endpoints carries the catch-and-replay. Mitigated by sharing `BuildRecord` / `IsKeyConflict` / `IdempotencyResults.Replay`.
- **Unbounded record growth** until a GC job exists — but this is unchanged from before (records were never deleted; the old `ExpiresAt` filter only hid them from replay), so it is not a regression.

### Neutral
- No schema change. The existing `(OrganizationId, Key)` unique index and the `IdempotencyRecord` columns are reused; `StatusCode` always holds a real HTTP status (no sentinel/placeholder state, because records are only ever written already-completed).
- `IdempotencyRecord` remains in `AuditSaveChangesInterceptor`'s non-audited set, so co-committing it emits no audit rows.
- **Billing checkout is the degenerate co-commit case.** It has no EF side-effect entity to bind the record to (the Stripe session is an external call), so the record commits alone on `SystemDbContext` — the unique index still serializes concurrent claimants, which is all that path needs. Document upload and sample seed are the cases where the co-commit's atomicity with a real entity carries its weight.

## Alternatives considered

### Option A — Insert-first placeholder reservation with a lease
Insert a `StatusCode = 0` "in progress" placeholder *before* the handler runs; `UPDATE` it with the real response on success, `DELETE` it on failure; a stale placeholder (crashed handler) is reclaimed after a lease window (à la the extraction zombie-reclaim). Fully centralizable in a wrapper and the loser does no wasted work. **Rejected** because it has an unavoidable (if tiny) duplicate window: the placeholder lives in a transaction separate from the side effect, so if the side effect commits but the `UPDATE`-to-completed then fails, the placeholder lease-expires and a retry re-runs the side effect → duplicate. For a compliance product whose bug class is "a document duplicated / a verdict computed twice," the co-commit's airtightness wins over the placeholder's centralization.

### Option B — Loser returns `409 in-progress` (Stripe-style)
Return `409` to the losing concurrent request and let the client retry (the retry then replays the winner via the fast path). Simpler, and matches Stripe's own concurrent-idempotency behavior. **Rejected as the primary contract** because co-commit makes the strictly better "replay the winner now" outcome free (the conflict proves the winner already committed), so there is no reason to push a retry onto the client. `409` is kept only as the defensive fallback.

### Option C — Late reservation (claim just before the side effect, after validation)
Move the claim below the cheap validation so validation errors need no cleanup. **Rejected** because it breaks replay-on-retry correctness: a sequential retry would re-run validation, and a check that now fails (e.g. the plan limit, which the first upload's own document pushed to the cap) would return a *different* error instead of replaying the original success. The claim/replay decision must precede validation — which the co-commit's fast-path `TryGetAsync` and the unique index both honor.

## References

- Tickets: [#336](https://github.com/neboxdev/complidrop/issues/336), [#333](https://github.com/neboxdev/complidrop/issues/333) (builds on this), [#243](https://github.com/neboxdev/complidrop/issues/243) (audit), [#235](https://github.com/neboxdev/complidrop/issues/235), [#48](https://github.com/neboxdev/complidrop/issues/48)
- ADRs: [0013](0013-retain-blob-on-document-delete.md) (blob retention contrast), [0028](0028-sample-demo-reuses-real-pipeline-and-shared-system-templates.md) (the sample unique-index pattern generalized here)
- Code: `Services/IdempotencyService.cs`, `Endpoints/IdempotencyResults.cs`, `Endpoints/{DocumentEndpoints,BillingEndpoints,SampleEndpoints}.cs`
- Audit: `docs/audits/concurrency-2026-06-22.md` §4
