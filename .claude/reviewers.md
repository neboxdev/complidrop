# CompliDrop — review addendum

Read at review time by every generic reviewer persona in the machine-level claude-kit
(`~/.claude/agents/`) and by the /start, /review, /plan and /epic-review skills. This
file owns the project's review-time facts: rosters, deliberate patterns, sensitive
areas, commit scopes, scale. The invariants themselves live in `docs/adr/` (indexed
one-per-line in CLAUDE.md § Domain invariants) — this file points at them, it does
not restate them.

**Sync rule:** a code change that alters one of these facts updates this file in the
same PR. That is the whole point of this file existing — the previous kit hardcoded
these facts into agent prompts and three of them drifted stale.

## Extra personas

- **Code roster** (added to the 5 core reviewers in /review and /start Phase 4):
  - `compliance-claims-reviewer` — product claims vs actual code behavior
- **PM roster** (added to the 5 core PM reviewers in /plan Phase 2 and /pm-review):
  - `legal-compliance-reviewer` — privacy, US-regulatory, AI-processing, liability

Both are defined in this repo's `.claude/agents/`.

## Do NOT flag (deliberate decisions — flagging these is reviewer noise)

- Portal READ routes (`/api/portal/{token}` info + upload-status GETs) are uncapped
  per-token with a 240/hr per-IP backstop — deliberate (#242). The UPLOAD route caps
  (`portal-token` 10/hr, `portal-ip` 30/hr, per-link `MaxUploads`, per-org monthly cost
  ceiling) DO apply and their absence would be a bug.
- `IgnoreQueryFilters()` / `SystemDbContext` inside background workers and system
  contexts — by design. In request-path code it IS a blocker (tenant leakage).
- Idempotency records replay the winner's exact response for as long as the row exists;
  `ExpiresAt` is a future-GC hint, NOT a replay filter; replays are not 409s
  (ADR 0029, ADR 0032).
- Document supersession de-counts ONLY the Expired liability (dashboard count,
  expiry-pipeline expired bucket, `?status=Expired` list, reminder windows) and the
  audit export annotates-but-keeps; deliberately NOT applied to compliant /
  nonCompliant / expiringSoon or future pipeline buckets (ADR 0033 + Amendments 1 & 2).
  A superseder must BOTH extend coverage (`ExpirationDate >=` this doc's) AND be
  continuous (`EffectiveDate` null or `<=` this doc's `ExpirationDate` — ADR 0033
  Amendment 2 / #362): a future-effective renewal that opens a live coverage gap does
  NOT supersede, by design (date-adjacency-conservative — effective on the old expiry
  supersedes, one day after does not).
- Future-effective verdict (#362 / ADR 0041): a not-yet-in-force cert (`EffectiveDate`
  a date strictly after today) that would read Compliant/ExpiringSoon reads `Pending`
  instead — a READ-ONLY overlay. `ComputeOutcome` and the nightly sweep DELIBERATELY
  keep storing the REAL rule verdict (never `Pending`) so the doc self-heals to it the
  day it becomes effective — do NOT flag that as a missed demotion; persisting `Pending`
  would be the bug. The demotion IS mirrored on every READ surface (documents-list
  filter + badge, dashboard compliant/expiringSoon counts AND the rate denominator,
  vendor rollup, CSV/PDF export via `ComplianceStatusDeriver.Effective`). A read site
  that decides Compliant/ExpiringSoon from `.ComplianceStatus` WITHOUT the EffectiveDate
  demotion IS a real finding (a #294-class count-vs-badge split). Expired still wins
  outright; a hard fail stays NonCompliant (never masked to Pending). The vendor rollup
  (`VendorEndpoints.ComputeCoverage`) consults the best CURRENTLY-IN-FORCE cert per
  required type (ANY doc reading Compliant/ExpiringSoon via the overlay), NOT strictly
  the newest upload (#362 review / ADR 0041): a vendor still covered by an in-force
  earlier cert who pre-uploads a future-effective renewal (reads Pending) stays Covered,
  while an expired-only / non-compliant-only / future-effective-only type still reads
  ActionNeeded. Do NOT "simplify" it back to latest-upload-only — that reintroduces the
  false-uncovered regression the review caught.
- A normal document delete RETAINS its blob (ADR 0013); the sample-demo clear DELETES
  its blob (ADR 0028). Both directions are deliberate.
- Vendor contact-email validation is ADR 0038; the review-time facts that follow are
  pointers into it, not a second copy of the rationale.
  - Two email validators coexist ON PURPOSE: `Services/ContactEmail.IsWellFormed` (vendor
    contact email — strict) and `AuthEndpoints.IsValidEmail` (account email — lax,
    `Contains('@')`). Different evidence, different strictness — do not "unify" them.
  - The pair that MUST agree is `Services/ContactEmail.cs` <-> `frontend/src/lib/contact-email.ts`;
    drift between THOSE two is a real finding. It is pinned mechanically by the shared corpus
    `api/CompliDrop.Api.Tests/SharedFixtures/contact-email-cases.json` (add a case THERE, not to
    one suite) plus a BMP-walking class-vs-predicate test on each side. The corpus lives in the
    api test tree so `api-ci`'s `api/**` filter covers a corpus-only edit; `frontend-ci` names it
    explicitly. Moving it back under `docs/` silently un-enforces the guarantee.
  - Both mirrors spell the blank class out as explicit `\uXXXX` ranges rather than `\s`, and strip
    edges with a LINEAR SCAN rather than a regex. Both are load-bearing, not style: `\s` differs
    between the engines, and the regex form is quadratic. Do not "simplify" either.
    If you re-measure the regex: the hostile shape is blanks in the MIDDLE with a non-blank at
    BOTH ends. Leading/trailing padding is linear and will wrongly clear the pattern.
  - `valid` cases in the corpus mean "the predicate accepts this", NOT "this is a good address" —
    some are bidi controls listed to pin a range bound. Bidi/invisible-format controls being
    accepted is a KNOWN deferred decision (ADR 0038 Consequences), not an oversight to re-flag.
- Vendor update is BLOCK-UNTIL-FIXED on a malformed contact email (#369): `UpdateVendor`
  validates the submitted address whether or not this request changed it, so a vendor
  whose STORED address is already malformed (written by the pre-#369 unguarded edit path)
  must be corrected or cleared before unrelated edits land. Deliberate — rationale and the
  rejected alternative are in ADR 0038. Finding those rows without opening each vendor is
  [#430](https://github.com/neboxdev/complidrop/issues/430), not a defect here.
- The sample-demo row is excluded from the plan-limit population on every enforcing /
  reporting surface (dashboard fence, portal fence, Settings `documentsUsed`) via the
  shared `PlanDocumentScope.CountsTowardLimit` predicate, and never generates mail
  (sample documents dropped from the reminder query; the fictional
  `sample-vendor@example.com` dropped from the recipient list even for a real document
  assigned to that vendor; the manual email-link action refuses it). That mail skip is
  deliberately keyed on the ADDRESS (`SampleData.IsUndeliverableSampleAddress`), NOT on
  `Vendor.IsSample` — `UpdateVendor` repurposes the sample vendor without clearing the
  flag, so a flag-based skip silently drops a real vendor's mail. But the dashboard's
  `totalDocuments` still COUNTS the sample — that asymmetry is deliberate ("what's in
  my account" vs "what do I owe for"), so a 4-real+1-sample org showing "5 documents"
  on the dashboard and "4 / 5" in Settings is correct (ADR 0028 Amendment 1, #367).
- An UNREADABLE canonical value (non-blank, won't parse into its typed column) fails
  CLOSED everywhere (ADR 0040, #383). The review-time facts that follow are pointers
  into it, not a second copy of the rationale.
  - `LookupValue`'s raw-string fallback is narrowed ON PURPOSE — a canonical field whose
    typed column is null falls back only when the raw value RE-PARSES. That is the
    fail-open path that let a `required` rule pass off text nothing else could read;
    it is not an oversight and must not be "restored".
  - The `EvaluateRule` guard sits AHEAD of the operator switch deliberately: `contains`
    would otherwise substring-match the raw text of an unparseable date. Do not push it
    down into the individual operators.
  - The unreadable note is deliberately NOT `"Field missing."` — the two assert opposite
    facts about the certificate. Do not unify them.
  - The request-side escalation to `ManualRequired` lives in ONE place —
    `DocumentEndpoints.ResolveManualReview`, which BOTH `UpdateFields` and `MarkVerified`
    call — and is computed from the document's RESULTING state, never from the field names
    the request submitted. A request that doesn't mention the unreadable field (empty-fields
    save, unrelated-field save, bare `PUT /verify`) must NOT resolve the review; a version
    keyed on the submitted names IS a bug.
  - That escalation fires only from a SETTLED status (`Completed`/`ManualRequired`), measured
    BEFORE the resolve. Load-bearing: overwriting `Pending` de-queues the document (the worker
    claims on `ExtractionStatus == Pending`), and `Processing`/`Failed` are the worker's own
    states. A missing-status-guard version IS a bug — all three exclusions are pinned by a
    Theory, so a loosened `!= Pending` goes red.
  - A JSON `null` in `ExtractionFields` is an ABSENCE on both sides: `RawFieldValue` maps
    `JsonValueKind.Null`/`Undefined` to null. Its old `GetRawText()` fallback returned the
    literal 4-character string `"null"`, which the reader called unreadable while the writer
    called it Blank — the same value, two verdicts. Restoring that arm re-opens the split.
  - There is ONE mechanism for "does this document carry an unreadable canonical value?" —
    `Services/DocumentFieldReadability.cs` (`TryGetUnreadableValue` /
    `UnreadableCanonicalFields` / `HasUnreadableCanonicalValue`), a dedicated static class in
    the `DocumentSupersession` / `PlanDocumentScope` shape. All four askers go through it:
    `EvaluateRule`'s guard, `LookupValue`'s narrowed fallback, `ResolveManualReview`, and
    `GetDocument` (the `unreadableFields` DTO field). `ExtractionWorker.PersistSuccess`
    asks it too, of the document it just wrote — it used to accumulate its own per-field
    `TypedColumnResult` set, a second mechanism nothing pinned equal. A re-introduced
    independent copy IS a finding. Last-value-wins now falls out structurally (the JSON
    mirror and the typed columns are both last-wins and the predicate reads only those);
    accumulating per occurrence sent a document to review over a value it no longer holds.
  - `DocumentDetail.UnreadableFields` exists because the detail page CANNOT re-derive it:
    the amber field outline keys on confidence, and an unreadable value is high-confidence
    (1.0 after a manual edit), so nothing gets outlined. A TypeScript re-implementation of
    "can this parse?" would drift from the .NET parse it mirrors — sourcing the names from
    the backend walk is the point, not incidental.
  - Deliberately NOT done: a new `ComplianceStatus` value, softening a computed verdict
    to `Pending`, rejecting the edit with a 400, or extending the flag to non-canonical
    fields. All four are recorded rejections in ADR 0040 § Alternatives.
- A DISTRUSTED extraction is ADR 0042 (#401); the review-time facts that follow are
  pointers into it, dovetailing with ADR 0040/0041.
  - `ExtractionWorker.PersistSuccess` routes to `ManualRequired` when ANY VERDICT-BEARING
    field (`Services/VerdictBearingFields.cs` — the dates + insurance coverage-limit / flag
    fields, NOT the license/cert identity fields) came back below the gate, EVEN IF the
    field average clears it. Both gates reference ONE shared
    `ExtractionWorker.ManualReviewConfidenceThreshold` (0.7) — a per-field gate on a
    DIFFERENT threshold is a real finding. An ABSENT field never trips it (only a
    present-but-low-confidence one).
  - `VendorEndpoints.ComputeCoverage` excludes a `ManualRequired` doc from in-force
    coverage, so a required type covered ONLY by distrusted docs reads ActionNeeded (like
    an expired-only type). READ-TIME only — the stored `ComplianceStatus` is untouched (no
    persisted `Pending`), extraction-trust and rule-verdict are separate axes.
  - Deliberately NOT applied to the document-level surfaces (dashboard compliant/
    expiringSoon counts, `?status=` list/badges, CSV/PDF export, per-doc compliance badge):
    the list already shows a separate `ManualRequired` extraction badge beside the
    compliance badge, and demoting the counts too would create a #294-class count-vs-badge
    split. The vendor rollup is the one surface with no room for the separate badge. Do NOT
    flag the untouched document-level counts as a missed demotion — that inversion is the bug.
    Note the CONTRAST with ADR 0048 below, which mirrors its demotion onto the document-level
    surfaces: that is not an inconsistency, it turns on whether a second badge already discloses
    the state. Here one does; for never-graded none does.
- A NEVER-GRADED document is ADR 0048 (#443) — zero `ComplianceCheck` rows, i.e. nothing was ever
  measured against it. The facts that follow are pointers into it, not a second copy.
  - There is ONE recognizer, `Services/DocumentGrading.cs`, shipping exactly ONE shape:
    `IsGraded(int checkCount)` (the in-memory threshold — ONE check row is enough). It deliberately has
    NO EF `Expression` mirror — every SQL site needs the fact inside a composite predicate where an
    expression can't be invoked, so a mirror would be a public form with zero production callers. Adding
    one back IS a finding, not an improvement. The DECISION is in `ComplianceStatusDeriver.Effective`,
    whose `isGraded` parameter is REQUIRED, not defaulted: a default would be fail-open for the next read
    surface added. A read site that re-derives the threshold instead of asking `DocumentGrading` IS a
    real finding.
  - The demotion is READ-ONLY, exactly like ADR 0041's. `ComputeOutcome`'s zero-applicable-rules
    branch and the nightly sweep DELIBERATELY keep storing the real date verdict (`ExpiringSoon`
    inside the window) — do NOT flag that as a missed demotion; persisting `Pending` would be the
    bug twice over (it strands the doc, and `Pending` is what `ExtractionWorker` claims on).
  - Mirrored on EVERY read surface, document-level INCLUDED — badge, `?status=` arms, dashboard
    `compliant`/`expiringSoon` AND the rate denominator, vendor rollup, CSV/PDF export. This
    deliberately DIVERGES from ADR 0042's document-level carve-out, whose stated reason (a separate
    `ManualRequired` extraction badge already sits beside the compliance badge) is FALSE here: no
    badge anywhere says "nothing graded this", and the detail page's "What we checked" panel is
    simply EMPTY. Demoting the rollup while leaving the badge affirmative would BE the #294 split.
  - The demotion must not make a document DISAPPEAR. The dashboard carries an `awaitingReview` tile
    deep-linked to `?status=Pending` — the WHOLE effective-Pending population (genuine Pending + the
    #362 and #443 demotions), counted from the SAME `ComplianceStatusDeriver.ReadsPending(today)`
    expression that builds that list, so the two can't drift. That shared predicate is the ONE
    exception to the spell-it-inline rule below, and it is allowed precisely because it is a
    whole-entity predicate on both consumers (the `DocumentSupersession.IsCurrent` shape). The
    `expiringSoon` tile is labelled "Expiring soon" (the `DisplayLabels.Compliance` wording of the
    status it links to), NOT a date range: it is a verdict count, and a date label put it in
    disagreement with the date-only "Next 30 days" pipeline bucket about the same document. Softening
    a COUNT to fix such a disagreement would re-open the overclaim — change the label, not the number.
  - The detail page's "Not checked yet" card NAMES the cause, so it may only ever name a cause it can
    KNOW. Zero check rows has FOUR causes: no vendor, no checklist, an EMPTY checklist, or a checklist
    whose rules all govern OTHER document types. Only the backend can tell the last three apart, so
    `DocumentDetail` carries BOTH `vendorHasChecklist` AND `vendorChecklistRuleCount` — neither alone
    separates them (a bare `count > 0` can't tell an empty checklist from no checklist), and the coarse
    boolean alone put an empty checklist in the arm asserting it holds rules for other types. Deriving
    the cause from `complianceChecks.length` prints a FALSE claim about a vendor that plainly has a
    checklist, plus a dead-end CTA — same reasoning as `DocumentDetail.UnreadableFields` (ADR 0040). A
    frontend re-derivation of the CAUSE IS a real finding. Three more properties of that card, each
    load-bearing rather than cosmetic:
    - It gates on an ALLOW-LIST of settled extraction statuses (`Completed`/`ManualRequired`, the
      `ResolveManualReview` pair), NOT on excluding `Pending`/`Processing`. A terminally `Failed`
      extraction never reaches `ApplyEvaluationAsync` (the worker calls it only inside `PersistSuccess`),
      so it sits at default-`Pending` with zero checks and used to get a confident, false cause named
      over it; `ProcessingErrorCard` states the real reason. Re-widening that gate to an exclusion list
      IS a real finding — the allow-list is what fails closed for a future extraction state.
    - When the stored `documentType` is NOT exactly canonical (the `"COI"` vs `"coi"` headline
      population), the arm prints the RAW stored value and leads with a one-click "Set type to {label}"
      PATCH. Load-bearing: `documentTypeLabel` normalizes case, so the old copy denied a requirement the
      vendor page visibly lists, and its "add a requirement" CTA added a DUPLICATE that still wouldn't
      grade the doc — while `DocumentTypeSelect` is case-insensitive and already displays the right
      label, so re-picking it fires no change event and the "just fix the type" advice was unfollowable.
      `frontend/src/lib/document-types.ts`'s `canonicalDocumentType` is PRESENTATIONAL — it picks which
      remedy leads, never which cause is named.
    - KNOWN residue, recorded in ADR 0048 §4 — do not re-report: a rule delete hard-deletes its checks
      and re-grades AFTER the commit, so a never-landed re-grade reaches zero checks with a governing
      rule present, and the last arm's copy overstates. Closing it needs the applicable-rules filter
      re-implemented at read time (a second grading predicate), for a transient that self-heals.
  - The vendor rollup has NO grading clause of its own — `ComputeCoverage` gets the exclusion for
    free because `Effective` already reads such a doc `Pending`. Adding a second explicit clause
    there (the shape ADR 0042's `ManualRequired` exclusion needs) would be a duplicate, not a fix.
  - `Expired` and `NonCompliant` are NEVER demoted: a lapsed date is a real un-graded fact and a
    present liability, and the clause must only ever move a doc OUT of the affirmative tally. A
    stored `Compliant` with zero checks DOES demote — transiently reachable when a rule delete
    hard-deletes its checks and the post-commit re-grade never lands; fail-closed is intended.
  - SQL sites spell the fact inline as `d.ComplianceChecks.Any()` — an EF expression cannot be invoked
    as one clause of a multi-clause lambda (the dashboard counts, most sharply the rate denominator's
    NEGATED composite) nor as a projected scalar (the list / rollup / export projections), the same
    hand-mirroring ADR 0041's date bounds already require. Covered by the count-vs-deep-linked-list pins;
    a NEW SQL read arm missing the clause IS a real finding. The lone shared predicate is `ReadsPending`
    (see the dashboard bullet above) — it has two consumers that must agree and both take a whole-entity
    predicate, and it is pinned against `Effective` over the full status × expiry × effective-date ×
    grading grid so one of its three OR arms can't drift out of the deriver. The documents-list
    Compliant/ExpiringSoon arms are the SAME shape and COULD take an `Expression`; they stay inline
    because each has ONE consumer, so there is no drift to prevent — not because they structurally
    can't. Do not "fix" that as an inconsistency in either direction.
  - The export discloses the COUNT as well as the demoted label, through the one shared
    `ExportService.ComplianceCell`: the two PDFs append `" (no requirements checked)"` inline (the
    `"(superseded)"` shape, and the two compose), the CSV instead carries a `RequirementsChecked`
    column so its `Compliance` cell stays machine-filterable. That asymmetry is deliberate. Each PDF's
    rows go through its OWN `internal` seam carrying its OWN check-count read —
    `AuditReportRowsAsync` and `VendorPackageLinesAsync` — because a `%PDF` smoke test can't tell a
    wired-up count map from an empty one (empty annotates EVERY row; a dropped annotation certifies a
    doc nothing graded), and neither is visible in the FlateDecode bytes. Inlining either back into its
    builder un-pins that wiring.
  - The ordinal-case-SENSITIVE applicable-rules filter vs `ComputeCoverage`'s case-INsensitive
    required-type match is a KNOWN live disagreement (it is how the never-graded state is reachable
    without any legacy data). ADR 0048 Option E records why it was NOT unified here — widening what
    a rule governs is a live-data grading change needing its own ticket. Do not re-report it as new.
  - Expiry-pipeline buckets, `expiresWithin` and REMINDERS are untouched (date questions, not verdict
    ones — reminders never read `ComplianceStatus` at all). FOUR raw stored-status readers stay
    un-overlaid and out of scope, because they ignore the ADR 0027/0041 overlays too and always did:
    the portal upload-status poll, the `AuthEndpoints` account data export,
    `ComplianceEndpoints.OrgStatus`, and `ComplianceEndpoints.RunCheck` (its `status` field has no
    reader — the only caller is `api.post<void>`). Recorded, not swept; if `RunCheck`'s `status` ever
    gains a reader it must be overlaid first.
  - Test fixtures: `IntegrationTestBase.MarkGradedAsync` is the ONE seam for "this seeded doc was
    actually graded". A fixture writing a stored `Compliant`/`ExpiringSoon` with no check row is
    seeding a state no production path reaches, and since #443 it silently tests the never-graded
    path instead of the one it names — that IS a real finding in a new test. Two documented side
    effects, not bugs: it picks the backing rule by `SortOrder` then `Id` (Postgres gives no row
    order without an ORDER BY, and the rule shows up in the "What we checked" panel), and with NO
    rule in the org it mints a real, org-visible `Graded-{guid}` template — a test asserting on
    template lists or requirement counts should seed its own checklist, which the helper then reuses.
    The per-suite `SeedDocAsync`/`SeedVendorDocAsync` helpers in `ComplianceVerdictFreshnessTests`,
    `FutureEffectiveCoverageGapTests` and `VendorEndpointsTests` therefore call it UNCONDITIONALLY and
    carry no `graded` knob: those suites test the date / effective-date / rollup axes, and an opt-out
    nothing passes is a dead parameter whose comment misleads. `NeverGradedCoverageTests.SeedDocAsync`
    is the one that keeps the knob — ungraded is its DEFAULT, because that is its subject.
- The canonical document-type vocabulary is ONE list — `Services/CanonicalDocumentTypes.cs`
  (#373, [ADR 0045](../docs/adr/0045-canonical-document-type-vocabulary.md)) — and
  `ExtractionWorker.PersistSuccess` coerces the model's `documentType` through it before that
  string overwrites `Document.DocumentType`. Facts here that look like bugs and are not:
  - A BLANK/ABSENT extracted type falls back to the STORED type (itself normalized), NOT to
    `other`. `documentType` is `required` in both providers' structured-output schemas, so a blank
    is off-spec and carries no information, while the stored type is usually the uploader's own
    dropdown pick (and the model's own type hint) — demoting a deliberate `license` to `other`
    would drop every license rule and strand the document in the zero-applicable-rules
    never-graded state #373 exists to close. Only a NON-blank answer overwrites, and a POSITIVE `"other"` counts as
    an answer. "Absent" is reachable because both clients' `MapResult` map a missing/JSON-null
    `documentType` to `null` rather than to the literal `"other"` — a clean-looking `?? "other"`
    there is the bug, not the fix.
  - `ComplianceEndpoints.UpsertRule` — the OTHER operand of the ordinal comparison — REJECTS an
    unknown type with `400 validation.document_type` instead of coercing it. Deliberate asymmetry
    with the worker: silently retyping a compliance RULE would change what it governs. Mis-cased
    input is still folded (spelling, not meaning).
  - `DocumentEndpoints`' `AllowedDocumentTypes` literal is GONE — #389 collapsed it (ADR 0046 §7 /
    ADR 0045 § "Option E"), so the PATCH type edit and BOTH upload paths call the vocabulary
    directly and `CanonicalDocumentTypeTests`' set-equality pin was retired with it (a pin comparing
    the vocabulary to itself is vacuous, not a safety net). The contract is now asserted over HTTP in
    `RequestInputLengthTests`. Re-introducing a second literal in an endpoint IS a real finding.
  - The two INGRESS paths COERCE an unknown type to `other`; `UpdateDocument` (PATCH) and
    `UpsertRule` REJECT it with a 400. Deliberate three-way split, not an inconsistency: an upload
    must not cost a vendor their file over a stray form value (and the portal 400 would arrive after
    the blob is already stored), while a human deliberately re-typing a document or writing a rule is
    choosing what gets graded.
  - `ComputeOutcome`'s blank-`DocumentType` WILDCARD arm survives on purpose even though
    `UpsertRule` can no longer write a blank type. Deleting it would silently change grading for
    pre-#373 blank-type rules (live-data behaviour change); a legacy blank-type rule must be
    re-typed before it can be saved again. Do not flag the arm as dead code, and do not flag the
    write/read asymmetry as an inconsistency — it is recorded in ADR 0045.
  `Normalize` needs no length clamp — it only ever returns a member of the vocabulary, so the
  `varchar(100)` column is safe by construction (pinned against the EF model). The sibling
  `DocumentSubType` has no vocabulary and is DROPPED when over-length (nothing could match a
  truncated half-value); the `DocumentField` columns and `ProcessingError` are TRUNCATED instead
  (an extracted field is user-facing content). `ExtractionWorker.Clamp` is a one-line delegate to the
  shared `Services/ColumnClamp.cs` (#372 / ADR 0044), the same shape as
  `ComplianceCheckService.ClampToColumn` — do not re-inline either body.
  **NEVER-GRADED IS NOT FAIL-SAFE** — do not repeat the old "nothing is certified, so it's safe"
  framing. CLOSED by [#443](https://github.com/neboxdev/complidrop/issues/443) /
  [ADR 0048](../docs/adr/0048-never-graded-document-asserts-no-affirmative-verdict.md); see the
  ADR 0048 block below for the review-time facts. The stored write side is UNCHANGED — do not flag
  `ComputeOutcome`'s zero-applicable-rules branch still storing `expiringSoon ? ExpiringSoon :
  Pending`, nor the sweep's `Pending -> ExpiringSoon` transition; the demotion is read-only.
  Three KNOWN GAPS, recorded in ADR 0045 — do not re-report them as new findings:
  - **Legacy rows are not laundered.** Coercion runs only on the next EXTRACTION and nothing
    re-extracts a processed row, so a pre-deploy non-canonical type persists (never graded, own
    supersession group, invisible to `?type=coi`) until a human re-types it. #389 does not touch
    stored rows either. Deliberately NOT fixed by a data migration — because laundering production
    rows is a destructive operation needing human sign-off (measure the population first), NOT
    because the residue is harmless. Since #443 / ADR 0048 such a row at least reads `Pending`
    everywhere instead of rolling up to `Covered`, so the residue is now VISIBLE rather than
    silent — the population still needs re-typing.
  - **A clamped field can be narrowed by a manual edit.** `ExtractionFields` (jsonb) keeps the FULL
    value AS EXTRACTED, but the detail page binds its input to the clamped `DocumentField.FieldValue`
    and `UpdateFields` writes the submitted text back into `ExtractionFields` — so saving an
    untouched clipped `description_of_operations` (a verdict input via the additional-insured
    `contains` fallback) narrows the canonical value. The SILENCE is CLOSED by
    [#444](https://github.com/neboxdev/complidrop/issues/444) / ADR 0049 (see the block below); the
    narrowing itself is still reachable ON PURPOSE and is not a residual gap to re-report.
  - **One unpinned mirror:** `frontend/src/lib/document-types.ts`. A .NET test cannot reach it
    (no shared fixture, unlike ADR 0038's contact-email corpus). The four in-repo mirrors that CAN
    be reached are pinned: both provider schemas, the extraction prompt's DOCUMENT TYPES block, and
    `DisplayLabels.DocumentTypes`. (It was five until #389 deleted
    `DocumentEndpoints.AllowedDocumentTypes` — see the bullet above; what those endpoints owe the
    vocabulary is now pinned over HTTP in `RequestInputLengthTests`, not by set equality.)
    `RuleEngine/RuleSetLoader.DocumentTypes` is a SEPARATE set and deliberately NOT a mirror — an
    RD-c SUBSET (`coi | license | certification | other`) that must NOT be pinned equal to `All`.
- A CLIPPED extraction field value is ADR 0049 (#444) — the disclosure half of ADR 0045 §4's
  truncate-not-drop clamp. The facts that follow are pointers into it, not a second copy.
  - The ADR 0045 §4 truncation is UNCHANGED: same policy, same `InputLengths.DocumentFieldValue`
    width, same `ColumnClamp.To` surrogate back-off. #444 changed DISCLOSURE only. "Widen the
    column", "don't truncate", "store the full value in the row" are all refuted (ADR 0049 Option C).
  - `DocumentFieldDto.FieldValueTruncated` is DERIVED at read time by
    `Services/DocumentFieldTruncation.ValueWasClipped` — the jsonb copy vs the clamped row — and
    deliberately NOT persisted. A persisted flag needs a migration AND its own clearing logic in
    every writer; the derivation self-clears because `UpdateFields` writes the submitted text into
    BOTH copies. "Emit it from the worker into a column" is a recorded rejection (Option A), not a
    finding.
  - The client must NOT re-derive it, exactly like `DocumentDetail.UnreadableFields` (ADR 0040
    Amendment 2). Both copies happen to be on the wire, so a TypeScript re-derivation is POSSIBLE —
    and would mean re-implementing the .NET width plus the surrogate back-off with nothing pinning
    the two equal. A frontend re-derivation IS a real finding (Option B).
  - The predicate reproduces the clamp (`row == ColumnClamp.To(full, width)`), NOT "the two differ".
    Two shapes legitimately differ WITHOUT being a clip and must read false: a JSON `null` (an
    absence on both sides — it goes through the ONE `DocumentFieldReadability.RawFieldValue` reader,
    ADR 0040), and an earlier duplicate-name row (the worker writes a row per extracted field but
    only the last value per name into the jsonb mirror). Loosening it to an inequality IS a finding.
    So does an over-length field NAME, whose row name is clamped so the full-name jsonb lookup misses
    (ADR 0049 §3's "safe direction" — no hint beats a hint pointing at a value we cannot line up).
    Keying the lookup off a clamped/normalised name to "fix" it IS a finding; pinned by test.
  - The narrowing save is still ALLOWED. Blocking it strands a user who genuinely needs to fix a
    clipped field — ADR 0046 rejects an over-length correction too, so they could not resubmit the
    full text either — and the narrowing is fail-CLOSED (removing text only turns a `contains` pass
    into a fail). "Reject the save" is a recorded rejection (Option D).
  - The hint states BOTH facts (the shown text is partial AND saving this field replaces the fuller
    record) and points at **View file**, never at retyping — retyping is unreachable through ADR
    0046's `validation.too_long`, pinned by a test. Dropping either fact, or offering "type the full
    value back", IS a real finding.
  - It SURVIVES a pending edit — deliberately NOT gated on the page's `edits` overlay (ADR 0049 §4).
    The flag describes the RECORD, which holds the fuller value until a Save lands, and a pending edit
    is when "saving replaces it" is one click from being true. Hiding it once the user types (or
    scoping away the "Shown shortened." lead) IS a finding; pinned by test, copy AND `aria-describedby`.
  - NO amber input border: that marker means "we couldn't read this" / low confidence (ADR 0040), and
    a clipped value is read correctly and high-confidence. Adding one conflates two states.
  - The worker-side pin (`ExtractionWorkerTests.A_row_the_worker_clipped_is_reported_clipped_by_the_
    read_time_derivation`) runs against a document the REAL worker wrote, on purpose: the hand-built
    unit shapes cannot catch the worker clamping the jsonb copy too, at which point the two would
    agree, the flag would go false, and the page would stop warning while still showing a clip.
- The re-extract in-flight guard is ADR 0050 (#365); the facts that follow are pointers into it,
  not a second copy of the rationale.
  - `Reextract` re-arms with ONE conditional `ExecuteUpdateAsync`, never a read-then-write. The
    atomicity IS the fix: an `if (status == Processing) return 409;` above a `SaveChanges` races the
    very claim it checks (the worker can flip the status between the SELECT and the UPDATE), so
    "simplify it back to a tracked-entity save with an if" IS a real finding, not a cleanup.
  - It bites ONLY while the claim is one the WORKER still believes in. A STALE claim passes through
    on purpose (`ClaimSql` would zombie-reclaim that row on its own next poll, so refusing buys no
    safety and strands the document), and so does `Processing` with a NULL `ProcessingStartedAt` —
    `ClaimSql`'s comparison yields NULL for it, i.e. the worker could never reclaim it either, so
    that is the one state with no route back from either side. Both are fail-OPEN by design; do not
    flag either as a hole.
  - The window is ONE constant, `ExtractionWorker.ZombieClaimTimeout`, and `ClaimSql` is BUILT from
    it — a re-inlined `interval '5 minutes'` IS a real finding (pinned: the SQL's interval is parsed
    back and compared to the constant). But `AttemptTimeoutCeilingSeconds` deliberately stays the
    literal 240 rather than `ZombieClaimTimeout - margin`, and the boundary tests on BOTH sides keep
    their own `-4m30s` / `-5m30s` literals (#62): those pins exist to discriminate a drift in this
    value, so deriving them from it makes them vacuous. "Hoist the test literals too" is the bug.
  - `ExecuteUpdateAsync` bypasses `AuditSaveChangesInterceptor`, so the explicit
    `document.reextract_queued` row (already there pre-#365) is now the WHOLE audit trace — the same
    trade `VendorPortalEndpoints`' upload-permit reservation makes. Not a lost audit trail. A REFUSED
    re-arm writes no audit row at all, deliberately: nothing was queued.
  - The 409 uses the plain `Error(...)` envelope (the `auth.email_taken` shape), NOT
    `IdempotencyResults.InProgressConflict()` — a different contract (ADR 0029 replays, it does not
    409). No frontend change: the button is already disabled while `isProcessing` and `friendly(err)`
    surfaces the message.
  - **NO `(DocumentId, FieldName)` unique index** — the ticket asks for one and ADR 0050 Option D
    refutes it, the same reasoning that refuted it for the waitlist table (ADR 0046 + ADR 0016
    auto-migrate-on-boot). Duplicates are NOT only a race artifact: `PersistSuccess` inserts one row
    per `extraction.Fields` entry with no `GroupBy` while the two lines above it DO de-dupe for the
    jsonb mirror and typed columns, and `Clamp(f.Name, …)` collapses two over-length names to one.
    ADR 0049 already treats a duplicate-name row as a shape the read predicate must tolerate. Adding
    the index, or de-duping that insert loop (Option E — a DIFFERENT duplicate source, and no help at
    all for the concurrent one), needs its own ticket with a measured population and a signed-off
    dedupe. Neither is a finding here.
- Client-controlled input in a BOUNDED audit column is ADR 0044 (#372); the review-time facts
  that follow are pointers into it.
  - The clamp lives at ONE boundary — `CurrentUserService` reading `ColumnClamp.To` — not at
    each sink. `ComplianceCheckService.ClampToColumn` is deliberately a one-line delegate to
    that same shared helper; do not re-inline it.
  - The widths bind STRUCTURALLY: `ModelConfiguration` calls
    `HasMaxLength(AuditColumnLengths.X)` / `HasMaxLength(ComplianceCheckService.CheckColumnMaxLength)`
    (the `ContactEmail.MaxLength` pattern). A re-inlined literal there is a real finding.
  - An unusable inbound `X-Trace-Id` is REPLACED with a fresh id, NOT clamped. Usable ==
    non-blank, ≤64, and EVERY character in `[A-Za-z0-9_-]` — an ASCII space, `.`, `:`, `@`,
    any other punctuation, any control character and any non-ASCII are all rejected. The
    narrow charset is load-bearing: it is what keeps client free text out of the deliberately
    un-redacted Sentry `correlation_id` tag (ADR 0037). Widening it is a real finding.
  - The echoed response header, `HttpContext.Items`, the log scope and the stored column must
    always agree — a version that clamps or rewrites one of them independently IS a real finding.
  - The SYSTEMIC sweep over the upload / register / waitlist / idempotency-key paths landed in
    [#389](https://github.com/neboxdev/complidrop/issues/389) — see ADR 0046 below.
- Request strings bound before a bounded column is ADR 0046 (#389); the review-time facts that
  follow are pointers into it, not a second copy of the rationale.
  - Reject-vs-clamp is PER-FIELD by design, and the axis is who authored the value. User-TYPED
    content is REJECTED with a `validation.too_long` 400 (silently truncating a person's words is
    invisible data loss — and a truncated rule `ErrorMessage` is a CHANGED requirement that still
    prints whole to an auditor). Machine-chosen incidentals the user never reads back — the uploaded
    file NAME, the waitlist `Source` tag, the client `Idempotency-Key` — are CLAMPED via
    `ColumnClamp.To`. "Make it consistent" in either direction is the bug, not the fix.
  - The SAME two `DocumentField` columns deliberately take BOTH policies: `ExtractionWorker`
    truncates (ADR 0045 §4), `UpdateFields` rejects. That is why their widths are ONE constant
    (`ExtractionWorker.FieldName/ValueMaxLength` alias `InputLengths`) — re-inlining either is a
    real finding. The two policies MEET at ADR 0049 (#444): because the correction is rejected
    rather than clamped, the clipped-field hint can only ever offer "read the original", never
    "type the full value back".
  - `Services/InputLengths.cs` is the SOURCE of those widths; `ModelConfiguration` calls
    `HasMaxLength(InputLengths.X)` (the `AuditColumnLengths` / `ContactEmail.MaxLength` pattern). A
    re-inlined literal there IS a real finding — and it is MECHANICAL, not just a convention: the two
    `AuditClientInputClampTests` width tests cover every `InputLengths` width as well as the ADR 0044
    ones (built-model equality, plus the entity-scoped source-text assertion that is the half catching
    an EQUAL-valued re-inline). Columns NOT listed there (`UploadedBy`, `BlobStoragePath`,
    `DocumentType`, `TimeZone`) are absent on purpose — each is length-safe by construction, and
    listing them would imply a guard that need not exist.
  - `validation.too_long` is the one code for THESE guards, not the app's only over-length rejection.
    `ContactEmail`'s `validation.contact_email` (#369 / ADR 0038, reachable on the SAME vendor
    request) and `AuthEndpoints.IsValidEmail`'s `validation.email` are older, deliberate exceptions —
    each is ONE answer covering shape AND length on a field with one message. Unifying either IS the
    bug (see the ADR 0038 entry). Only the NUMBER is shared: `InputLengths.UserEmail` backs
    `User.Email`, `EmailVerificationToken.NewEmail` and `IsValidEmail`'s cap.
  - The widths live in `Services/`; the `IResult`-producing guard `InputLength` lives in `Endpoints/`
    beside `IdempotencyResults` — it is the layer that owns HTTP envelopes. It stays a shared
    ENVELOPE rather than a `(code, message)` pair each endpoint shapes: "one code, one message shape"
    is the promise, and returning the finished result is what makes it true by construction.
    `Services/WaitlistSignup` holds the waitlist index name + duplicate predicate for the mirror
    reason — `ModelConfiguration` must not compile against `Endpoints`.
  - A non-nullable POSITIONAL record parameter is STILL null on the wire (System.Text.Json binds a
    missing/JSON-null property to null), and `InputLength.FirstViolation` treats null as "fits" —
    correctly, an absent value is not an over-length one. So a NOT NULL column fed from one needs its
    OWN blank guard beside the length check: `UpdateFields` (`fields` + each `fieldName`),
    `UpsertRule` (`operator`), `UpdateTemplate` (`name`). An EMPTY `fields` array must stay LEGAL —
    the detail page's no-edit Save is what resolves the #383 review card (ADR 0040).
  - The `Idempotency-Key` clamp must sit at the SINGLE point the header is read, before BOTH the
    lookup and the storage. A version that clamps only one of the two makes a repeat of a long key
    miss its own record and duplicate the side effect — idempotency silently broken. The PUBLIC
    portal still IGNORES an oversize key rather than clamping (truncation manufactures collisions an
    untrusted third party could force on purpose, ADR 0044 §2's reasoning); the three authenticated
    routes clamp at the same 128. That asymmetry is deliberate.
  - The dashboard upload's blob cleanup is ATTEMPTED on every failure path (`documentPersisted` +
    `finally`, the portal's shape). It does not violate ADR 0029/0032: `blobName` and `doc.Id` are the
    request's own Guids so a concurrent same-key loser only ever touches ITS OWN blob, and a
    sequential replay returns at the fast path before any blob exists. A version that narrows the
    TRIGGER back to the `IsKeyConflict` catch re-opens the orphan.
  - But the trigger is not the verdict: all three sites (both uploads + the sample seed) delegate to
    ONE `Endpoints/OrphanBlobCleanup`, which CONFIRMS ABSENCE before deleting — it re-queries
    `Documents.AnyAsync(d => d.Id == documentId)` on a FRESH scope's `SystemDbContext`
    (`IgnoreQueryFilters`: a soft-deleted row owns its blob too, ADR 0013) and SKIPS the delete when
    the row is there, or when that read itself fails (absence unproven -> keep the blob, log it).
    `documentPersisted == false` only means `SaveChangesAsync` did not return normally, which includes
    the commit landing and the ACK never getting back — deleting then destroys a REAL document's file
    (unviewable, un-extractable, still on the audit export), which is strictly worse than the orphan.
    Removing the confirm-absence read, or inlining a fourth copy of this cleanup, IS a real finding.
  - That cleanup deletes on its OWN short-lived (`OrphanBlobCleanup.Budget`, 10s) token — NEVER the
    request's `ct`, and no longer `CancellationToken.None`. `BlobStorageService.DeleteAsync` forwards
    the token to `DeleteIfExistsAsync`, which throws BEFORE issuing the DELETE on an already-cancelled
    token, so a client abort (the likeliest orphan-maker: tab closed, phone drops mid-upload) was the
    one failure whose cleanup could not run; `None` fixed that but let a best-effort delete burn the
    full ~90s Azure retry budget inside the caller's `finally`, including on the PUBLIC portal route.
    The helper takes no token from its callers at all so neither can be reintroduced, and its catches
    are unfiltered — the portal's old `when (ex is not OperationCanceledException)` let the aborted
    delete's exception escape the `finally` and replace the real one. Passing `ct` back in IS a real
    finding.
  - The waitlist duplicate race is fixed by CATCHING the existing `(Email)` unique violation (matched
    on the index NAME, the `IsKeyConflict` shape, verified against `pg_indexes` on the migrated test
    DB because the EF-model form would compare the constant to itself) and replaying the friendly
    200 — NOT by adding an index. Adding one over possibly-duplicated prod rows would fail the startup auto-migration and
    take prod down (ADR 0016). "Just add a unique index" is a refuted suggestion, not a finding.
  - An index name a 23505 `catch` matches on is ONE `Services/` constant consumed by BOTH
    `ModelConfiguration` (`HasDatabaseName`) and the endpoint — `WaitlistSignup.EmailUniqueIndexName`
    and `SampleData.DocumentUniqueIndexName` / `IsDocumentUniqueViolation`. Naming it there makes the
    EF-model check vacuous, so each is witnessed against `pg_indexes` on the migrated test DB and each
    PREDICATE has the three-case `IsKeyConflict`-shaped unit test (right index true, other index
    false, non-Postgres false). A hand-copied literal in an endpoint IS a real finding; so is a
    predicate broadened to bare SqlState.
  - Element length is not request size: `UpdateFields` caps the ARRAY COUNT too
    (`InputLengths.DocumentFieldUpdatesPerRequest`, its own `validation.too_many_fields` code),
    checked BEFORE the walk. It is the one `InputLengths` entry that is not a column width, so it is
    deliberately absent from the two `ModelConfiguration` binding tests — not an omission.
  - The waitlist endpoint has no frontend caller and is KEPT anyway — removing a public endpoint is a
    product decision. Do not flag it as dead code.
- Bare `now()` / `DateTime.UtcNow` in raw SQL on `timestamptz` is correct; the bug is
  `AT TIME ZONE` whose result feeds back into a timestamptz comparison/assignment
  (ADR 0009 — output-only conversion for display stays legitimate).
- Reminder catch-up window (org-local 08:00 → midnight) and failed-send retry-in-place
  (ADR 0025); per-recipient dedupe key and suppression skips (ADR 0031).
- `Database:AutoMigrate` stays ON in Development — the dev Neon branch is throwaway
  and the startup environment banner is the guard (#271).
- The corrected system-checklist set + its cross-org re-grade (`CorrectedTemplates` in
  the seed), the liquor "+ Add a requirement" menu option, and the additional-insured
  nudge are behind `TemplateCorrections:Enabled` (default OFF) pending the
  G1-COUNSEL-BRIEF §0 attorney/broker sign-off — deliberate merged-but-invisible code,
  not dead code, same posture as `RuleEngine:Enabled` (ADR 0036 Amendment 3). The
  flag-off `LegacyTemplates` set is byte-exact main's pre-#416 definitions ON PURPOSE
  (the merge-safety no-op) — do not flag its outdated floors/messages, and do not
  "fix" them: any edit there rewrites prod rows before the sign-off. Test hosts pin
  the flag ON; prod default stays OFF.
- The corrected additional-insured claim WORDING (#396 / CLM-1) is behind a SEPARATE
  default-OFF flag `ComplianceClaims:CorrectedAdditionalInsuredWording`, surfaced as
  `features.correctedAdditionalInsuredWording` on `/api/auth/me` — merged-but-inert
  pending the G1-COUNSEL-BRIEF §0 CLM-1 attorney sign-off, same posture as
  `TemplateCorrections` / `RuleEngine` (ADR 0043). It is COPY-ONLY: flag-OFF keeps the
  legacy "Names '{name}' as additional insured" sentence, the "was not found" failure
  message, and the "box is checked" affirmative-flag check note byte-for-byte (the
  merge-safety no-op — the legacy copy is deliberate, NOT a bug; do not flag or "fix"
  it), and flag-ON swaps in the honest "certificate indicates… request the endorsement"
  wording (TRR §3). The flag MUST NOT move any pass/fail verdict — `EvaluateRule`'s
  `fallbackHit` is computed independently of it and a verdict-parity Theory pins this;
  a version where the flag changes a verdict IS a real finding. DISTINCT from
  `TemplateCorrections` on purpose (a different sign-off — do not "unify" them). Test
  hosts leave it at the prod default OFF; the ON value is pinned by
  ComplianceClaimsFlagTests (isolated host). The staged corrected copy is not dead code.
- The export disclaimer (#402 / CLM-3, ADR 0047) is ON BY DEFAULT and behind NO flag —
  deliberately unlike ADR 0043's staged wording, and the asymmetry is recorded in ADR 0047
  §4: a flag stages a string whose flip changes what a VERDICT ASSERTS (dangerous in either
  direction, so default OFF), while adding a disclaimer where none existed is
  one-directional risk reduction and a default-OFF flag would leave the reported bug live in
  prod. "Stage it like CLM-1" is a refuted suggestion, not a finding. Other facts here that
  look like bugs and are not:
  - ONE constant, `ExportService.Disclaimer`, feeds all three artifacts (audit PDF, vendor
    package PDF, CSV). A hand-copied second literal IS a real finding (pinned: the sentence
    occurs exactly once in `ExportService.cs`).
  - Both PDFs render it from `ApplyPageDefaults`' `page.Footer()`, which QuestPDF repeats per
    page. Moving it into `page.Content()` (prints once, last page only) or applying it at one
    builder instead of the shared chrome IS a real finding — `ExportDisclaimerTests` scans EVERY
    `.cs` file under `api/CompliDrop.Api/` and requires every QuestPDF page composition to call
    `ApplyPageDefaults(`. That pin matches on SHAPE, not on the identifiers `container`/`page`
    (a renamed lambda parameter used to walk straight past it), fails CLOSED on a `.Page(` shape
    it cannot read, and carries `Adr0009EnforcementTests`-style anti-no-op floors. Weakening it
    back to a file-local, name-coupled count IS a real finding. A type-level guarantee (the
    `ObligationReport` technique) is NOT available here and the ADR says why — QuestPDF's
    `Document.Create` is a third-party static returning `byte[]`, so no wrapper of ours can be
    the only door; do not re-suggest it.
  - `SampleCertificateGenerator` is the ONE exemption from that scan, named in
    `ExportDisclaimerTests.ChromeExemptions` and cited to ADR 0047. Exemptions must exist on disk
    and cite an ADR (pinned) — an uncited or silent one IS a real finding.
  - The vendor package passes `attribution: null` on purpose (it never loads the Organization
    row); only the audit report carries the `CompliDrop · {org}` line. Not a missing footer. Both
    call-site arguments are pinned: "unify the two builders" would drop the audit attribution.
  - The CSV note is a TRAILING single-field row, never a preamble above the header (FP-102's
    row-1-is-the-header shape) and never padded rectangular (that would read as a document
    named after the disclaimer). "Make it a real 12-column row" is the bug, not the fix. Its
    tests assert the PARSED field, not the raw line — today's comma-free sentence happens to need
    no quoting, and that property is pinned separately so a counsel reword reads as a reword.
  - The wording is PROVISIONAL pending the CLM-3 attorney pass and is pinned verbatim by test
    so a reword is deliberate. Do not expand it with liability-cap / warranty / subprocessor
    prose — ADR 0047 Option D records that rejection (an over-reaching disclaimer is its own
    liability; the Terms own the full treatment).
  - So is its PROMINENCE. It renders in the same 8pt `#64748b` fine print as the attribution
    line, which ADR 0047 §5 records as an OPEN question routed to CLM-3 (counsel answers wording
    AND conspicuousness in one pass), not as a settled choice. Flagging "this is buried fine
    print" is correct as an observation and already recorded; unilaterally restyling it ahead of
    sign-off is the over-reach, and `ApplyPageDefaults` is the one place an answer would land.
  - Deliberately NOT in scope: softening the export's verdict LABELS (Option E — that is
    verdict semantics, and the known overclaims are #443), the in-app read surfaces, the
    reminder emails, `SampleCertificateGenerator` (a simulated vendor document, not a
    CompliDrop assertion), and the ACCOUNT DATA EXPORT (`AuthEndpoints.ExportAccount` — a
    portability dump of the account's own data back to its owner: raw JSON, no masthead, bare
    numeric enum codes, no rendered verdict label). The Decision line is scoped "every export a
    customer hands to a third party" for exactly that reason — do not read it as "every export".

## Sensitive areas (`careful-review` label ⇒ merge needs a two-reviewer clearance)

- **Auth**: `Endpoints/Auth*`, JWT/cookie issuance (`cd_session`/`cd_refresh`), BCrypt,
  lockout logic
- **Billing**: Stripe checkout, webhook, subscription state
- **Tenancy**: `AppDbContext.CurrentOrgId`, global query filters, any
  `IgnoreQueryFilters` call
- **Vendor portal**: `/api/portal/*` (public, untrusted input)
- **Blob storage**: Azure Blob access, SAS scoping
- **Audit**: `AuditSaveChangesInterceptor`, `IAuditLogger`
- **PII**: extraction fields, exports, email contents
- **Compliance-verdict semantics**: `ComplianceStatus`, `IComplianceCheckService`,
  the supersession predicate, checklist/template requirements

## Deployment model

Railway auto-deploys `main`; **merge = prod deploy**, and EF migrations auto-apply on
startup (additive ones included — ADR 0016). Overlapping instances during deploys are
possible: **multi-instance races are REAL findings**, never hypothetical.

## Sensitive globs (machine-readable — merge-gate `--careful` matching)

Any touched path matching one of these ⇒ pass `--careful` to the merge gate, which now
means "this merge needs a careful clearance" (two Fable-5 max-effort reviewers, both
answering safe) rather than "stop for a human" — same treatment as the `careful-review`
label above, and the gate blocks identically without the clearance record:

```
api/**/Endpoints/Auth*
api/**/Migrations/**
api/**/*Stripe*
api/**/*Billing*
api/**/AppDbContext.cs
api/**/AuditSaveChangesInterceptor.cs
api/**/*Portal*
frontend/src/app/(auth)/**
frontend/src/lib/api.ts
.github/workflows/**
Dockerfile*
**/package.json
api/**/*.csproj
```

(The last four are the deploy surface: merge auto-deploys, so CI definitions, the
container image, and dependency manifests are an unreviewed-path-to-prod risk.)

## Labels

No project labels beyond `task`, `bug`, `epic`, `careful-review`, `in-progress`.

## Commit scopes

`extraction`, `reminders`, `portal`, `auth`, `billing`, `audit`, `frontend`, `api`,
`db`, `worker`, `docs`, `ci`

## Scale (feeds the performance reviewer's scenario rule — flag bugs at ~10× these)

- Orgs: single digits live today; design threshold 100+ orgs
- Documents per org: up to ~1,000; vendors per org: up to ~200
- `ExtractionWorker` polls every 5s (`FOR UPDATE SKIP LOCKED`, 5-min zombie reclaim);
  `ReminderBackgroundService` ticks hourly
- Paid per-call: Document AI OCR + Gemini extraction per document; Resend per email —
  re-processing an identical blob is real money

## Project severity anchors

- Cross-tenant data exposure: **blocker**, always.
- Wrong persisted compliance verdict — the product IS the verdict: **blocker**.
- Verdict/inputs torn pair (violates ADR 0030 combined-unit-of-work): **blocker**.
- Missed or duplicated reminder send: **major** (blocker if suppression is ignored).
- Paid AI call in a loop without dedupe/cache: **major**.
- Copy that overclaims compliance or legal certainty: **major** — the
  compliance-claims persona owns this lens.

## Workflow wiring

- `bug`-labeled issues auto-index into the rolling epic
  [#48](https://github.com/neboxdev/complidrop/issues/48) via
  `.github/workflows/bugfix-epic-sync.yml` — never hand-edit that epic body.
- CI: lint blocks merge (`react-hooks/static-components`,
  `jsx-a11y/label-has-associated-control` among others) — the merge gate waits for
  checks.
