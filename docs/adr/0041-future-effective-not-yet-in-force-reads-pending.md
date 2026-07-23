# 0041. A not-yet-in-force (future-effective) certificate reads Pending, via a read-only overlay

- **Status:** accepted
- **Date:** 2026-07-23
- **Deciders:** Ruben G. (founder), Claude (implementing #362)

## Context

`Document.EffectiveDate` (the policy's start date) was parsed, exposed to user rules via `LookupValue`, and
displayed/exported — but **never compared to "today" anywhere in the verdict, count, supersession, or
vendor-rollup logic**. So a certificate that is **effective in the future** graded as if it were in force
today:

- A vendor's **first-ever** policy, effective next month, all rules passing, graded **Compliant today** — the
  product asserted present-tense coverage that is not in force. There is no prior cert to supersede, so the
  [ADR 0033](0033-document-supersession-expired-liability.md) supersession machinery does not reach this case
  at all.
- Combined with a lapsed prior cert, a future-effective renewal also made the old expired liability vanish —
  the supersession half of the same bug, closed by [ADR 0033 Amendment 2](0033-document-supersession-expired-liability.md#amendment-2-2026-07-23--superseder-must-be-continuous-effective-date).

This ADR covers the **verdict** half: what a future-effective document's compliance status reads. It is the
effective-date sibling of the #257/#294 date-overlay cluster ([ADR 0027](0027-compliance-date-window-boundaries.md)),
which made a doc's verdict reflect its **expiration** relative to today; #362 extends that to its **effective**
date relative to today.

## Decision

**A certificate whose `EffectiveDate` is a calendar date strictly after today is "not yet in force." When its
verdict would otherwise read an affirmative Compliant or ExpiringSoon, it reads `Pending` ("not yet in force")
instead.** Precedence, chosen to disturb the well-tested #257/#294 invariants as little as possible:

1. **Expired still wins outright** (top precedence, unchanged). A malformed cert (`EffectiveDate` after today
   *and* `ExpirationDate` before today) reads Expired — a present liability is never softened to Pending.
2. **A doc that FAILS its rules stays NonCompliant.** A not-yet-active deficient cert is accurately
   "not compliant" — the demotion must never mask a hard fail.
3. **Only the affirmative outcomes demote.** A Compliant or ExpiringSoon verdict for a future-effective doc
   reads `Pending`. So the change only ever moves a doc **out of the compliant tally** — never into or out of
   the NonCompliant or Expired populations.

**Reuse `Pending`; do not add a new `ComplianceStatus` value.** `Pending` already means "graded, but nothing
affirmative to assert yet" (the zero-applicable-rules branch of `ComputeOutcome` already returns it). A new
enum value would ripple through frontend badges, dashboard counts, list filters, export, and plan surfaces for
no semantic gain.

**The demotion is a READ-ONLY overlay — never persisted.** This is the load-bearing design choice:

- The persisted `Document.ComplianceStatus` keeps the **real rule verdict** (Compliant / ExpiringSoon).
  `ComplianceCheckService.ComputeOutcome` and the nightly `ComplianceSweepBackgroundService` deliberately do
  **not** write `Pending` for a future-effective doc.
- Every **read** surface applies the demotion: `ComplianceStatusDeriver.Effective` (which gains an
  `effectiveDate` parameter) plus every SQL mirror of it — the documents-list status filter and badge, the
  dashboard `compliant`/`expiringSoon` counts and the compliance-rate denominator, and the vendor coverage
  rollup; the CSV/PDF export derives at generation time through the same helper.

The reason it must be read-only: unlike Expired/ExpiringSoon (monotonic-forward — a date that passes never
un-passes), future-effective **resolves back** to the real verdict the moment `today` reaches `EffectiveDate`.
If the demotion were persisted as `Pending`, the rule verdict would be erased and **nothing would recover it**
— no production path re-runs rule evaluation on an effective-date crossing, so the doc would be stranded at a
stale `Pending` after it became effective (a new stale-verdict bug of exactly the class this work fights). As a
read overlay, the doc **self-heals**: the read surfaces stop demoting the instant the calendar reaches the
effective date, revealing the stored real verdict — precisely how the Expired/ExpiringSoon overlay already
behaves.

**Date↔instant boundary (ADR 0027 / 0009 convention).** "Not yet in force today" is `effectiveDate.Date > today`.
The SQL sites use the provably-equivalent instant test `effectiveDate >= today + 1 day` at UTC midnight
(`ComplianceStatusDeriver.NotYetEffectiveLowerBoundInclusive`), the mirror of `WindowUpperBoundExclusive`.
`EffectiveDate` is a face date stored at UTC midnight, so the comparison stays a plain `timestamptz`-vs-
`timestamptz` test — no `::date`, no `date_trunc`, no `AT TIME ZONE`, no session-TimeZone dependence.

## Consequences

### Positive
- The product no longer asserts present-tense coverage for a policy that has not started. A future-effective
  cert reads `Pending` (not yet in force) on the detail badge, the list filter/badge, the dashboard, the vendor
  rollup, and the export — consistently, so counts and badges never disagree (no #294-class split).
- **Self-healing with no re-evaluation and no schema change.** The doc flips to its real verdict the day it
  becomes effective, purely from the read overlay, exactly as Expired/ExpiringSoon already do. `EffectiveDate`
  already exists — no migration.
- The compliance rate treats a future-effective doc like any `Pending` doc (#318): excluded from the
  denominator, not counted as a graded non-compliant one, so a batch of early-uploaded renewals doesn't
  falsely tank the rate.
- Scoped so the NonCompliant and Expired populations are untouched — the #257 mutual-exclusivity and #294
  boundary invariants are preserved.

### Negative
- The date-driven verdict now lives in **one more axis** (effective date) alongside the expiration overlay, so
  a future contributor adding a new SQL read site must remember to mirror **both** the expiry window and the
  future-effective demotion. Mitigated by centralizing the instant bound in one helper
  (`NotYetEffectiveLowerBoundInclusive`), by the demotion living in the single `Effective` deriver every
  non-SQL surface calls, and by cross-surface tests that fail if a site drifts (the dashboard-count ==
  deep-linked-list pin, extended to the future-effective case).
- The **stored** `ComplianceStatus` and the **effective** (read) status now diverge for a future-effective doc
  (stored Compliant, reads Pending). A reader who queries the column directly, bypassing the overlay, would see
  the un-demoted value. This is the same contract the Expired/ExpiringSoon overlay already established (a
  stored-Compliant doc past its date reads Expired) — the column is a cache; the overlay is the truth — but the
  divergence is now also reachable via the effective date. `ComputeOutcome` carries a prominent comment so the
  read-only choice isn't "fixed" into a persist.

### Neutral
- The expiry-pipeline future buckets (30/60/90/beyond) and the `expiresWithin` filter are **not** demoted —
  they answer "when does each doc expire," a date-timing question independent of the compliance verdict, so a
  future-effective doc still appears in its expiry bucket (same informational-vs-liability scoping ADR 0033
  applies to the future buckets). Reminders remain expiry-driven and are unchanged on the verdict axis.

## Alternatives considered

### Option A — Persist the demotion in `ComputeOutcome` / the sweep (store `Pending`)
Write `Pending` to the column for a future-effective affirmative doc. **Rejected**: it erases the rule verdict,
and no production path re-runs rule evaluation on an effective-date crossing, so the doc would be stranded at a
stale `Pending` after becoming effective — a new stale-verdict bug. The read-only overlay self-heals instead.
(This is why `ComputeOutcome` and the sweep deliberately keep storing the real verdict — the observable
outcome, "reads Pending until effective, then reads its real verdict," is identical and additionally
self-healing.)

### Option B — Add a new `ComplianceStatus.NotYetEffective` value
A dedicated status would read most precisely. **Rejected** per the ticket: a new value ripples through frontend
badges, dashboard counts, list filters, export, and plan surfaces — a large surface for a state `Pending`
already models adequately ("graded, nothing affirmative to assert yet").

### Option C — Also demote NonCompliant / block future-effective on ExpiringSoon differently
**Rejected**: masking a future-effective **failing** cert to Pending would hide a real deficiency, and the
narrow "only affirmative outcomes demote" rule keeps the change from touching the NonCompliant/Expired
populations, minimizing risk to the #257/#294 invariants.

## References

- Tickets: [#362](https://github.com/neboxdev/complidrop/issues/362), [#48](https://github.com/neboxdev/complidrop/issues/48) (rolling bug-fix epic)
- ADRs: [0033](0033-document-supersession-expired-liability.md) (the supersession half of #362 — Amendment 2 is the sibling effective-date fix on the Expired-liability side), [0027](0027-compliance-date-window-boundaries.md) (the date↔instant boundary convention this reuses for the effective date), [0030](0030-compliance-verdict-combined-unit-of-work.md) (the combined unit of work that persists the real verdict `ComputeOutcome` returns), [0009](0009-no-at-time-zone-on-timestamptz-in-raw-sql.md) (why the effective-date comparison is a bare `timestamptz` test). ADR 0040 (added concurrently under #383) is unrelated to this decision.
- Code: `Services/ComplianceStatusDeriver.cs` (`Effective`, `IsFutureEffective`, `NotYetEffectiveLowerBoundInclusive`), `Services/ComplianceCheckService.cs` (`ComputeOutcome` — read-only note), `Endpoints/DashboardEndpoints.cs`, `Endpoints/DocumentEndpoints.cs`, `Endpoints/VendorEndpoints.cs`, `Services/ExportService.cs`, `BackgroundServices/ComplianceSweepBackgroundService.cs`
