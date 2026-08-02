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

## Amendment 1 (2026-07-30) — the document-level carve-out is conditional, and does not generalize

[ADR 0048](0048-never-graded-document-asserts-no-affirmative-verdict.md) (#443) applies this ADR's
coverage-exclusion reasoning to a sibling state — a document with **zero `ComplianceCheck` rows**, which the
machine never graded at all — but **mirrors its demotion onto the document-level surfaces too**, the opposite
of "What this deliberately does NOT do" above and of Option B.

That is not a reversal of this decision, and nothing here changes: a `ManualRequired` document's counts,
badges and export are still untouched. The two differ because this ADR's carve-out rests on a **verifiable
premise**, not on a general principle — *"the documents list already renders a distinct `ManualRequired`
extraction badge next to the compliance badge, so the document surface is not misleading."* For a
never-graded document no such second badge exists (the extraction badge reads a healthy "Read" and the "What
we checked" panel is simply empty), so leaving the document surfaces affirmative there would **create** the
#294-class split rather than avoid it. The substantive difference: a `ManualRequired` document **was** graded
and its verdict is real — only its extraction *inputs* are distrusted, two separate axes — whereas a
never-graded document has no verdict on either axis.

**The test when extending either decision:** ask whether some other surface already discloses the state
beside the compliance badge. If yes, confine the demotion to the vendor rollup (this ADR). If no, mirror it
everywhere (ADR 0048).

## Amendment 2 (2026-08-01) — a terminally FAILED extraction NOBODY CONFIRMED is excluded from in-force coverage too

Decision 2's exclusion list named only `ManualRequired`. `ExtractionStatus.Failed` joins it, on the same
read-time clause in `ComputeCoverage`, with the same read-only contract (no persisted `ComplianceStatus`
change, no document-level surface touched) — **and with the same human exit**, which for `Failed` cannot be
the status. See "The exit" below; a version of this clause without one is a regression, not a safety fix.

**Why.** `ExtractionStatus` is the ONLY column carrying this ADR's distrust, and it is not durable across a
re-read. `DocumentEndpoints.Reextract` (#365 / [ADR 0050](0050-reextract-refuses-a-live-extraction-claim.md))
re-arms the queue by writing `Pending` over whatever was there — including `ManualRequired`. So clicking
"Read again" on a distrusted document immediately flipped the vendor rollup from ActionNeeded to **Covered**
on the strength of the OLD verdict, computed from the extraction the system itself flagged unreliable. If the
re-read then failed terminally, the row landed on `Failed` — which the exclusion list did not name — and
nothing restored the distrust: `ExtractionWorker.MarkFailed` and `RecordFailedAttempt` write
`ExtractionStatus` only, leaving `ComplianceStatus`, the `ComplianceCheck` rows and the `DocumentField`s
exactly as the distrusted read left them. That Covered label was then **permanent**, with no human ever
confirming the extraction — the same silent false-affirmative this ADR exists to prevent, reached by a
different route. The at-risk population is precisely the per-field confidence gate above: the value parses,
the rules pass, the stored verdict is a real `Compliant`, and only the trust flag ever dissented.

An extraction the system could not COMPLETE is at least as untrustworthy as one it distrusted, so the honest
rollup answer is the same one: a required type covered only by such documents reads ActionNeeded.

**The exit — `IsManuallyVerified`, because the status cannot be one.**

Decision 2's exclusion is explicitly scoped *"until a human confirms the extraction on the document detail
page"*, and the first cut of this amendment inherited the clause without inheriting the exit. That
over-reached, and in exactly the direction this ADR is supposed to protect against — it just pointed the
wrong way. The detail page offers a manual-entry affordance FOR the failed case (*"We couldn't pull the
details from this file automatically. Enter the key details below and we'll check them against the
requirements when you save."*, with `effective_date` / `expiration_date` / `general_liability_limit`), and
that Save is a real grade: `UpdateFields` mirrors the typed values into the canonical inputs and folds
`IComplianceCheckService.ApplyEvaluationAsync` into its own unit of work (ADR 0030), so the
`ComplianceCheck` rows and the stored verdict come from values a human vouched for. `DocumentGrading.IsGraded`
is true and `ComplianceStatusDeriver.Effective` returns the real `Compliant` — and a status-only `Failed`
exclusion dropped the document BEFORE the deriver ever ran. The vendor then read ActionNeeded with no
extraction badge, no reason and no remedy on that page, **and no endpoint could move it back** — worse than
the pre-amendment reading, and a demand for a remedy the product cannot offer.

The exit cannot be the STATUS: `DocumentEndpoints.ResolveManualReview` deliberately refuses to move a
`Failed` row (*"Failed is its own louder error state"* — it is `Completed`/`ManualRequired` that its
`wasSettled` allow-list governs), so a confirmed document stays `Failed` forever. What that same helper DOES
write, unconditionally and on every caller (`PUT /fields`, `PUT /verify`), is `IsManuallyVerified`. So the
clause is `ExtractionStatus == Failed && !IsManuallyVerified`, and `DocCoverageInfo` carries the flag in both
projections. Amendment 2's target population is untouched: a distrusted document nobody confirmed carries
`IsManuallyVerified == false` when it lands on `Failed`.

The flag is STICKY — nothing clears it, so a document confirmed once, then successfully re-extracted, then
failed again reads as confirmed on values a human never saw. Accepted deliberately: it is strictly narrower
than the pre-amendment reading (which counted every `Failed` document), the alternative is the schema change
[#459](https://github.com/neboxdev/complidrop/issues/459) owns, and the `ManualRequired` half is unaffected —
that clause has no `IsManuallyVerified` escape, so a re-extraction that lands back on `ManualRequired`
re-excludes the document regardless of what was confirmed before.

**Scope, deliberately narrow.**

- **`Pending` / `Processing` are NOT excluded.** The window while a re-read is genuinely in flight is bounded
  and self-healing — the worker resolves it within a poll — so excluding in-flight statuses would drop every
  legitimately-compliant vendor to ActionNeeded during any ordinary re-extract. A test asserts the Covered
  reading in that window so widening the clause stays a visible choice rather than a silent drift.
- **The document-level surfaces stay untouched**, exactly as in the original decision and Amendment 1. The
  documents list already renders a distinct `Failed` extraction badge ("Couldn't read") beside the compliance
  badge and the detail page carries the extraction-error card, so the Amendment 1 test — *does some other
  surface already disclose this state beside the compliance badge?* — answers **yes** here, the same as for
  `ManualRequired`. Demoting the counts would be the #294-class split, not a fix.
- **Nothing is persisted.** Recovering the distrust signal across a re-arm (a separate column, so
  extraction-trust stops sharing one column with pipeline position) is a schema change and is deliberately
  NOT done here; it is [#459](https://github.com/neboxdev/complidrop/issues/459). (This line originally
  pointed at #366 — a loose "it touches the same table" association, not a shared decision. #366 shipped
  as ADR 0030 Amendment 1 with no schema change at all, so the pointer is corrected to the ticket that
  actually owns the column.) The read-time exclusion closes the permanent case without one.

## Amendment 3 (2026-08-02) — the distrust signal moves to its own column; the exclusion reads trust, not status

[ADR 0052](0052-extraction-trust-is-its-own-column.md) (#459) gives this ADR's distrust signal a column of
its own, `Document.ExtractionTrust` (`Trusted` / `Distrusted`), because `ExtractionStatus` was carrying both
**pipeline position** and **extraction trust** — which is why the re-arm could destroy the trust in the first
place, and why Amendment 2 had to reconstruct it from where the document landed.

**What changes here.** `ComputeCoverage`'s extraction clause is now a single test on the new column, and
`DocCoverageInfo` carries neither `ExtractionStatus` nor `IsManuallyVerified` any more. Everything else in
this ADR stands: the per-field confidence gate is untouched, the exclusion is still read-time only (the
stored `ComplianceStatus` is never rewritten), and the document-level carve-out survives unchanged — the
documents list still discloses the state with its own extraction badge, which is Amendment 1's test for when
a demotion may stay confined to the rollup (ADR 0052 Option F).

**Two things this ADR recorded as accepted are now closed, and one is reversed:**

- **The `IsManuallyVerified` stickiness (Amendment 2, "The exit") is CLOSED.** The clause no longer reads
  that flag. `DocumentEndpoints.ResolveManualReview` writes `Trusted` instead, which is the same human exit —
  still reachable from a `Failed` row, where the status can never be the exit — except that a later
  extraction re-decides it. A document confirmed once, successfully re-extracted, then failed again is
  distrusted again, rather than reading as confirmed on values no human ever saw. The flag itself stays on
  the entity and the detail DTO; it just stops gating coverage.
- **"Nothing is persisted" (Amendment 2, § Scope) no longer holds, deliberately.** That line pointed at
  #459 as the ticket that owned the column, and this is it. The *verdict* is still never persisted from a
  read; what is persisted is the trust fact itself, written by the events that establish or undermine the
  document's basis.
- **The in-flight carve-out is REVERSED for distrusted documents.** Amendment 2 asserted, with a test, that a
  re-armed distrusted document reads **Covered** while the re-read is in flight. It now reads
  **ActionNeeded**, and the test's middle assertion was updated to match. That is not a widening of the
  status clause — the clause cannot see the status at all. An in-flight document is excluded exactly when it
  is `Distrusted`, and **two** paths reach that state (this paragraph originally named only the first, and
  was corrected in round 2 of the #459 review — the second path did not exist when it was written):

  1. **It was already distrusted and the re-arm carried the distrust through.** Every queue writer
     (`Reextract`, `RecordFailedAttempt`'s retry arm, `RequeueInterruptedAsync`) leaves trust alone, so the
     document read ActionNeeded the instant before the click. This exclusion is *continuous*.
  2. **`ResolveManualReview` distrusts it while it is in flight.** Trust follows READABILITY on **every**
     status — only the escalation back to `ManualRequired` is gated on the document having been settled,
     because only that write could de-queue it — so a `PUT /fields` or `PUT /verify` that leaves an
     unreadable canonical value on a `Pending`/`Processing` row withdraws trust there too. This is a
     genuinely NEW mid-read demotion and it is **accepted**: it is fail-CLOSED (ADR 0040 — a value nothing
     can parse must not roll up as Covered), it is user-initiated one request earlier, and it is disclosed —
     the detail page's `ManualReviewCard` names the unreadable field, rendering off `unreadableFields`
     rather than off the extraction status. It clears when the read the user is watching lands cleanly
     (`PersistSuccess` re-decides trust) or when a later save leaves nothing unreadable. Pinned by
     `A_field_save_that_leaves_an_unreadable_value_demotes_a_cert_that_is_already_in_flight`.

  The carve-out's stated rationale — *"excluding in-flight statuses would drop every legitimately-compliant
  vendor to ActionNeeded during any ordinary re-extract"* — is fully intact under both paths and separately
  pinned: an ordinary re-extract of a **trusted** document keeps its vendor Covered at `Pending` and at
  `Processing`, because nothing in the queue path touches trust.

**One new residue, recorded in ADR 0052 § Consequences:** during a Railway deploy overlap the OLD container
can write `ManualRequired`/`Failed` without writing trust, leaving a distrusted document reading `Trusted`
until it is re-read or confirmed. Not closable by the column default (the exposed transition is an `UPDATE`,
not an `INSERT`); bounded by the health-check overlap; pinned by a test so it is known rather than
surprising.

## References

- Tickets: [#401](https://github.com/neboxdev/complidrop/issues/401), [#443](https://github.com/neboxdev/complidrop/issues/443) (Amendment 1), [#365](https://github.com/neboxdev/complidrop/issues/365) (Amendment 2), [#459](https://github.com/neboxdev/complidrop/issues/459) (Amendment 3), [#48](https://github.com/neboxdev/complidrop/issues/48) (rolling bug-fix epic)
- ADRs: [0040](0040-unreadable-canonical-value-fails-closed.md) (the unreadable-value trigger this dovetails with — both raise `ManualRequired`), [0041](0041-future-effective-not-yet-in-force-reads-pending.md) (the read-only-overlay pattern and the vendor-rollup in-force test this extends), [0030](0030-compliance-verdict-combined-unit-of-work.md) (the single unit of work the gate stays inside), [0052](0052-extraction-trust-is-its-own-column.md) (Amendment 3 — the distrust signal's own column)
- Code: `Services/VerdictBearingFields.cs` (the verdict-bearing set), `BackgroundServices/ExtractionWorker.cs` (`PersistSuccess`, `ManualReviewConfidenceThreshold`), `Endpoints/VendorEndpoints.cs` (`ComputeCoverage`, `DocCoverageInfo` + both projections), `Endpoints/DocumentEndpoints.cs` (`ResolveManualReview` — the exit; `IsManuallyVerified` until Amendment 3, `ExtractionTrust` after it)
