# 0048. A never-graded document asserts no affirmative verdict — on every read surface

- **Status:** accepted
- **Date:** 2026-07-30
- **Deciders:** Ruben G. (founder), Claude (implementing #443)

## Context

A document that was **never graded against a single rule** could roll up to **"Covered"** on the vendor page
and print **"Expiring soon"** into the auditor-facing vendor package — with an **empty "What we checked"
panel** behind it. Three links, each individually defensible, composed into an affirmative-coverage
overclaim:

1. `ComplianceCheckService.ComputeOutcome`'s zero-applicable-rules branch stores
   `expiringSoon ? ExpiringSoon : Pending`. So a never-graded document inside the 30-day expiry window is
   **stored ExpiringSoon**.
2. `ComplianceStatusDeriver.Effective`'s expiry overlay promoted even a stored `Pending` to `ExpiringSoon`
   inside that window.
3. `VendorEndpoints.ComputeCoverage` counts `Compliant or ExpiringSoon` as in-force coverage, so the
   required type resolved to `Covered`; `ExportService` then printed the same label into the CSV, the audit
   PDF and the vendor package.

The tell that step 1's date-preservation had leaked into being a *verdict*: **the same absence of grading
read `Pending` at 31 days to expiry and `ExpiringSoon` at 29.** The date, not the evidence, decided whether
the product asserted coverage. `ExpiringSoon` is honest about the *date* but reads as an affirmative
*verdict* on every surface that groups it with `Compliant` — which is every surface that matters.

**The state is reachable today, not hypothetical.** The applicable-rules filter compares `DocumentType` with
ordinal `==` (case-**sensitive**) while `ComputeCoverage` matches a required type with
`OrdinalIgnoreCase`. A document typed `"COI"` against a `"coi"` rule therefore matches **zero** rules — and
is still counted by the rollup as a document *of that required type*. The two disagree, and the disagreement
resolved in the direction that asserts coverage. [#373](https://github.com/neboxdev/complidrop/issues/373)
(ADR 0045) closed the *ingress* of new non-canonical types but deliberately does not launder already-stored
rows, so the existing population stays reachable. The other zero-check branches — no checklist assigned, an
empty checklist — land in exactly the same state.

Severity per `.claude/reviewers.md` § Project severity anchors: *"Copy that overclaims compliance or legal
certainty: major"*. The auditor-facing export is the artifact that carries the claim.

This is the third instance of one recurring class — a silent false-affirmative concealing a real gap — after
[ADR 0040](0040-unreadable-canonical-value-fails-closed.md) (#383, an unreadable value),
[ADR 0041](0041-future-effective-not-yet-in-force-reads-pending.md) (#362, a not-yet-in-force cert) and
[ADR 0042](0042-distrusted-extraction-per-field-gate-and-coverage-exclusion.md) (#401, a distrusted
extraction). It reuses their machinery deliberately.

## Decision

**A document with zero `ComplianceCheck` rows has no verdict. When its verdict would otherwise read an
affirmative `Compliant` or `ExpiringSoon`, it reads `Pending` instead — on every read surface.**

### 1. One recognizer: `Services/DocumentGrading.cs`

"Did anything actually grade this document?" is answered in one place, in the
`DocumentSupersession` / `PlanDocumentScope` / `DocumentFieldReadability` shape:

- `IsGraded(int complianceCheckCount) => complianceCheckCount > 0` — the in-memory predicate and the ONE
  threshold. A single check row is enough: a document graded against one rule HAS been graded, pass or fail.

It ships **exactly that one shape**, deliberately. Every SQL read site needs the fact *inside* a composite
predicate or a projection, where an EF `Expression` cannot be invoked, so each spells it inline as
`d.ComplianceChecks.Any()` (§4). An `Expression` mirror would therefore be a second public form with **no
production caller** — a shape nothing exercises, pinned only against itself. `ComplianceCheck` carries no
`DeletedAt` and has no query filter, so `Any()` counts exactly what `IsGraded` counts; the two forms that
actually ship are pinned against **the `ComplianceChecks` table itself**, not against each other, because
both traverse the same navigation and a filter landing on it would move them identically.

**A check row is the right recognizer** because it is the artifact the engine emits when it actually
measured something, and every `ComputeOutcome` branch that certifies nothing returns zero checks **and**
`ClearExistingChecks: true`. Keying on "zero applicable rules" instead would have covered only one of the
three branches and would need re-deriving at read time from data the read sites don't load.

### 2. The decision lives in `ComplianceStatusDeriver.Effective`, not in each read site

`Effective` gains a **required** `isGraded` parameter and a demotion clause structurally identical to
ADR 0041's future-effective one, on a third axis:

```csharp
if (overlaid is Compliant or ExpiringSoon && !isGraded) return Pending;
```

- **Required, not defaulted.** A default would be fail-open — a new read surface that forgot it would
  silently re-assert the coverage this ADR removes. Same forcing function ADR 0041 used when it added
  `effectiveDate`.
- **Placed after the expiry if/else**, so it catches the null-expiry path too (the #362 review's S2 lesson),
  and gated on the **overlaid** status so it demotes both a stored `ExpiringSoon` and the stored `Pending`
  the expiry window just promoted into one.
- **Expired is never demoted.** A lapsed date is a real, un-graded fact and a present liability; softening
  it to `Pending` would hide a gap — this ticket's own failure mode, inverted.
- **NonCompliant is never demoted.** Unreachable without a failed check row, but excluded anyway so the
  clause can only ever move a document OUT of the affirmative tally.
- **A stored `Compliant` demotes too.** Not reachable from `ComputeOutcome` (an affirmative verdict always
  commits with its check rows, ADR 0030), but reachable transiently: `ComplianceEndpoints.DeleteRule`
  hard-deletes a rule's check rows and re-grades *after* the commit, as do the seed's orphan cleanup and
  `SystemTemplateDedup`. If that re-grade never lands, the stored `Compliant` is backed by nothing — fail
  **closed** rather than certify off missing evidence.

Because the vendor rollup already judges in-force coverage through `Effective`, **the ADR 0042 exclusion
arrives for free**: a never-graded document reads `Pending`, so it falls out of the in-force set exactly as
an expired or not-yet-in-force cert does, and a required type covered only by never-graded documents reads
`ActionNeeded`. No second clause in `ComputeCoverage`.

### 3. Read-only, never persisted

`ComputeOutcome` and the nightly sweep keep storing the **real date verdict** — deliberately unchanged.
Persisting `Pending` would be wrong twice over: it would strand the document (nothing re-runs rule
evaluation on a *grading* change any more than on an effective-date crossing), and `Pending` is a
load-bearing written value elsewhere — `ExtractionWorker` claims documents on `ExtractionStatus == Pending`
and request code must never write it. As a read overlay the document **self-heals**: add a governing rule,
correct its type, or assign a checklist, and the re-evaluation writes check rows and the stored verdict
surfaces on its own.

### 4. Every read surface mirrors it — including the document-level ones

This is the point where this decision **diverges from ADR 0042**, and the divergence is deliberate.

ADR 0042 left the document-level surfaces (dashboard counts, `?status=` list + badges, export, per-doc
badge) untouched for a `ManualRequired` document, on an explicit and **verifiable** premise: *"the documents
list already renders a distinct `ManualRequired` extraction badge next to the compliance badge, so a
distrusted document is already visible there as 'Needs your review' beside its verdict."* The rollup was
"the one surface with no room for the separate badge."

**That premise is false for a never-graded document.** No *badge* anywhere says "nothing graded this" — the
extraction badge reads a healthy "Read", and the detail page's "What we checked" panel is simply **empty**
beneath the verdict. The distinction is also substantive, not cosmetic: for `ManualRequired` the document
*was* graded and the verdict is real (only its extraction *inputs* are distrusted) — two axes. Here there is
no verdict on either axis. Applying ADR 0042's carve-out would leave the vendor rollup saying `ActionNeeded`
while the list badge said "Expiring soon" for the same document: a #294-class split, and precisely the
outcome the ticket names as the failure mode to avoid.

There is, however, **one existing surface that explains** the state rather than badging it, and this
decision widens what it has to explain: the document detail page's "Not checked yet" card
(`NotCheckedExplainer`), which renders when `complianceStatus === "Pending"` and *names the cause*. Before
this ADR it had a two-cause taxonomy — no vendor, or a vendor with no checklist — derived from
`complianceChecks.length === 0`. `ComputeOutcome` reaches zero checks from more branches than that, and the
`applicableRules.Count == 0` one (a checklist that exists but whose rules govern other document types) is
exactly the population §"Context" describes. It used to read `ExpiringSoon` inside the window, so the card
never rendered for it; now it does, and re-deriving the cause from the check count would print the **false**
claim "this vendor doesn't have a requirements checklist yet" plus a CTA that resolves nothing — while the
vendor rollup simultaneously read `ActionNeeded` against the checklist the card denied existed.

**The governing rule for this card is that it may only name a cause it can actually know**, which the
review pass turned into four requirements:

- **`DocumentDetail` carries `vendorHasChecklist`** — the backend knows whether a template is assigned and
  the page cannot. Same reasoning as `DocumentDetail.UnreadableFields` under ADR 0040: sourcing the fact
  from the server is the point, not incidental.
- **…and `vendorChecklistRuleCount` beside it**, because "a template is assigned" is too coarse for the
  taxonomy the card names. A vendor assigned an **empty** checklist has one, so it landed in the arm whose
  copy asserts the checklist *holds requirements that govern other types* — false about the customer's own
  data, with the wrong remedy. The two fields together separate all four causes; neither alone does (a bare
  `count > 0` cannot tell an empty checklist from no checklist). A soft-deleted template reports `0` and
  lands on the empty-checklist arm, where the remedy is equally right.
- **Only a SETTLED extraction gets a cause named.** A terminally failed extraction never reaches
  `ApplyEvaluationAsync` — `ExtractionWorker`'s only call site is inside `PersistSuccess` — so it sits at
  the default `Pending` compliance with zero checks, indistinguishable from a non-governing checklist by
  the check count alone. The card gates on an **allow-list** (`Completed` / `ManualRequired`, the pair
  `DocumentEndpoints.ResolveManualReview` treats as settled) rather than on excluding
  `Pending`/`Processing`, so it also fails closed for any extraction state added later. `ProcessingErrorCard`
  states the real reason, and every `Failed` write sets the `ProcessingError` that renders it.
- **A non-canonically-stored type leads with the type correction, and shows the raw value.**
  `documentTypeLabel` normalizes case, so the headline population — a document stored `"COI"` against a
  `"coi"` rule — renders "Certificate of Insurance" on both sides: the old copy told the user nothing on
  the checklist applied to Certificate of Insurance while the vendor page visibly listed a Certificate of
  Insurance requirement, and offered to add a **duplicate that still would not grade the document**. That
  arm now prints the RAW stored string and offers a one-click "Set type to {label}" (a `PATCH`, which
  re-grades) — necessary because the type picker at the top of the page is case-insensitive and already
  *displays* the right label, so re-picking it fires no change event. A value the vocabulary does not
  recognise at all has no one-click target, so the copy points at that picker, which does show such a value
  as un-selectable. `frontend/src/lib/document-types.ts`'s `canonicalDocumentType` answers "is the stored
  string exactly canonical?"; it is **presentational only** — it chooses which remedy leads, never which
  cause is named.

The resulting four arms: no vendor (assign picker) · no checklist (set one up) · empty checklist (add the
first requirement) · a checklist with rules that do not govern this document (add a covering requirement,
or — when the stored type is non-canonical — correct the type).

**Known residue, deliberately not closed here:** a rule delete hard-deletes its check rows and re-grades
*after* the commit (§2). If that re-grade never lands, a document whose checklist *does* have a governing
rule reaches zero checks, and the last arm's copy overstates. Closing it would need the applicable-rules
filter re-implemented at read time — a second copy of a grading predicate, the exact drift this ADR's
one-recognizer rule exists to prevent — to describe a transient state that self-heals on the next
evaluation. Recorded rather than mirrored.

So the demotion is mirrored everywhere:

| Surface | How |
| --- | --- |
| Document detail badge | `Effective` with `doc.ComplianceChecks.Count` (already `Include`-loaded for the panel) |
| Documents-list badge | `Effective` with a projected `ComplianceChecks.Count` scalar |
| Documents-list `?status=` arms | SQL: `d.ComplianceChecks.Any()` on the Compliant/ExpiringSoon arms, its negation as a third OR in the Pending arm |
| Dashboard `compliant` / `expiringSoon` | SQL: `d.ComplianceChecks.Any()` |
| Dashboard compliance-rate denominator | SQL: a clause exactly parallel to the future-effective exclusion |
| Vendor rollup (list + detail projections) | through `Effective`, via `DocCoverageInfo.ComplianceCheckCount` |
| CSV / audit PDF / vendor package | through `Effective`, plus §5 below |
| Dashboard `awaitingReview` tile | SQL: the shared `ComplianceStatusDeriver.ReadsPending` predicate — §6 |
| Document detail "Not checked yet" card | `DocumentDetail.vendorHasChecklist` + `vendorChecklistRuleCount` — see the four-cause taxonomy above |

The SQL sites spell the fact inline as `d.ComplianceChecks.Any()` rather than composing a shared
expression — the same hand-mirroring ADR 0041's date bounds already require (an EF `Expression` cannot be
invoked inside a composite predicate or a projection), covered the same way: by cross-surface tests pinning
each count against its own deep-linked list.

The **one exception** is `?status=Pending`, which is a whole-entity predicate on both of its consumers and
therefore *can* be shared — and must be, because its second consumer is the dashboard count that deep-links
to it (§6). It lives in `ComplianceStatusDeriver.ReadsPending(today)`, the `DocumentSupersession.IsCurrent`
shape.

### 5. The export discloses the count, not just the demoted label

"Awaiting review" and "we measured zero requirements against this" are different facts, and the auditor is
entitled to the second. One shared `ExportService.ComplianceCell` renders the verdict for all three
artifacts so they cannot diverge, and each surface discloses in its own established idiom:

- **The two PDFs** append `" (no requirements checked)"` inline, the same shape as the `"(superseded)"`
  qualifier beside it (#327) — and the two compose.
- **The CSV** gains a `RequirementsChecked` **column** (the integer count) rather than a parenthetical,
  because `Compliance` there is a machine-filterable cell, exactly like `Superseded` next to it.

**The exported count is DISTINCT RULES, not check rows** (added by the #468 review; `CheckCountsAsync`).
This column is the only surface that PRINTS the number instead of thresholding it at zero, and
[ADR 0030](0030-compliance-verdict-combined-unit-of-work.md) § Consequences accepts that a concurrent
re-grade can leave a document holding BOTH writers' check rows — two rows citing the same rule. That
residue is scoped there as a detail-page display desync, where the count and the list of rows the reader
is looking at agree; an auditor's CSV has no such list beside it, so a raw row count would state "2
requirements checked" against a checklist holding exactly one rule — a claim about the EVIDENCE rather
than a rendering artifact, and the export is precisely where reliance forms. Counting distinct rules
leaves everything else byte-identical: `IsGraded` asks only `> 0`, and the distinct count is zero exactly
when the row count is, so the two PDFs' annotation, the demoted verdict, and every `d.ComplianceChecks
.Any()` read site are unmoved. Pinned against the real doubled state by
`ExtractionWorkerStaleBasisTests.A_regrade_that_deletes_the_check_rows_this_persist_staged_costs_no_extraction`,
which constructs the interleave and then reads this cell.

`ComplianceCell` is `internal` so its wording can be pinned by a unit test: both PDFs are
FlateDecode-compressed and not text-assertable. Each PDF's rows are `internal` seams for the same reason —
`ExportService.VendorPackageLinesAsync` and `ExportService.AuditReportRowsAsync`, **each performing its own
check-count read**, each pinned against a seeded org holding one graded and one never-graded document. A
`%PDF` smoke test cannot distinguish a correctly-wired count map from an empty one: an empty map would
annotate *every* row of an auditor's report "(no requirements checked)", and a dropped annotation would
print an affirmative verdict for a document nothing graded. Both directions are invisible in the compressed
bytes, so both are asserted at the seam that builds the rows rather than at the artifact.

### 6. Demoting a document must not make it disappear

A demotion that removes a document from `compliant` and `expiringSoon` and puts it nowhere else leaves it in
**no number on the dashboard at all** — the opposite of this ADR's purpose, which is to *tell* the user
nothing graded it. So the dashboard gains an **`awaitingReview`** tile, deep-linked to `?status=Pending`.

- It counts the **whole effective-Pending population** (genuine Pending + the ADR 0041 and ADR 0048
  demotions), not a never-graded-only number, because that is the population its label names and the
  population the list it links to contains. A narrower count over a wider list would be the same
  count-vs-list split, one level down.
- It is computed from the **same predicate** that builds that list (`ReadsPending`), so the two cannot drift.
- The `expiringSoon` tile is **relabelled** from "Expiring within 30 days" to **"Expiring soon"** — the
  `DisplayLabels.Compliance` wording of the status it deep-links to. That count is a *verdict* count (it
  already excluded a not-yet-in-force cert under ADR 0041, and now a never-graded one), so a pure DATE label
  put it in disagreement with the date-only "Next 30 days" bucket on the same screen about the same
  document. The date question stays owned by the "When documents expire" card, which is untouched.
- The **counts themselves are not softened** — that would re-open the overclaim. Only the label changes.

## Consequences

### Positive

- The product no longer asserts coverage it never established. A never-graded document reads `Pending`
  identically on the badge, the list, the dashboard, the vendor rollup and all three export artifacts, so
  the vendor rollup and the document-level surfaces can never tell an auditor two different stories.
- **The grading axis stops depending on the calendar.** The same document no longer reads `Pending` at 31
  days to expiry and `ExpiringSoon` at 29.
- **The empty "What we checked" panel now matches its badge.** `Pending` ("Awaiting review") beside zero
  checks is coherent; "Expiring soon" beside zero checks was the contradiction.
- The compliance rate stops silently penalising these documents: a never-graded doc stored `ExpiringSoon`
  sat in the denominator while being structurally unable to reach the numerator.
- **Self-healing, no schema change, no migration, no new status value** — the same properties as ADR 0041/0042.
- Fail-closed on a check-row population that a rule delete emptied but whose re-grade never landed.

### Negative

- **A third axis to mirror.** A future contributor adding a SQL read site must now reproduce the expiry
  window, the future-effective demotion **and** the grading clause. Mitigated by the required `isGraded`
  parameter (every non-SQL surface fails to compile without it) and by the cross-surface count-vs-list pins.
- **More `Pending` documents and more `ActionNeeded` vendors** for orgs holding legacy non-canonical or
  mistyped document types — the population ADR 0045 deliberately does not launder. That is the gap becoming
  visible, which is the point, but it is a visible change for existing customers.
- **The stored verdict and the read verdict diverge on one more axis** (stored `ExpiringSoon`, reads
  `Pending`). Same contract ADR 0041 established; a reader querying the column directly, bypassing the
  overlay, sees the un-demoted value.
- **One extra scalar per read.** A `COUNT`/`EXISTS` correlated subquery on the list, dashboard, rollup and
  export queries. At the documented scale (≤1,000 documents per org) this is noise, and no check *rows* are
  ever shipped to compute it.

### Neutral

- The **expiry pipeline** buckets (30/60/90/beyond), the `expiresWithin` filter, and **reminders** are
  untouched — they answer "when does this expire", a date question independent of the verdict, and reminders
  never consulted `ComplianceStatus` at all. Same informational-vs-liability scoping ADR 0041 applies.
- `ComputeOutcome`'s zero-applicable-rules branch, its `ClearExistingChecks: true`, and the nightly sweep's
  `Pending → ExpiringSoon` transition are all unchanged.
- **Four** raw stored-`ComplianceStatus` readers remain un-overlaid and are **out of scope**, because each
  predates this decision and already ignores the ADR 0027/0041 overlays too: the portal upload-status poll
  (`VendorPortalEndpoints`), the account data export (`AuthEndpoints` — exporting the stored record is
  arguably correct), `ComplianceEndpoints.OrgStatus`, and `ComplianceEndpoints.RunCheck`
  (`POST /api/compliance/check/{documentId}`), which returns the freshly-stored verdict in its response
  envelope — so it answers `ExpiringSoon` for exactly the document class this ADR makes read `Pending`
  everywhere else. No user sees it today: its only caller discards the body (`api.post<void>`) and refetches
  the overlaid surfaces. Recorded here rather than overlaid silently, so the next contributor reading "every
  read surface" is not misled; if that `status` field ever acquires a reader, it must be overlaid first.

## Alternatives considered

### Option A — Exclude never-graded documents in `ComputeCoverage` only, leaving the document surfaces alone
The literal ADR 0042 precedent, and the ticket's own first suggestion. **Rejected**: ADR 0042's carve-out
rests on the documents list already carrying a separate `ManualRequired` extraction badge, and there is no
equivalent badge for "nothing graded this". Fixing the rollup and the export while the list badge still read
"Expiring soon" would manufacture the #294-class count-vs-badge split the ticket explicitly names as the
failure mode to avoid.

### Option B — Persist `Pending` for a never-graded document at write time
Stop the problem at the source in `ComputeOutcome`. **Rejected** for both ADR 0041 Option A's reason and a
sharper one: `ComplianceStatus.Pending` is not an inert label. Persisting a manufactured `Pending` erases
the real date verdict with nothing to restore it, and request-path code writing `Pending` collides with the
extraction worker's claim semantics. The read overlay self-heals; a write does not.

### Option C — Stop the `Pending → ExpiringSoon` promotion in the deriver, and nothing else
The narrowest reading of the ticket's second bullet. **Rejected as insufficient**: it addresses only link 2
of the chain. `ComputeOutcome` *stores* `ExpiringSoon` for a never-graded doc in the window (link 1) and the
nightly sweep writes it too, so a stored-`ExpiringSoon` never-graded document would still roll up to
`Covered` with the promotion gone.

### Option D — A new `ComplianceStatus.NotGraded` value
Most precise reading. **Rejected** for the third time, on ADR 0040/0041/0042's reasoning: a new value ripples
through frontend badges, dashboard counts, list filters, export and plan surfaces. `Pending` already means
"graded, but nothing affirmative to assert yet", and `ComputeOutcome`'s own zero-applicable-rules branch
*already returns `Pending`* outside the expiry window — this decision makes the in-window case agree with
the out-of-window one rather than inventing a state.

### Option E — Fix the case-sensitivity disagreement instead
Make the applicable-rules filter case-insensitive so a `"COI"` document matches a `"coi"` rule. **Rejected
as neither sufficient nor safe here**: it closes one door into the never-graded state, not the state itself
(no checklist, an empty checklist, and a genuinely non-governing type all still reach it), and widening what
a compliance rule *governs* is a live-data grading change that deserves its own ticket and its own
sign-off — the same reasoning ADR 0045 applied to the blank-`DocumentType` wildcard arm. Noted here so the
disagreement is recorded rather than silently absorbed.

### Option F — Denormalize a `ComplianceCheckCount` column on `Document`
Avoid the correlated subquery. **Rejected**: a migration plus a backfill plus a new invariant to keep in
sync, to optimize a count that is free at this scale. The counter drifting from the rows would recreate this
exact bug with an extra step.

## References

- Tickets: [#443](https://github.com/neboxdev/complidrop/issues/443), [#48](https://github.com/neboxdev/complidrop/issues/48) (rolling bug-fix epic)
- ADRs: [0042](0042-distrusted-extraction-per-field-gate-and-coverage-exclusion.md) (the coverage-exclusion precedent this follows, and whose document-level carve-out it deliberately does not), [0041](0041-future-effective-not-yet-in-force-reads-pending.md) (the read-only-overlay mechanism this reuses verbatim on a third axis), [0040](0040-unreadable-canonical-value-fails-closed.md) (the same fail-closed posture), [0045](0045-canonical-document-type-vocabulary.md) (why the never-graded population is reachable and is not laundered), [0030](0030-compliance-verdict-combined-unit-of-work.md) (why an affirmative stored verdict normally commits with its check rows — and, Amendment 4, why the exported count is distinct RULES: its accepted mixed-check-row residue is scoped to DISPLAY, which §5's column would otherwise have turned into an evidence claim), [0027](0027-compliance-date-window-boundaries.md) (the date-window convention the demotion sits beside)
- Code: `Services/DocumentGrading.cs` (the recognizer), `Services/ComplianceStatusDeriver.cs` (`Effective`, `ReadsPending`), `Endpoints/VendorEndpoints.cs` (`ComputeCoverage`, `DocCoverageInfo` + both projections), `Endpoints/DocumentEndpoints.cs` (list status arms + projection, detail), `Endpoints/DashboardEndpoints.cs` (`Stats`), `Services/ExportService.cs` (`ComplianceCell`, `CheckCountsAsync`, `NeverGradedAnnotation`, `AuditReportRowsAsync`, `VendorPackageLinesAsync`, the CSV `RequirementsChecked` column), `frontend/src/app/(dashboard)/documents/[id]/page.tsx` (`NotCheckedExplainer`), `frontend/src/lib/document-types.ts` (`canonicalDocumentType`)
