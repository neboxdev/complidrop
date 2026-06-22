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

## Alternatives considered

### Option A — `xmin` optimistic-concurrency token on `Document`
Add a rowversion token so a verdict write that raced an inputs change fails `DbUpdateConcurrencyException` and retries against the fresh inputs. **Rejected** as the primary mechanism: an entity-level token forces **every** `Document` write path (upload, patch, verify, fields, worker persist) to handle the concurrency exception, broadly changing their semantics from last-writer-wins to conflict-and-retry — a large, risky surface for a narrow race. The combined unit of work achieves consistency without it. (A future need for genuine *lost-update prevention*, not just consistency, could revisit this.)

### Option B — `FOR UPDATE` row lock across the verdict read→write
Lock the `Document` row from inputs-read to verdict-write so concurrent writers serialize. **Rejected**: requires every input-writer and re-grade to take an explicit transaction + row lock, more invasive than co-locating the verdict in the writer's existing `SaveChanges`, for the same consistency outcome.

### Option C — Keep best-effort by leaving the prior verdict on recompute failure
On `ApplyEvaluationAsync` failure, commit the inputs and leave `ComplianceStatus` unchanged. **Rejected**: if the edit changed compliance-relevant inputs, the untouched prior verdict is exactly the torn (stale) state #337 is about. Degrading to `Pending` (non-committal) is the safe failure mode.

### Option D — Extraction-status guard on `UpdateFields` (reject edits while Pending/Processing)
Forbid manual edits during an in-flight (re)extraction. **Rejected** as unnecessary friction: ADR 0017 already defines re-extraction-overwrites-edit as intended last-writer-wins, and the combined unit of work makes that outcome consistent, so there is no torn state to forbid the edit over.

## References

- Tickets: [#337](https://github.com/neboxdev/complidrop/issues/337), [#243](https://github.com/neboxdev/complidrop/issues/243) (audit), [#235](https://github.com/neboxdev/complidrop/issues/235), [#246](https://github.com/neboxdev/complidrop/issues/246), [#48](https://github.com/neboxdev/complidrop/issues/48)
- ADRs: [0017](0017-manual-field-edits-sync-compliance-inputs.md) (last-writer-wins on re-extract, now applied to the whole tuple)
- Code: `Services/ComplianceCheckService.cs` (`ApplyEvaluationAsync`), `Endpoints/DocumentEndpoints.cs` (`UpdateFields`, `UpdateDocument`, `EvaluateIntoUnitOfWorkAsync`), `BackgroundServices/ExtractionWorker.cs` (`PersistSuccess`)
- Audit: `docs/audits/concurrency-2026-06-22.md` §6
