# 0021. Extraction budget resets lazily on a UTC-month anchor

- **Status:** accepted
- **Date:** 2026-06-11
- **Deciders:** Ruben G., Claude

## Context

`Subscription.ExtractionSpendThisMonthUsd` was only ever incremented — no reset existed
anywhere — so the $5 free / $50 paid "monthly" ceilings (`CostCeilings`) were in fact
lifetime caps. Once an org crossed one, `CanSpendAsync` denied every future extraction
(dashboard and vendor portal both funnel through the `ExtractionWorker` gate) while the
UI promised "It resumes next cycle" (#256, audit FP-003).

A reset needs a month boundary. Two prior conventions were in tension:

- ADR 0007 establishes that `DateOnly` columns carry **org-local** calendar semantics
  (`ReminderLog.SendDate` records the org's wall-clock day).
- The cost ceiling is not a per-tenant billing promise — it protects the **company's**
  Document AI / Gemini bill.

## Decision

1. **`Subscription.SpendMonthStart` (`date`) anchors the counter to a UTC calendar
   month.** The counter counts only when the anchor is the current-or-newer UTC month;
   an anchor from any past month means the counter is stale and reads as **zero**
   (`CostTrackingService.EffectiveSpend` — shared by the gate and
   `GET /api/billing/subscription`, so the Settings "this month" tile shows the number
   the gate enforces).

   **Deliberate divergence from ADR 0007:** this `DateOnly` is UTC-anchored, not
   org-local. The ceiling is a company cost control; one unambiguous global boundary
   beats forty timezone-shifted ones, and no tenant-facing promise depends on which
   midnight the reset happens at. Do not "fix" this column to org-local.

2. **The reset is lazy — evaluated at read time, no background job.** Nothing flips rows
   at midnight; `CanSpendAsync` compares the anchor, and the first `RecordSpendAsync` of
   a new month re-anchors. Fits the existing DB-as-state philosophy (cf. the DB-as-queue
   extraction pipeline) and adds zero scheduler machinery.

3. **`RecordSpendAsync` is one atomic conditional `ExecuteUpdate`** (server-side CASE
   WHEN: same-or-newer month → increment, past month → overwrite-and-re-anchor). This
   also fixes the pre-existing lost-increment race between concurrent workers that the
   old read-modify-write save had. The **anchor is monotonic** (never moves backwards):
   a writer that stamped its month just before a UTC month flip but commits after
   another instance re-anchored the row increments the newer month's counter instead of
   wiping it — a boundary-straddling laggard's cents land in the new month, which is the
   safe direction for a spend control. The audit-interceptor bypass is
   behavior-preserving: the worker scope has no current user, so the old tracked save
   emitted no audit row either; `UpdatedAt` is set manually.

4. **Pre-#256 lifetime counters are forgiven on deploy.** The migration default
   (`0001-01-01`) is an always-stale anchor, so every existing row's counter reads as
   zero the moment the new code boots — orgs locked out by the lifetime cap revive with
   no manual data surgery. The accumulated number stays in the column (informational)
   until the first new spend overwrites it.

5. **Ceiling-failed documents are NOT auto-requeued when the month rolls.** Re-running
   weeks-old failed extractions unprompted would spend money without consent and surprise
   the user. The failure copy (`extraction.cost_ceiling_hit` in `display-labels.ts`)
   tells the user the limit resets next month and points at the existing one-click
   "Read again" action (which resets `ProcessingAttempts` and requeues). Within-cycle
   auto-recovery messaging is FP-056 and rides #241.

## Consequences

- "Monthly" in the ceilings' names is now true. The boundary is the UTC month flip.
- An org can legitimately spend up to the ceiling twice in two wall-clock days around a
  month boundary ($5 on May 31 + $5 on June 1) — inherent to any calendar reset.
- The worst-case race overspend is bounded to spend recorded within one DB round-trip of
  the month flip across overlapping instances (single-digit cents, monotonic-anchor
  capped), in the over-count direction.
- A clock-skewed future anchor still counts against today's ceiling (`>=` in
  `EffectiveSpend`) — over-enforcement is the safe failure mode.
- `CostTrackingServiceTests` pins: stale-month forgiveness, year-aware anchor equality,
  the `<=` ceiling boundary, increment vs reset, anchor monotonicity, concurrent-writer
  exactness (10-way), missing-subscription fail-closed, and the billing endpoint's
  effective-spend contract.

## Alternatives considered

- **Anchor on Stripe `CurrentPeriodEnd`:** free orgs have no Stripe subscription, so the
  field is null exactly where the $5 ceiling matters most; would need a fallback anyway.
- **Per-month spend rows:** auditable history, but a new table + join for a gate that
  needs one number; overkill at MVP.
- **Scheduled reset job:** a writer that must run exactly once per month per org
  reintroduces the multi-instance coordination this codebase keeps avoiding (cf. the
  reminder worker's advisory locks); the lazy read beats it with zero moving parts.
