# 0036. System templates converge to their seed definition (add / update / delete), tenant clones never

- **Status:** accepted (amended 2026-07-13 — see [Amendment 1](#amendment-1-2026-07-13--re-grade-on-any-rule-set-change-fk-safe-rule-deletes) and [Amendment 2](#amendment-2-2026-07-13--durable-re-grade-via-a-revision-watermark))
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
2. **A system template whose RULE SET changed re-grades its documents across all orgs** — on ANY rule add, delete, or update, message- and sort-order-only edits included ([Amendment 1](#amendment-1-2026-07-13--re-grade-on-any-rule-set-change-fk-safe-rule-deletes)) — via the existing `ReevaluateForTemplateForSystemAsync`, which **excludes sample-demo docs** (ADR 0028; a pre-change sample predates newly-required fields and must not falsely flip). Verdict + checks commit in one unit of work per page (ADR 0030). The re-grade is **DURABLE**: it is gated on a persisted revision watermark, not on "templates this boot mutated," so an interrupted or partially-failed re-grade re-fires on the next boot until every document catches up ([Amendment 2](#amendment-2-2026-07-13--durable-re-grade-via-a-revision-watermark)).
3. **Idempotent:** a boot whose system templates are all **caught up** (`RulesRevision == RegradedThroughRevision` — see [Amendment 2](#amendment-2-2026-07-13--durable-re-grade-via-a-revision-watermark); before the watermark this was "already match the seed") makes no rule change and triggers no re-grade.
4. **No raw SQL, no EF data migration for the rule content** — convergence is app-level so it composes with the re-grade and with ADR 0009 (there is no `timestamptz` raw SQL to get wrong). ([Amendment 2](#amendment-2-2026-07-13--durable-re-grade-via-a-revision-watermark) adds an **additive** schema migration for two `int` *bookkeeping* watermark columns — not a rule-content data migration — so this principle still holds: the rule rows are still reconciled only by the app-level convergence.)

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

## Amendment 1 (2026-07-13) — re-grade on any rule-set change; FK-safe rule deletes

A post-implementation re-review of #416 found two gaps in the convergence path as first shipped.

**Re-grade trigger decoupled from the evaluator (invariant #2).** The original code re-graded a converged template only on a *verdict-affecting* change — a rule add, a rule delete, or an `ExpectedValue` change — and skipped the re-grade for a pure `ErrorMessage` / `SortOrder` edit, on the reasoning that "`ExpectedValue` is the only non-natural-key field `ComplianceCheckService.EvaluateRule` reads." That coupled this data-layer seeder to the evaluator's internals: a future `EvaluateRule` that read `ErrorMessage`, `SortOrder`, or a new column for a pass/fail decision would make convergence silently skip a needed re-grade and leave a **stale persisted verdict** — the project's blocker-class failure. **Revised rule:** convergence re-grades a system template whenever its rule set changes AT ALL — any rule add, delete, or update, message- and sort-order-only edits included. The dropped optimization saved a re-grade only on a verdict-neutral message/sort edit, negligible at MVP scale; the decoupling is worth far more than the saving. Invariant #3 is unchanged: a boot with no drift makes no change and triggers no re-grade — and a *description*-only edit likewise updates the row without re-grading, since the description is not a rule.

**FK-safe rule deletes.** `ComplianceCheck.ComplianceRuleId` is a required FK with `ON DELETE RESTRICT`. On any existing DB where a document was graded against a rule this correction drops, a live `ComplianceCheck` references it, so removing the rule in the shared `SaveChanges` raised Postgres `23503` and rolled the WHOLE convergence back — the §4 correction would then silently never apply and re-fail every boot (`Program.cs` swallows the seed error). The delete arm now deletes each removed rule's dependent `ComplianceCheck` rows in the SAME unit of work as the rule removal (EF orders the dependent deletes before the principal-rule deletes), cross-org by design — mirroring the rules-page trash button's #269 fix. The post-commit re-grade recreates checks for the surviving rules.

Both are safe-direction corrections: strictly more re-grading (never a missed one), and an atomic convergence that either fully applies or fully rolls back. See [#416](https://github.com/neboxdev/complidrop/issues/416).

## Amendment 2 (2026-07-13) — durable re-grade via a revision watermark

A re-review of #416 found the convergence path re-graded **best-effort** and gated on the wrong thing, so a corrected rule set could persist while documents kept a stale verdict — indefinitely.

**The gap.** Convergence commits the corrected rules, then re-grades the affected documents in a *separate*, paged, best-effort step. That step could be lost two ways: (a) the boot is interrupted between the rule commit and the end of the re-grade (Railway SIGTERM on redeploy / startup-timeout), or (b) a re-grade page throws and is caught-and-continued inside `ReevaluateWhereAsync`. Either way the rules are corrected but some document keeps its OLD verdict — and **nothing healed it.** The re-grade was gated on `changedTemplateIds` = the templates *this boot* mutated, and the next boot's convergence is idempotent: the rules already match the seed, so `changedTemplateIds` is empty and the re-grade is skipped. `ComplianceEndpoints.UpsertRule` is blocked on system templates, and `ComplianceSweepBackgroundService` only runs date-transition `ExecuteUpdate`s (Compliant→Expired etc.) — it never re-runs rule evaluation. So a caterer COI persisted `Compliant` with no liquor coverage could stay `Compliant` forever — a false-`Compliant` persisted verdict, the product's blocker-class failure, and a violation of invariant #2.

**The fix — a durable revision watermark.** Two additive `int` columns on `ComplianceTemplate`, both default `0`: `RulesRevision` and `RegradedThroughRevision`.

- Convergence **bumps `RulesRevision`** whenever it changes a system template's rule set at all (any rule add / delete / `ExpectedValue` / message / sort change — the existing `ruleSetChanged` flag; a description-only edit does not count). The bump rides the SAME convergence `SaveChanges` as the rule change, so the revision can never advance without its rule change also committing.
- After the commit, the re-grade fans out over **every system template whose `RulesRevision != RegradedThroughRevision`** — which is exactly this boot's changes PLUS any template a prior boot changed but never finished re-grading. The watermark, not "this boot," is the gate.
- `RegradedThroughRevision` advances to the re-graded revision **only when the fan-out reports FULL success.** `ReevaluateForTemplateForSystemAsync` now returns a `RegradeResult(Targeted, Regraded, FailedPages)`; the seed advances the watermark iff `FailedPages == 0`. A skipped page leaves the watermark behind, so the next boot re-fires the re-grade for that template until every page lands. The advance is written with `ExecuteUpdate` (not the tracked entity) because the shared fan-out clears the context's `ChangeTracker` per page, which would otherwise silently drop a tracked-property write. The deploy log now reports attempted-vs-succeeded (`re-graded {Regraded}/{Targeted} … {FailedPages} page(s) failed`) so a partial re-grade is visible.

**Invariants.** #2 is restored and strengthened (the re-grade is now durable across an interrupted boot, not merely attempted once). #3's idempotency is preserved and re-stated in watermark terms: a boot where every system template has `RulesRevision == RegradedThroughRevision` bumps no revision and re-grades nothing. #4 still holds: the migration is **additive bookkeeping columns**, NOT a rule-content data migration — the rule rows are still reconciled only by the app-level convergence (no raw SQL on the rules, so ADR 0009 stays moot here). Tenant clones never converge, so their watermark stays `0/0` (invariant #1 untouched).

**Cost.** One extra tiny `ExecuteUpdate` per changed template per boot, and a re-grade that may repeat once after a genuinely interrupted boot — negligible at MVP scale, and strictly the safe direction (an extra re-grade never produces a wrong verdict; a missed one can). See [#416](https://github.com/neboxdev/complidrop/issues/416).

## References

- Tickets: #416 (this correction, incl. Amendment 2 durable watermark), #400 (additive predecessor), #397 (per-occurrence pin, same PR), #412 (rule natural-key unique index, deferred)
- ADRs: 0028 (sample demo — excluded from re-grade), 0030 (verdict/inputs combined unit of work), 0033 (supersession / cross-count discipline), 0009 (no `AT TIME ZONE` — moot here, no raw SQL), 0016 (migrations auto-apply on deploy — how the Amendment 2 watermark columns reach prod)
- Migration: `SeedRegradeRevisionWatermark` (additive — `ComplianceTemplate.RulesRevision` + `RegradedThroughRevision`, both `int NOT NULL DEFAULT 0`)
- External: [TEMPLATE-REQUIREMENTS-REVIEW.md](../rule-engine/TEMPLATE-REQUIREMENTS-REVIEW.md) §4 (the corrected template set), [G1-COUNSEL-BRIEF.md](../rule-engine/G1-COUNSEL-BRIEF.md) §0 (go-live gate)
