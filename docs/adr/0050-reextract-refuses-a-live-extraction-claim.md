# 0050. Re-extract refuses a live extraction claim, on the worker's own staleness rule

- **Status:** accepted
- **Date:** 2026-08-01
- **Deciders:** Ruben G.

## Context

`POST /api/documents/{id}/reextract` reset the row to `Pending` (`ProcessingStartedAt = null`,
`ProcessingAttempts` / `FailedAttempts` zeroed) **unconditionally**. Nothing checked whether a worker
was mid-flight on that document.

`ExtractionWorker.ClaimSql`'s `FOR UPDATE SKIP LOCKED` protects the **claim instant** only — no lock is
held across the ~240s processing span. So an unconditional reset re-arms the queue underneath a running
worker, and the next 5-second poll claims the same row again:

1. Worker A claims doc X (`Pending` → `Processing`).
2. The endpoint sets X back to `Pending` at t+10s.
3. Worker B's next poll claims X. A and B now run Document AI + the LLM on the same blob concurrently.

Consequences, all verified against the code:

- **Double paid spend** — OCR + LLM twice, `RecordSpendAsync` twice.
- **Duplicated extraction fields, permanently.** On a *first* extraction — exactly when someone
  re-triggers, because nothing is on screen yet — both `PersistSuccess` runs `RemoveRange` an empty set
  and insert the whole field list. `DocumentField` has **no** `(DocumentId, FieldName)` unique index, and
  `UpdateFields` edits only `FirstOrDefault`, so nothing ever heals the duplicates.
- **Two `document.processed` feed rows.**
- **Single-instance variant: the request is silently swallowed.** The in-flight run commits `Completed`
  on top of the `Pending` the endpoint just wrote, and the re-read the user asked for never happens.

Reachability is narrow (the detail page disables "Read again" while `isProcessing` and polls every 3s,
so this needs a raw API client or a stale second tab; two concurrent workers need a deploy overlap), which
is why the ticket was red-teamed from Medium to Low. But `reviewers.md` § Deployment model is explicit that
**multi-instance races are real findings**, and the outcome here is persisted bad data plus real money.

## Decision

**1. The re-arm is one conditional statement, not a read-then-write.**

`Reextract` issues a single `ExecuteUpdateAsync` whose `WHERE` carries the guard, so Postgres re-evaluates
the predicate under the row's own `UPDATE` lock. A read-then-write guard (`if (doc.ExtractionStatus == …)`
above a `SaveChanges`) would test a status the worker can flip between the `SELECT` and the `UPDATE` — it
would be exactly as racy as the thing it guards. Zero rows affected means the guard bit.

**2. The guard's predicate is the WORKER'S OWN staleness rule, from the worker's own constant.**

Refuse only while `ExtractionStatus == Processing` **and** `ProcessingStartedAt` is newer than
`ExtractionWorker.ZombieClaimTimeout`. Letting a *stale* claim through is deliberate: the worker would
zombie-reclaim that row on its own next poll, so refusing buys no safety and would leave a wedged document
with no manual route back.

That correspondence only holds while the two sides are one value, so the threshold is promoted out of
`ClaimSql`'s `interval '5 minutes'` into a single constant, and `ClaimSql` is now **built** from it
(`InvariantCulture` — this is SQL, not display text). A test pins the SQL text back against the constant.

Because it is now a fact TWO layers must agree on, the value is **sourced in `Services/ExtractionClaims`**,
not on the hosted-service class: `ExtractionWorker.ZombieClaimTimeout` aliases it (the
`FieldNameMaxLength = InputLengths.DocumentFieldName` shape) and the endpoint reads the `Services/` constant
directly, so `Endpoints/` never compiles against `BackgroundServices/` — which nothing outside the
composition root does. `Services/InputLengths` states the same direction rule and scopes its own exception
the same way: worker-ONLY numbers (`MaxAttempts`, `MaxClaims`, `AttemptTimeoutCeilingSeconds`, the response
clamp widths) stay on the worker.

**And the guard reads ONE CLOCK, not one constant on two clocks.** The cutoff is computed *inside* the
predicate (`d.ProcessingStartedAt < DateTime.UtcNow - ExtractionClaims.ZombieTimeout`), which Npgsql renders
as `"ProcessingStartedAt" < now() - $interval` — Postgres's own clock, the one that wrote
`ProcessingStartedAt` (`"ProcessingStartedAt" = now()`) and the one `ClaimSql`'s staleness test uses. It is
ADR 0009-clean (a bare `now()` on timestamptz, no `AT TIME ZONE`), the same shape
`.SetProperty(d => d.UpdatedAt, DateTime.UtcNow)` already emitted. Capturing the cutoff into a local from
the API container's clock instead would have left a second, unpinnable disagreement: a container running
ahead of Neon stops refusing claims the worker still holds — the guard silently weakened — and no
behavioural test could see it, because the tests seed `ProcessingStartedAt` from the app clock too. A test
captures the host's EF command log and asserts the emitted `WHERE` compares against `now()`.

