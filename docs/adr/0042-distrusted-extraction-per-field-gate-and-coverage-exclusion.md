# 0042. A distrusted extraction is routed to review by a per-field confidence gate and does not roll up to "Covered"

- **Status:** accepted
- **Date:** 2026-07-23
- **Deciders:** Ruben G. (founder), Claude (implementing #401)

## Context

Two mechanisms decided that an extraction the system itself distrusts could still be graded and reported
as coverage in force:

1. **The confidence gate that raises `ManualRequired` was an AVERAGE.**
   `ExtractionWorker.PersistSuccess` routed a document to `ExtractionStatus.ManualRequired` when the mean
   confidence across ALL extracted fields fell below `0.7`. But a certificate mostly reads cleanly: a dozen
   fields at 0.95 and one `expiration_date` at 0.3 average to ~0.90, comfortably above the gate — and that
   one field is exactly the value a compliance verdict turns on. Averaging a mis-read of the single
   verdict-bearing field into a sea of confidently-read incidental fields (policy number, insurer name,
   certificate holder) hides it completely.

2. **Even a `ManualRequired` document's verdict rolled up to "Covered".** The compliance verdict is computed
   in the same transaction as the extracted inputs (ADR 0030) and stored regardless of extraction status, so
   "Needs your review" and a stored `Compliant` sit side by side. `VendorEndpoints.ComputeCoverage` judged a
   required document type "covered" purely on the effective `ComplianceStatus` of its documents — so a vendor
   whose only certificate for a required type was an extraction the system flagged for a human still read
   **Covered**.

For a product whose core claim is the verdict, a verdict the machine distrusts must not silently become a
green "Covered" badge. A single mis-read critical field — a limit, an expiration, the additional-insured
party — is precisely what flips a verdict, and both mechanisms above let it through. This is the same
compliance-safety class as #362 (ADR 0041) and #383 (ADR 0040): a silent false-affirmative concealing a real
gap. It dovetails with #383, which already routes a document to `ManualRequired` when a canonical value is
non-blank but unreadable.

## Decision

Two changes, on the two axes above.

**1. A per-field confidence gate on the VERDICT-BEARING fields, in addition to the average.**
`PersistSuccess` routes a document to `ManualRequired` when ANY verdict-bearing field the model actually
returned came back below the **same** `0.7` threshold the average uses — regardless of the average. The
existing triggers are all kept (average, the model's `NeedsReprocessing` signal, and #383's unreadable
canonical value); this adds a fourth, independent one.

- **The verdict-bearing set** (`Services/VerdictBearingFields.cs`, one named collection, never scattered
  literals) is the fields whose value backs a compliance verdict, derived from what extraction emits
  (`ExtractionPrompts` COI field list) and what the rule catalog grades (`requirements.ts` fieldNames /
  `ComplianceCheckService.LookupValue`): the date fields `effective_date` and `expiration_date`, and the
  coverage-limit / flag fields `general_liability_limit`, `auto_liability_limit`,
  `professional_liability_limit`, `umbrella_limit`, `liquor_liability_limit`, `workers_comp_limit`, and
  `additional_insured`. The three typed-column names reuse the `CanonicalDocumentFields` constants so the
  spelling lives in one place. `general_liability_limit` already IS the each-occurrence reading (the prompt
  reads that ACORD 25 cell into it), so there is no separate occurrence/aggregate field to gate.
- **Scope, deliberately.** The license/certification IDENTITY fields (`license_number`, `license_type`,
  `certification_number`/`_name`) are OUT: their date requirement is already covered by `expiration_date`,
  and gating every identity field would drag much of the license/permit corpus into manual review for little
  verdict-safety gain. The gate covers universal dates + insurance coverage — the fields the ticket names.
- **Only a present-but-low-confidence field trips it.** A field the model OMITTED never fires the gate — the
  prompt tells the model to omit what it cannot find, and a missing required field is the rule engine's
  concern, not the confidence gate's.
- **The threshold is a single shared constant** (`ExtractionWorker.ManualReviewConfidenceThreshold = 0.7`)
  referenced by both the average gate and the per-field gate, so the two can never drift apart.

**2. A `ManualRequired` document does not contribute in-force coverage in the vendor rollup.**
`ComputeCoverage` now excludes a document with `ExtractionStatus == ManualRequired` from the in-force set,
alongside the existing effective-status test. A required type covered ONLY by `ManualRequired` documents
falls to **ActionNeeded** — a genuine gap surfaces, exactly like an expired-only or non-compliant-only
type — until a human confirms the extraction on the document detail page. `DocCoverageInfo` (and both its
projections, the list query and the detail query) now carries `ExtractionStatus`.

This is the read-time realization of the ticket's "hold the verdict at Pending while ManualRequired"
suggestion, **localized to the vendor coverage rollup** rather than applied by mutating the stored verdict.

### What this deliberately does NOT do

- **No persisted `Pending` (or any changed `ComplianceStatus`) for a `ManualRequired` document.** ADR 0040
  and 0041 keep the REAL stored rule verdict precisely so a document self-heals when re-evaluated; persisting
  `Pending` would strand it, because nothing re-runs rule evaluation on an extraction-status change. Change 2
  is a **read-time** exclusion inside `ComputeCoverage` only.
- **No change to the document-level surfaces.** The dashboard `compliant`/`expiringSoon` counts, the
  documents-list `?status=` filter and badges, the CSV/PDF export, and the per-document compliance badge are
  all UNTOUCHED. Extraction-trust and rule-verdict are two separate axes: the documents list already renders a
  distinct `ManualRequired` **extraction** badge next to the **compliance** badge, so a distrusted document is
  already visible there as "Needs your review" beside its verdict. Demoting the document-level verdict too
  would conflate the axes and create count-vs-badge splits (the #294 class of bug). The vendor rollup is the
  one surface that collapses many documents into a single Covered/ActionNeeded judgement with no room to show
  the extraction badge, so it is where the trust axis must fold in.
- **No new `ComplianceStatus` value.** As in ADR 0040/0041, a new verdict would ripple through badges, counts,
  filters, export and plan surfaces. `ExtractionStatus.ManualRequired` already carries "the system distrusts
  this extraction"; this decision just teaches the vendor rollup to read it.

### Interaction with the existing invariants

- **ADR 0030 (combined unit of work) is preserved.** The per-field gate is computed and the status set
  *before* `PersistSuccess`'s single `SaveChanges`, so inputs + verdict + review flag still commit as one unit.
- **ADR 0040 (#383) dovetails.** The unreadable-canonical-value trigger and the low-confidence-field trigger
  both raise `ManualRequired`; a document flagged for EITHER reason is now excluded from in-force coverage by
  change 2, closing the rollup half for both.
- **ADR 0041 (#362) is preserved.** The rollup's future-effective demotion (a not-yet-in-force cert reads
  Pending, so it isn't in-force coverage) is unchanged; the `ManualRequired` exclusion is an additional
  independent clause on the same in-force test.

## Consequences

### Positive
- A single mis-read verdict-bearing field can no longer average away into a healthy-looking Completed
  document, and a document the system distrusts can no longer roll up to a green "Covered" vendor badge. The
  vendor rollup now surfaces the gap the same way it does for expired or non-compliant coverage.
- The gate is scoped and self-healing: confirming the extraction (an edit / verify on the detail page) clears
  `ManualRequired`, and the vendor immediately reads Covered again if the verdict warrants it.

### Negative
- **A noisier `ManualRequired` population and more ActionNeeded vendors.** An org whose model output regularly
  carries one fuzzy verdict-bearing field will see more "Needs your review" documents and more vendors reading
  ActionNeeded until confirmed. Mitigated by the narrow scope (only verdict-bearing fields, only when actually
  present and below the gate) — the incidental-field mis-reads that make up most low-confidence noise do not
  trip it.
- **The stored verdict and the vendor rollup can now disagree** for a `ManualRequired` document (stored
  Compliant, rollup treats it as not-in-force). This mirrors the stored-vs-effective divergence ADR 0041
  already established, confined to the rollup, and is the point: the rollup asserts present coverage, which a
  distrusted extraction cannot back.

### Neutral
- No schema change, no migration, no new status value. The gate reuses `ExtractionStatus.ManualRequired`; the
  rollup reads a column the document already has.

## Alternatives considered

### Option A — Persist `Pending` while `ManualRequired`
Store `Pending` for a distrusted document so it drops out of every count and rollup at once. **Rejected** for
the same reason as ADR 0041 Option A: nothing re-runs rule evaluation when a human clears `ManualRequired`
(the sweep only does date transitions), so the document would be stranded at a stale `Pending`. A read-time
exclusion in the one surface that matters self-heals instead.

### Option B — Also demote the document-level counts / badge / export
Apply the trust axis everywhere the verdict is shown. **Rejected** as out of scope and as a source of
count-vs-badge splits: the documents list already shows a separate `ManualRequired` extraction badge next to
the compliance badge, so the document surface is not misleading; conflating the two axes on the counts would
reintroduce the #294 class of split. The vendor rollup is the only surface with no room for the separate badge.

### Option C — Drop the low-confidence field's confidence below the average gate
Lower the mean instead of adding a per-field gate. **Rejected** (as in ADR 0040 Option D): it corrupts
`ExtractionConfidence`, a measured quantity the UI uses, to move a status, and it is exactly the averaging
blindness this decision fixes.

### Option D — Gate the entire rule-catalog field set, including license/certification identity fields
Include `license_number`, `license_type`, `certification_number`/`_name`. **Rejected** as scope creep for
little gain: the date requirement for those document types is already covered by `expiration_date`, and gating
every identity field would push much of the license/permit corpus into manual review. Revisitable if the
review population shows those mis-reads matter.

## References

- Tickets: [#401](https://github.com/neboxdev/complidrop/issues/401), [#48](https://github.com/neboxdev/complidrop/issues/48) (rolling bug-fix epic)
- ADRs: [0040](0040-unreadable-canonical-value-fails-closed.md) (the unreadable-value trigger this dovetails with — both raise `ManualRequired`), [0041](0041-future-effective-not-yet-in-force-reads-pending.md) (the read-only-overlay pattern and the vendor-rollup in-force test this extends), [0030](0030-compliance-verdict-combined-unit-of-work.md) (the single unit of work the gate stays inside)
- Code: `Services/VerdictBearingFields.cs` (the verdict-bearing set), `BackgroundServices/ExtractionWorker.cs` (`PersistSuccess`, `ManualReviewConfidenceThreshold`), `Endpoints/VendorEndpoints.cs` (`ComputeCoverage`, `DocCoverageInfo` + both projections)
