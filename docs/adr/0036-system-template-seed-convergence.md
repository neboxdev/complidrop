# 0036. System templates converge to their seed definition (add / update / delete), tenant clones never

- **Status:** accepted
- **Date:** 2026-07-10
- **Deciders:** Ruben G. (founder), autonomous session

## Context

The five seeded vendor checklists (`ComplianceTemplateSeed.Templates`) are `IsSystemTemplate = true` rows that every org can assign to a vendor and that grade documents directly (`ComplianceCheckService.ComputeOutcome` permits system templates). They shipped with #192 (June 2026) and are live in production.

Before this decision the seeder could not correct them once shipped:

- **Original (`main` before #400):** `EnsureAsync` skipped an entire template whose *name* already existed. Any rule added, changed, or removed in a seed definition was inert against an already-seeded system template.
- **#400 / ADR-adjacent (PR #413):** `EnsureAsync` was upgraded to an **additive** reconcile — it back-fills seed rules missing on the `(DocumentType, FieldName, Operator)` natural key onto existing system templates, and re-grades their documents across orgs (`ReevaluateForTemplateForSystemAsync`, sample docs excluded). It still **never updates a changed value and never deletes a stale rule.**

A deep template review ([TEMPLATE-REQUIREMENTS-REVIEW.md](../rule-engine/TEMPLATE-REQUIREMENTS-REVIEW.md)) then established that three of the five templates need exactly the two operations the additive reconcile cannot perform on production rows:

- **Value changes:** Photographer general-liability `$500,000 → $1,000,000`; Transportation auto `$1,000,000 → $1,500,000` (below the federal for-hire floor, 49 CFR 387.33T).
- **Rule removals:** Security's `certification` expiry (maps to no document a guard *company* provides → permanent false "Missing"), Photographer's `license` expiry (Texas issues no photographer license) and E&O (no venue insurable interest), Transportation's `license_type equals "CDL"` (fails every lawful ≤15-seat driver — CDL attaches at 16+ seats, 49 CFR 383.5).

A raw-SQL EF data migration could mutate the rows, but it cannot perform the ADR 0030-compliant re-grade (recompute `ComplianceStatus` + `ComplianceCheck` rows per document in one unit of work); coordinating a migration with an app-level re-grade adds a second mechanism that must stay in lockstep with the seed. The re-grade machinery already exists in the seeder path (#400).

## Decision

The seeder is the **single source of truth for system templates**, and `EnsureAsync` **converges** each existing `IsSystemTemplate` template to its seed definition on boot:

- **Add** a seed rule the live template lacks (natural key `(DocumentType, FieldName, Operator)`, case-insensitive — the same key `ComplianceEndpoints.UpsertRule` dedupes on).
- **Update** a live rule whose `ExpectedValue`, error message, or sort order differs from the seed.
- **Delete** a live rule on a system template that no seed rule matches.
- Update the template `Description` when it differs.

Hard invariants:

1. **Tenant clones (`IsSystemTemplate == false`) are never loaded and never touched** — a venue's own edits are user data. Only the system org's templates converge.
2. **A changed system template re-grades its documents across all orgs** via the existing `ReevaluateForTemplateForSystemAsync` — which **excludes sample-demo docs** (ADR 0028; a pre-change sample predates newly-required fields and must not falsely flip). Verdict + checks commit in one unit of work per page (ADR 0030).
3. **Idempotent:** a boot whose system templates already match the seed makes no change and triggers no re-grade.
4. **No raw SQL, no EF data migration** for the rule content — convergence is app-level so it composes with the re-grade and with ADR 0009 (there is no `timestamptz` raw SQL to get wrong).

This supersedes the additive-only note in the #400 seeder comments.

## Consequences

### Positive
- A seed edit is the only step needed to correct a shipped system template — prod converges on the next deploy, fresh databases seed correctly, and both paths share one definition.
- The correction re-grades stale verdicts in the safe direction (fail-closed) automatically, closing the "false Compliant survives a rule change" gap that motivated #400's re-grade.
- No divergent migration/seed pair to keep synchronized; no raw SQL on the rules table.

### Negative
- `EnsureAsync` now **deletes and rewrites** system-template rows at boot — a stronger, more dangerous operation than insert-only. The blast radius is bounded to `IsSystemTemplate = true` rows and guarded by test, but a seed authoring mistake (e.g. a typo'd natural key) could delete a live rule. Mitigated by: convergence tests that pin add/update/delete + tenant-untouched + idempotency, and the fact that the seed is small and reviewed.
- Convergence widens the concurrent-double-boot race surface already tracked in #412 (no unique index on the rule natural key). Update-to-same-value and delete-of-same-row are idempotent across racing boots; only the duplicate-insert window remains, unchanged from #412. Not closed here.
- A stricter re-grade changes live customer verdicts on the deploy that carries a seed change — deliberately gated behind the legal/insurance sign-off ([G1-COUNSEL-BRIEF.md](../rule-engine/G1-COUNSEL-BRIEF.md) §0) before it may ship.

### Neutral
- The set of documents re-graded per changed template is the same cross-org fan-out #400 introduced; convergence only changes *what* rule set they are graded against.

## Alternatives considered

### Option A — Raw-SQL EF data migration on the rule rows
Rejected: cannot perform the ADR 0030 re-grade, so it needs a second app-level re-grade trigger that must stay in sync with the seed; introduces raw SQL on a table the app otherwise reconciles; two sources of truth (migration + seed) for the same rows.

### Option B — Keep the additive-only reconcile (#400) and ship a one-off migration per correction
Rejected: every future template correction that changes a value or removes a rule would need a bespoke migration; the seed stops being authoritative for system templates; higher long-run maintenance and drift risk.

### Option C — Version the system templates and insert a new version rather than mutate in place
Rejected as over-engineering for the current scale (single-digit orgs, five templates): vendors reference a template by id, so versioning would require a reassignment/migration of every vendor to the new version — more moving parts than convergence, with no benefit until there is a real need to preserve historical system-template versions (Phase 2 territory).

## References

- Tickets: #416 (this correction), #400 (additive predecessor), #397 (per-occurrence pin, same PR), #412 (rule natural-key unique index, deferred)
- ADRs: 0028 (sample demo — excluded from re-grade), 0030 (verdict/inputs combined unit of work), 0033 (supersession / cross-count discipline), 0009 (no `AT TIME ZONE` — moot here, no raw SQL)
- External: [TEMPLATE-REQUIREMENTS-REVIEW.md](../rule-engine/TEMPLATE-REQUIREMENTS-REVIEW.md) §4 (the corrected template set), [G1-COUNSEL-BRIEF.md](../rule-engine/G1-COUNSEL-BRIEF.md) §0 (go-live gate)