`AttemptTimeoutCeilingSeconds` (240) deliberately stays a literal rather than being derived from the new
constant: the pin guarding their relationship parses the threshold back out of `ClaimSql` and compares, so
deriving one from the other would collapse it into a tautology. The worker's own `-4m30s` / `-5m30s`
boundary tests keep their literals for the same #62 reason, and the endpoint's boundary pair mirrors them.

**3. `Processing` with a NULL `ProcessingStartedAt` counts as NO live claim — the guard fails OPEN there.**

No writer produces that shape (every `Processing` write sets both), and `ClaimSql`'s
`"ProcessingStartedAt" < now() - interval …` yields NULL — i.e. unclaimable — for it. Refusing it would make
it the only state in the system with no route back from either side.

**4. A refused re-arm is `409 document.extraction_in_progress`** in the existing error envelope
(`Endpoints.Error`, the `auth.email_taken` / `billing.already_subscribed` shape — not the idempotency
envelope, which is a different contract). It writes nothing at all, including no audit row: since
`ExecuteUpdateAsync` bypasses `AuditSaveChangesInterceptor`, the pre-existing explicit
`document.reextract_queued` row is now the whole audit trace of a re-extract, and a refused one must not
claim to have queued anything. Zero rows is ambiguous on its own, so the endpoint re-reads existence to
answer `404` for an absent (or another tenant's) id rather than telling someone their deleted document is
busy.

**5. The `(DocumentId, FieldName)` unique index the ticket also asks for is NOT added here.** See Option D.

## Consequences

### Positive

- The double-OCR/LLM window that a re-extract could open is closed, and with it the duplicate-field,
  double-spend and double-feed-row outcomes that followed from it.
- The silently-swallowed re-extract becomes a visible, actionable 409 instead of a click that does nothing.
- The endpoint and the worker can no longer disagree about which claims are live — one constant, pinned.

### Negative

- `ExecuteUpdateAsync` bypasses `AuditSaveChangesInterceptor`, so the re-extract no longer emits the
  interceptor's Before/After entity-mutation row; only the explicit `document.reextract_queued` row remains.
  Accepted: that row names the *action*, which is the auditable fact here, and this is the same trade
  `VendorPortalEndpoints`' upload-permit reservation already makes for the same reason.
- The happy path costs one statement; the refusal path costs two (the update, then the existence re-read).

### Neutral

- No frontend change. The detail page already disables "Read again" while `isProcessing`, and
  `api.post` surfaces `error.message` through `friendly(err)` into a toast, so the 409 copy lands as
  written with no HTTP jargon.
  **Amended — the first sentence is wrong, and wrong in exactly the state the 409 occupies. See
  [Amendment 1](#amendment-1-2026-08-01--the-409-must-reconcile-the-page-it-lands-on).**
- A re-extract at t+301s of a still-wedged attempt can still double-process. That is unchanged
  zombie-reclaim semantics — a second *worker* could claim it at that instant too — not something this
  guard regresses or is scoped to fix.
- Deliberately NOT a `Document` concurrency token. That is [#366](https://github.com/neboxdev/complidrop/issues/366).

## Alternatives considered

### Option A — `if (status == Processing) return 409;` above the existing `SaveChanges`

Rejected. The check would race the very claim it is checking: the worker can flip `Pending → Processing`
between the `SELECT` and the `UPDATE`, so the guard would be exactly as racy as the bug. The atomicity is
the fix, not the condition.

### Option B — `SELECT … FOR UPDATE` on the document in the endpoint's transaction

Rejected — it buys nothing. The worker's claim is a single `UPDATE … RETURNING` in autocommit, so the row
lock it takes is released immediately; it does not span the processing window. A row lock in the endpoint
would therefore never actually contend with a live claim, while adding a request-path lock that can queue
behind the worker's poll.

### Option C — silently no-op (200) instead of 409 when a claim is live

Rejected. The pre-fix single-instance behaviour already *was* a silent no-op, and that is the reported
defect: the user asks for a re-read and nothing happens. The client cannot distinguish "queued" from
"ignored" without a distinct status.

### Option D — add the `(DocumentId, FieldName)` unique index in this change

Rejected **here**, deferred to its own ticket with a human-signed-off dedupe step. EF migrations
auto-apply at startup and fail fast ([ADR 0016](0016-apply-ef-migrations-on-startup.md)), so a
`CREATE UNIQUE INDEX` over rows that already contain duplicates fails the boot migration and takes prod
down. Duplicates are not merely a race artifact — two ordinary, single-worker paths produce them:

- `ExtractionWorker.PersistSuccess` inserts one row per entry of `extraction.Fields` with **no**
  `GroupBy`, while the two lines immediately above it *do* de-duplicate by name for the jsonb mirror and
  the typed columns. A provider response repeating a field name writes two rows on the plain path.
- `Clamp(f.Name, FieldNameMaxLength)` collapses two distinct over-length names to the same stored name.

[ADR 0049](0049-clipped-extraction-field-is-disclosed-not-silent.md) already treats "an earlier duplicate
field-name row" as a shape the read-time truncation predicate must tolerate — i.e. the codebase records
duplicate-name rows as reachable. This is the same reasoning that refuted "just add a unique index" for the
waitlist table (ADR 0046); the index needs a measured population and a signed-off dedupe first.

### Option E — de-duplicate `PersistSuccess`'s insert loop by name in this change

Rejected as out of scope, and recorded so it is not mistaken for an oversight. It would fix a *different*
duplicate source than the one this ticket reports (a repeated name **within one response**, not two
concurrent runs), it changes a shape ADR 0049 explicitly documents and tests around, and it would not help
the concurrent case at all — two workers each write a full de-duplicated set and still land two rows per
name. It belongs with Option D's ticket, where the population is measured.

## Amendment 1 (2026-08-01) — the 409 must reconcile the page it lands on

The Neutral consequence above claimed "**No frontend change.** The detail page already disables 'Read
again' while `isProcessing`". That justification is **false in exactly the state the 409 occupies**, and it
is the reason the 409 is reachable at all.

`isProcessing` is derived from the LAST SUCCESSFUL payload
(`doc.extractionStatus === "Pending" || "Processing"`). A tab whose last payload said `Completed` therefore
has `isProcessing === false` and an ENABLED button — which is precisely the client state that can send a
re-arm into a live claim. And that tab never self-corrects: `refetchInterval` returns `false` for a settled
status, `refetchOnWindowFocus` is off (`frontend/src/lib/providers.tsx`), and `onError` only toasted. So the
toast said *"We're still reading this document"* while the extraction badge one line above it said **Read**,
the fields and verdict on screen were the previous read's, and every further click 409'd until a manual
reload. Two assertions about one document, contradicting each other, with nothing to break the tie — the
overclaim lens this repo's compliance-claims reviewer owns.

**Decision.** `reextract.onError` invalidates the detail query when — and only when — the error is this
conflict:

```ts
if (err instanceof ApiError && err.code === "document.extraction_in_progress") {
  qc.invalidateQueries({ queryKey: ["documents", params.id] });
}
```

The refetch lands the live `Processing` row, so the badge agrees with the toast, the 3-second poll restarts
off that same status, and the button disables itself — *"give it a moment"* becomes enforced rather than
merely advised, and the page self-heals when the read finishes instead of needing the reload.

**Why it is scoped to the one code**, rather than a blanket invalidate-on-any-error: the other failures
(5xx, `network.unreachable`, a queue-at-capacity 503) make no claim about the document's state, so there is
nothing for the cache to be reconciled against — while a blanket refetch fires a fresh GET into a backend
that just failed one and fights the deliberate #97 handling, where the detail query short-circuits its own
polling while erroring and the `StaleDataBanner`'s Try-again is the manual affordance. Both directions are
pinned by test (`page.test.tsx`, "the in-progress 409 reconciles the page it lands on"), the negative one
against a positive control in the same test rather than a timer.

The 409 envelope, the copy and every backend decision above are unchanged.

## References

- Tickets: [#365](https://github.com/neboxdev/complidrop/issues/365); adjacent, deliberately separate:
  [#366](https://github.com/neboxdev/complidrop/issues/366) (Document concurrency token),
  [#385](https://github.com/neboxdev/complidrop/issues/385)
- ADRs: [0016](0016-apply-ef-migrations-on-startup.md) (auto-migrate on startup — why Option D is deferred),
  [0044](0044-audit-client-input-clamped-at-the-boundary.md) (the audit trail this endpoint still writes),
  [0046](0046-request-input-length-guards.md) (the refuted "just add a unique index" precedent),
  [0049](0049-clipped-extraction-field-is-disclosed-not-silent.md) (duplicate-name rows as a tolerated shape)
- Code: `api/CompliDrop.Api/Endpoints/DocumentEndpoints.cs` (`Reextract`),
  `api/CompliDrop.Api/Services/ExtractionClaims.cs` (`ZombieTimeout` — the source both layers read),
  `api/CompliDrop.Api/BackgroundServices/ExtractionWorker.cs` (`ZombieClaimTimeout` alias, `ClaimSql`),
  `frontend/src/app/(dashboard)/documents/[id]/page.tsx` (`reextract.onError` — Amendment 1)
