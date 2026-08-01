# 0030. Compliance verdict commits in the same transaction as its inputs (combined unit of work)

- **Status:** accepted
- **Date:** 2026-06-22
- **Deciders:** Ruben G. (founder), Claude (implementing #337)

## Context

The persisted compliance verdict (`Document.ComplianceStatus` + the `ComplianceCheck` rows) was written in a transaction **separate** from the canonical compliance inputs it is derived from (`Document.ExtractionFields` + the typed `GeneralLiabilityLimit / EffectiveDate / ExpirationDate` columns), with no concurrency token on `Document` and no lock spanning the verdict read→write. Both input-writing paths persisted inputs and verdict as two transactions:

- **Manual edit** (`DocumentEndpoints.UpdateFields`, also `UpdateDocument` for vendor/type): committed inputs (`SaveChanges`), then called `ComplianceCheckService.EvaluateAsync`, which did its OWN read→compute→`SaveChanges` of the verdict.
- **Extraction** (`ExtractionWorker.PersistSuccess`): committed inputs + `ComplianceStatus = Pending`, then called `EvaluateForSystemAsync` (same separate read→compute→save).
- The shared core `EvaluateInternalAsync` was a plain `FirstOrDefaultAsync → ComputeOutcome → SaveChangesAsync` — no `FOR UPDATE`, no version token. The two callers used different DbContexts (`AppDbContext` vs `SystemDbContext`), so there was no shared change tracker to reconcile them.

**The reachable race** (found by the #235 hunt — concurrency audit #243 §6, filed as #337): a user edits a document's fields *while* a (re)extraction of the same document is in flight. The worker computes verdict(W) from freshly-extracted inputs W; the user commits inputs U and verdict(U); the worker commits verdict(W) **last**. Terminal state: **inputs = U but `ComplianceStatus` = verdict(W)** — a verdict that contradicts the stored inputs. It does not self-heal (the hourly `ComplianceSweepBackgroundService` only flips date-driven Expired/ExpiringSoon transitions, never re-runs rule evaluation), and the detail page renders the stored verdict beside the edited field values — so a user can see **Compliant next to a deficient GL limit** (the dangerous direction for a compliance product). This is *not* the ADR 0017 "re-extraction overwrites manual edits by design" contract, which yields a clean last-writer-wins (fully W or fully U); this is a torn pair that is neither.

The compliance/audit core is a #246 "touch only with a control-asserting test + AuditLog golden snapshot" area, which is why this is its own `/start` ticket.

## Decision

**Fold the verdict computation into each input-writer's own unit of work (combined unit of work).**

A new `IComplianceCheckService.ApplyEvaluationAsync(DbContext context, Document doc, CancellationToken ct)` evaluates an **already-tracked** document and applies the verdict (`ComplianceStatus` + the `ComplianceCheck` rows) to the **same context WITHOUT saving**. The input-writing paths call it just before their own `SaveChanges`, so the inputs and the verdict they imply commit in **one transaction**:

- `UpdateFields` / `UpdateDocument`: apply the verdict on the edited tracked entity, then `SaveChanges` once.
- `ExtractionWorker.PersistSuccess`: apply the verdict on the freshly-extracted tracked entity, then `SaveChanges` once (the separate `EvaluateForSystemAsync` pass is removed; the worker no longer parks the doc at `Pending` for a second transaction to resolve).

Each writer now commits the **whole `(inputs, verdict)` tuple atomically**, so any interleave resolves to one writer's consistent pair — never a mix. This is **last-writer-wins on the tuple**, which is exactly the ADR 0017 contract (re-extraction overwriting a manual edit is by design); the fix is that it's now applied to the inputs *and* the verdict together instead of leaving them independently racing.

`EvaluateAsync` / `EvaluateForSystemAsync` are retained for the **pure re-grade callers** that do not themselves change inputs (the "Check again" button, the vendor/checklist/rule-change fan-outs) — they now delegate to `ApplyEvaluationAsync` and add the `SaveChanges`. Their read→compute→write window is a single in-method round-trip with no interleaved input-write from the same action, so they are not the scoped race; folding the verdict into the input-writers is what closes it.

**Best-effort preserved via Pending-degradation.** The pre-existing, deliberately-tested guarantee — a failing inline recompute must not fail the user's edit (`ThrowingComplianceCheckService` tests) — is kept: if `ApplyEvaluationAsync` itself throws, the caller catches it, sets `ComplianceStatus = Pending` (a safe "not yet graded" state the sweep / "Check again" resolves), and commits the inputs. So the edit still succeeds, but the stored verdict is never a **confident value computed from now-stale inputs** — `Pending` is non-committal, not contradictory. The worker does the same (matching its prior best-effort `try/catch`) rather than failing the extraction into a costly re-OCR/LLM retry. `ApplyEvaluationAsync` performs all its I/O (template load, existing-checks load) *before* any change-tracker mutation, so a throw leaves no partial check rows for the fallback `SaveChanges` to commit.

## Consequences

### Positive
- **No torn `(inputs, verdict)` pair** under any manual-edit-vs-(re)extraction interleave — the acceptance invariant. Proven by a deterministic two-context interleave test and an AuditLog golden snapshot.
- **One audit row per logical edit.** The interceptor now emits a single `document.updated` row whose Before/After spans the input change *and* the verdict transition, instead of two rows from two transactions (the first of which captured a torn new-inputs/stale-verdict snapshot).
- **The worker no longer publishes an intermediate `Pending`** between extraction and grading — a processed document reaches its real verdict atomically.
- **No schema change, no entity-wide concurrency token**, so every other `Document` write path (upload, patch, verify, delete) keeps its simple last-writer-wins semantics.

### Negative
- **Best-effort now degrades the verdict to `Pending` on a recompute failure** rather than leaving the previous verdict untouched. This is the correct trade-off (a stale confident verdict is the bug), and the failure is rare — `ApplyEvaluationAsync`'s only I/O is a cheap template load, so a failure ≈ the inputs `SaveChanges` failing anyway.
- **`ComplianceCheck` display rows can still transiently desync** under a concurrent edit-vs-extraction interleave (each writer's `ClearExistingChecks` reads the other's not-yet-committed rows), so the detail-page explainer may briefly show mixed check rows. This is **cosmetic** (the headline `ComplianceStatus` verdict is consistent) and **self-heals** on the next evaluation, which clears and rewrites the checks. Audit #243 §6 scoped it as such; making the check rows airtight would need the heavier row-lock / token this ADR deliberately avoids.

### Neutral
- `ApplyEvaluationAsync` loads `Vendor → ComplianceTemplate → Rules` via the tracked navigation query against the document's *current* (possibly just-edited) `VendorId`, honoring the Vendor soft-delete filter exactly as the prior `Include` did. The pure re-grade path does one extra cheap query (doc, then vendor chain) versus the old single Include — negligible, and not on a hot path.
- `EvaluateForSystemAsync` is now **caller-less in production** — the worker (its only former caller) folds grading into `PersistSuccess` via `ApplyEvaluationAsync`. It is retained as the symmetric system-context entry point to `EvaluateAsync` (and is still exercised by the sample-grading test as a convenient driver); a future system-context re-grade would use it. If no such caller materializes it can be dropped along with the service's now-`EvaluateForSystemAsync`-only `SystemDbContext` dependency.

## Alternatives considered

### Option A — `xmin` optimistic-concurrency token on `Document`
Add a rowversion token so a verdict write that raced an inputs change fails `DbUpdateConcurrencyException` and retries against the fresh inputs. **Rejected** as the primary mechanism: an entity-level token forces **every** `Document` write path (upload, patch, verify, fields, worker persist) to handle the concurrency exception, broadly changing their semantics from last-writer-wins to conflict-and-retry — a large, risky surface for a narrow race. The combined unit of work achieves consistency without it. (A future need for genuine *lost-update prevention*, not just consistency, could revisit this.)

### Option B — `FOR UPDATE` row lock across the verdict read→write
Lock the `Document` row from inputs-read to verdict-write so concurrent writers serialize. **Rejected**: requires every input-writer and re-grade to take an explicit transaction + row lock, more invasive than co-locating the verdict in the writer's existing `SaveChanges`, for the same consistency outcome.

### Option C — Keep best-effort by leaving the prior verdict on recompute failure
On `ApplyEvaluationAsync` failure, commit the inputs and leave `ComplianceStatus` unchanged. **Rejected**: if the edit changed compliance-relevant inputs, the untouched prior verdict is exactly the torn (stale) state #337 is about. Degrading to `Pending` (non-committal) is the safe failure mode.

### Option D — Extraction-status guard on `UpdateFields` (reject edits while Pending/Processing)
Forbid manual edits during an in-flight (re)extraction. **Rejected** as unnecessary friction: ADR 0017 already defines re-extraction-overwrites-edit as intended last-writer-wins, and the combined unit of work makes that outcome consistent, so there is no torn state to forbid the edit over.

## Amendment 1 (2026-08-01) — the two PARTIAL writers must DETECT a conflicting concurrent commit

The combined unit of work holds for **whole-tuple** writers. `ExtractionWorker.PersistSuccess` writes every canonical input it knows about plus the verdict, so whichever such writer commits last wins the whole tuple and the terminal row is somebody's consistent pair. The Decision above is unchanged for them.

`UpdateFields` and `UpdateDocument` are **partial** writers, and for them one transaction is not enough. Each rebuilds the ENTIRE `ExtractionFields` JSON mirror from its own read snapshot, but EF's modified-property tracking writes back only the typed columns / FK **that request touched**. `Document` carried no concurrency token and nothing locked the row across load → save, so two overlapping edits committed a row that was not either writer's snapshot:

- **Two field edits, different fields (#366 scenario A).** A corrects `general_liability_limit` 1M → 3M and commits (typed column + mirror + verdict). B, loaded before that, edits `certificate_holder`, rebuilds the whole mirror from its stale snapshot (GL still 1M) and grades from its stale tracked entity. B's UPDATE carries the mirror and `ComplianceStatus` but NOT `GeneralLiabilityLimit`. Terminal row: typed column 3M, mirror 1M, verdict computed from 1M. The two copies of one canonical input disagree and the verdict matches neither — and when A's correction was DOWNWARD, the badge reads Compliant beside a deficient displayed limit, the direction #337 exists to prevent.
- **Vendor patch vs field edit (#366 scenario B), the sharper one.** `UpdateDocument` commits `VendorId = V2`; the in-flight `UpdateFields` grades against its stale tracked `V1` and commits that verdict without touching `VendorId`. The row keeps the new vendor with the **old vendor's checklist verdict** — a wrong persisted compliance verdict, which this project's severity anchors call a blocker.

Neither self-heals: `ComplianceSweepBackgroundService` only does date transitions, never rule evaluation.

**Decision.** Those two writers — and only those two — run their load → mutate → evaluate → `SaveChanges` inside a `REPEATABLE READ` transaction (`Endpoints/DocumentWriteConcurrency.RunAsync`). Postgres then refuses to let an UPDATE land on a row version committed after the transaction's snapshot and raises `40001 serialization_failure`, which is exactly the fact these writers need and could not otherwise observe. On that signal the whole callback is **re-run against a fresh read**: new transaction, new snapshot, re-read the document, re-apply the request to it, recompute the verdict. The winner's committed change becomes an INPUT to the retried verdict instead of something the loser half-overwrites. Bounded at `Services/DocumentConcurrency.MaxAttempts` (3).

**Exhaustion answers `409 document.concurrent_update` and commits nothing.** This is stronger than — not a departure from — the Pending-degradation rule above. That rule governs a RECOMPUTE failure, where the user's inputs are committing regardless and the only question is which verdict rides along, so a non-committal `Pending` beats a confident verdict from stale inputs. Here the whole unit of work is still abandonable: rolling back leaves the last successful writer's consistent `(inputs, verdict)` tuple exactly as it stood. Committing the edit with `Pending` instead would BE a half-applied partial write of the shape this amendment removes.

**Why not the `xmin` token of Option A.** #366 suggested it as the narrowest fix, on the reading that the retry could be confined to these two endpoints and *"leave the other paths' last-writer-wins undisturbed"*. The code refutes that: `UseXminAsConcurrencyToken` is an ENTITY-level mapping, so it makes EVERY tracked `Document` write optimistic whether or not that path handles the exception. The worst landing is the extraction worker, whose window between claim and `PersistSuccess` is the whole OCR + LLM run (minutes), and whose own remarks (`ExtractionWorker.Clamp`) record what an exception out of that `SaveChanges` costs: the catch's bookkeeping save runs on the SAME context and throws again, `FailedAttempts` never increments, and the document is zombie-reclaimed every 5 minutes **re-paying Document AI + the LLM on every doomed run**. The exact ordinary sequence Option A's rejection worried about — a user correcting fields while an extraction is in flight — would have become a money-burning loop. So Option A's original reasoning stands, and its parenthetical (*"a future need for genuine lost-update prevention could revisit this"*) is answered by scoping the detection to the two writers that need it rather than by taking the token.

**Why not the `FOR UPDATE` of Option B.** It would also serialize the two writers, but it takes the `Documents` row lock BEFORE the transaction touches `ComplianceChecks`, while every other writer's EF batch acquires them in the opposite order. That is a lock-order inversion the current code does not have — a new deadlock (`40P01`) between an edit and a worker persist, whose worker-side landing is the same re-paid extraction. `REPEATABLE READ` changes no lock acquisition order at all: the same statements, in the same order, with Postgres refusing the stale-basis UPDATE instead of applying it. This is also why `DocumentConcurrency.IsConcurrentUpdateConflict` deliberately does NOT match `40P01`: with no inversion introduced, a deadlock here would be a new bug that must surface loudly rather than be absorbed by a retry.

**Recorded residual, deliberately not fixed here.** The pure re-grade paths (`EvaluateAsync` / `EvaluateForSystemAsync` / the fan-outs) keep their read → compute → write window. They write no inputs, so they cannot LOSE an update; the worst they do is store a verdict computed from inputs that changed a moment earlier, which is the last-writer-wins window this ADR already accepts, and the next re-grade heals it. Giving them the same treatment would put the whole fan-out under retry for no invariant this ticket is about.

## References

- Tickets: [#337](https://github.com/neboxdev/complidrop/issues/337), [#243](https://github.com/neboxdev/complidrop/issues/243) (audit), [#235](https://github.com/neboxdev/complidrop/issues/235), [#246](https://github.com/neboxdev/complidrop/issues/246), [#366](https://github.com/neboxdev/complidrop/issues/366) (Amendment 1), [#48](https://github.com/neboxdev/complidrop/issues/48)
- ADRs: [0017](0017-manual-field-edits-sync-compliance-inputs.md) (last-writer-wins on re-extract, now applied to the whole tuple), [0050](0050-reextract-refuses-a-live-extraction-claim.md) (the sibling atomic-guard-instead-of-read-then-write decision, whose "that is #366" pointer Amendment 1 answers)
- Code: `Services/ComplianceCheckService.cs` (`ApplyEvaluationAsync`), `Endpoints/DocumentEndpoints.cs` (`UpdateFields`, `UpdateDocument`, `EvaluateIntoUnitOfWorkAsync`), `BackgroundServices/ExtractionWorker.cs` (`PersistSuccess`), and for Amendment 1 `Endpoints/DocumentWriteConcurrency.cs` (the transaction + bounded retry + 409) and `Services/DocumentConcurrency.cs` (the 40001 predicate and the retry bound)
- Audit: `docs/audits/concurrency-2026-06-22.md` §6
