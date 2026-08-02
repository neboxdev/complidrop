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
  - `VendorEndpoints.ComputeCoverage` excludes a DISTRUSTED doc from in-force coverage, so a
    required type covered ONLY by distrusted docs reads ActionNeeded (like an expired-only
    type). READ-TIME only — the stored `ComplianceStatus` is untouched (no persisted
    `Pending`), extraction-trust and rule-verdict are separate axes. Since #459 / ADR 0052
    (Amendment 3) the clause reads ONE column, `Document.ExtractionTrust`, and `DocCoverageInfo`
    carries NEITHER `ExtractionStatus` NOR `IsManuallyVerified` any more — see the ADR 0052
    block below for the whole mechanism. The historical shape (two status clauses plus an
    `IsManuallyVerified` escape) is described in Amendments 1–2; do not restore it.
  - A terminally `Failed` extraction is in that population too (Amendment 2, #365) — an
    extraction the system could not COMPLETE is at least as untrustworthy as one it distrusted,
    and nothing about the failure touches `ComplianceStatus`, the check rows or the fields.
    Since ADR 0052 it gets there because `MarkFailed` / `RecordFailedAttempt`'s terminal arm
    WRITE `Distrusted`, not because the read infers it from the status.
  - Deliberately NOT applied to the document-level surfaces (dashboard compliant/
    expiringSoon counts, `?status=` list/badges, CSV/PDF export, per-doc compliance badge):
    the list already shows a separate `ManualRequired` extraction badge beside the
    compliance badge, and demoting the counts too would create a #294-class count-vs-badge
    split. The vendor rollup is the one surface with no room for the separate badge. Do NOT
    flag the untouched document-level counts as a missed demotion — that inversion is the bug.
    Note the CONTRAST with ADR 0048 below, which mirrors its demotion onto the document-level
    surfaces: that is not an inconsistency, it turns on whether a second badge already discloses
    the state. Here one does; for never-graded none does.
- Extraction TRUST as its own column is ADR 0052 (#459 / ADR 0042 Amendment 3); the facts that
  follow are pointers into it, not a second copy of the rationale.
  - `Document.ExtractionStatus` is PIPELINE POSITION and `Document.ExtractionTrust` is TRUST, and
    the split is the point: they used to be one column, so `Reextract` writing `Pending` over
    `ManualRequired` DESTROYED the ADR 0042 distrust signal. Re-conflating them in either
    direction IS a real finding.
  - FOUR writers, and only four: `PersistSuccess` (ONE boolean drives BOTH columns — a review-routed
    read that forgets to withdraw trust re-opens the ADR 0042 hole, a clean read that forgets to
    restore it strands the doc at ActionNeeded), `MarkFailed`, `RecordFailedAttempt`'s TERMINAL arm,
    and `ResolveManualReview`. The retry arm deliberately leaves trust alone — a requeued attempt
    says nothing about the values already on the row, and distrusting on a transient hiccup sinks a
    covered vendor for the whole retry cycle.
  - The three WORKER writers go through `ExtractionWorker.SetTrust`, which sets `IsModified` on the
    column — a plain assignment is NOT enough and removing the force is a real finding.
    `ProcessDocumentAsync` loads the doc BEFORE OCR+LLM and holds that snapshot for minutes; EF emits
    only changed properties, so assigning the snapshot's own value emits no `SET` at all while the row
    may have moved (a mid-read `PUT /verify` commits the opposite value). Do NOT file this as the ADR
    0030 / #460 stale-snapshot residual, and do NOT read #460's read-only grading basis (ADR 0030
    Amendment 2, which ships on the same method) as a reason to unforce this: #460 is about verdict INPUTS
    a REQUEST owns, which is why it grades from a fresh value and writes no input back — it FORCES
    `ComplianceStatus` (`ForceVerdictWrite`) for the same reason this forces trust, because a verdict, like
    trust, is the writer's OWN conclusion, which ADR 0052 §2 says it owns. The axis is ownership, not
    mechanism. Pinned by
    `PersistSuccess_forces_its_trust_decision_over_a_write_that_landed_mid_extraction` and
    `A_terminal_failure_forces_its_distrust_over_a_confirmation_that_landed_mid_attempt`
    (`FakeExtractionClient.DuringExtract` constructs the interleaving; do not "simplify" it away).
  - `ResolveManualReview` decides trust from READABILITY on every status; only the escalation back to
    `ManualRequired` is gated on `wasSettled`. That gate exists solely so the escalation cannot
    DE-QUEUE the doc — withdrawing trust de-queues nothing — so re-gating trust on it is the bug, not
    tidiness: on a re-armed `Pending` row or a `Failed` one, one click would buy `Trusted` over an
    unparseable canonical value (and on `Failed` nothing would ever take it back). Pinned by
    `Marking_verified_cannot_buy_trust_over_an_unreadable_value_on_an_unsettled_row` (all three
    unsettled statuses), with the status half still pinned by
    `An_unreadable_edit_does_not_overwrite_an_unsettled_extraction_status`.
  - `Reextract`'s `ExecuteUpdateAsync` does NOT set trust, and that ABSENCE is the fix. Adding a
    `.SetProperty(d => d.ExtractionTrust, …)` there (it looks like tidy bookkeeping) restores the
    original bug exactly. `Reextract` is pinned twice: a behavioural Theory in `DocumentEndpointsTests`
    and `Adr0052EnforcementTests.The_queue_re_arm_does_not_write_trust`. `RequeueInterruptedAsync` is
    the same rule with its own pin —
    `Graceful_shutdown_mid_attempt_requeues_without_counting_a_failure` seeds `Distrusted` and asserts
    it survives, plus the member-level source assertion in `Adr0052EnforcementTests`. The whole-file
    source scan exists because no behavioural test can pin "no OTHER surface reads trust" — the ADR
    0042 document-level carve-out.
  - The `IsManuallyVerified` clause is RETIRED from `ComputeCoverage`, which is how Amendment 2's
    recorded stickiness residue closes. The flag STAYS on the entity + detail DTO (a real fact about
    the doc); re-adding it to the coverage predicate re-opens "confirmed once, re-extracted, failed
    again reads confirmed". The human EXIT is unchanged in substance —`ResolveManualReview` writes
    `Trusted`, still reachable from a `Failed` row where the status can never move — and both
    directions stay pinned by the same two tests
    (`A_FAILED_extraction_a_human_confirmed_reads_Covered_again`,
    `A_vendor_whose_only_cert_is_a_FAILED_extraction_reads_ActionNeeded_not_Covered`).
  - `Pending`/`Processing` are still not excluded BY STATUS — the clause cannot see the status. An
    in-flight doc is excluded exactly when it is `Distrusted`, and TWO paths reach that (round 2 of the
    #459 review corrected this bullet: the second one used to be unreachable, so the record said the
    exclusion was continuous "by construction"). (1) It was ALREADY distrusted and the re-arm carried the
    distrust through — the queue writers leave trust alone, so it read ActionNeeded the instant before the
    click. (2) `ResolveManualReview` distrusts it WHILE in flight, because trust follows READABILITY on
    every status and only the `ManualRequired` escalation is de-queue-gated — so a `PUT /fields` /
    `PUT /verify` leaving an unreadable canonical value on a `Pending`/`Processing` row withdraws trust
    there too. Path 2 is a genuinely NEW mid-read demotion and it is ACCEPTED: fail-CLOSED (ADR 0040),
    user-initiated, disclosed by the detail page's `ManualReviewCard` (which renders off
    `unreadableFields`, not off the status), and cleared by the read the user is watching landing cleanly
    or by a later save that leaves nothing unreadable. Pinned:
    `A_field_save_that_leaves_an_unreadable_value_demotes_a_cert_that_is_already_in_flight`. ADR 0042
    Amendment 2's carve-out keeps its actual protection either way — an ordinary re-extract of a TRUSTED
    doc stays Covered throughout (pinned:
    `A_trusted_cert_being_re_extracted_stays_Covered_for_the_whole_in_flight_window`). Amendment 2's
    OLD assertion that a re-armed DISTRUSTED doc reads Covered in flight was reversed deliberately
    in Amendment 3 — do not "restore" it, and do not read the reversal as licence to exclude
    in-flight statuses BY STATUS.
  - The migration is ADDITIVE and its BACKFILL is a decision, not a default: it reproduces the
    pre-#459 read predicate verbatim (`ManualRequired`, or `Failed AND NOT IsManuallyVerified`) so
    no row excluded before the deploy is re-covered after it. "Just default everything to Trusted"
    (re-covers the excluded population) and "default everything to Distrusted" (sinks every covered
    vendor, re-paying the whole corpus's OCR+LLM to recover) are both recorded rejections, ADR 0052
    Options C/D. The store default `'Trusted'` is load-bearing: EF's implicit `""` for a required
    text column is unreadable by the enum.
  - KNOWN residue, ADR 0052 § Consequences, BOTH DIRECTIONS — do not re-report either. During a
    Railway deploy overlap the OLD container writes `ExtractionStatus` without trust (it does not know
    the column exists). NOT closable by the column default: the exposed transition is an `UPDATE`, not
    an `INSERT`. The nullable-column-with-legacy-fallback fix is Option E, refuted (keeps the
    status→trust inference alive forever with no forcing function to remove it); splitting the release
    so the read switch ships a deploy later is also recorded and declined.
    - Fail-OPEN: `ManualRequired`/`Failed` + `Trusted` — a distrusted doc reads Covered. SELF-HEALS on
      the next extraction and keeps its own extraction badge meanwhile. Pinned by
      `A_ManualRequired_row_the_backfill_never_reached_reads_Covered_by_design`.
    - Fail-CLOSED: `Completed` + `Distrusted` — the old container's `PersistSuccess`/
      `ResolveManualReview` lands on a row the boot backfill marked `Distrusted`. Excluded from
      coverage with **no badge and no self-heal** (extraction badge reads `Read`, compliance badge
      reads `Compliant`) — the ONE shape where ADR 0042's carve-out disclosure premise is false. The
      REMEDY is user-reachable and is the exclusion's normal exit: any new-container writer rewrites
      trust, i.e. a re-extract that lands cleanly, or one `Mark verified`. Pinned (remedy included) by
      `A_Completed_row_the_boot_backfill_distrusted_reads_ActionNeeded_with_nothing_disclosing_why`.
    - A THIRD path reaches BOTH pairs with no deploy overlap, corrected in round 2 of the #459 review and
      ticketed as [#465](https://github.com/neboxdev/complidrop/issues/465) — the record used to say
      "neither shape is reachable through the NEW writers", which is true of any ONE writer and false of
      two INTERLEAVED. `MarkVerified` is an unforced READ COMMITTED partial write (SELECT, then EF emits
      only what differs from that snapshot), and on an unsettled row `ResolveManualReview` leaves the
      status alone — so the UPDATE carries trust WITHOUT it, and a `PersistSuccess` commit landing in that
      window leaves `ManualRequired` + `Trusted`. ADR 0030 last-writer-wins class, same family as
      #460 (closed by ADR 0030 Amendment 2, whose grading basis does not reach this — a status/trust pair
      is not a verdict basis) and #461, plausibly absorbed by #461's shape; `UpdateFields` is exempt (REPEATABLE READ + 40001
      re-run). ACCEPTED, not overlooked: widening the RR guard to `MarkVerified` is itself a finding (see
      the ADR 0030 block), and the conditional-`ExecuteUpdateAsync` alternative drops the
      `AuditSaveChangesInterceptor` diff row on a human confirmation while still grading from the stale
      snapshot. Pinned by
      `Marking_verified_on_an_unsettled_row_emits_trust_WITHOUT_the_status_it_read` (EF command log).
  - KNOWN in-flight disclosure gap, also ADR 0052 § Consequences — do not re-report: a re-armed
    DISTRUSTED doc (`Pending`/`Processing` + `Distrusted`) makes the vendor read ActionNeeded while the
    extraction badge reads `Reading…` rather than `Needs your review`. Bounded by one poll,
    self-healing, and in the safe direction (it read ActionNeeded the instant before the click).
  - NO wire field: trust itself is not surfaced. The disclosure the carve-out rests on is the detail
    page's existing extraction badge and review / error cards — except in the recorded windows above,
    where that premise is explicitly noted as not holding. ONE frontend change went with it (round 2 of
    the #459 review): `ManualReviewCard` is gated on `unreadableFields` being non-empty as well as on
    `extractionStatus === "ManualRequired"`, because an unreadable canonical value now withdraws trust on
    EVERY status while the escalation is refused on `Failed` (where manual entry is the affordance the
    page offers) and on `Pending`/`Processing`. Re-narrowing that gate back to the status IS a finding —
    it leaves those users a page reading "Verified: Yes" with nothing naming the value blocking coverage.
    The card still renders NOTHING when there is neither cause; both directions are pinned by test.
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
- UNTRUSTED input reaching the extraction PROMPT is ADR 0051 (#384). Two of the prompt's three parts
  are vendor-authored — the document-type hint and the OCR text of a file the vendor wrote — so both
  are DATA, never instruction. Pointers, not a second copy of the rationale:
  - There is ONE user-message builder, `ExtractionPrompts.BuildUserPrompt`, called by BOTH clients.
    The two providers used to hold byte-identical PRIVATE `BuildPrompt` copies, and `Extraction:Provider`
    decides which one runs at config time — so a guard added to one is the same bug left live in the
    other, invisibly to the diff. A re-introduced per-provider builder IS a real finding.
  - The hint line is emitted ONLY when `CanonicalDocumentTypes.Normalize` returns a non-`Fallback`
    member, and it prints the VOCABULARY's own spelling, never the caller's string — so even a
    RECOGNISED value contributes zero caller-controlled bytes. Echoing the input back after merely
    TESTING membership (`IsAllowed(x) ? x : ""`) IS a finding, not an equivalent. A positive `other`
    still emits nothing (unchanged pre-#384 behaviour), and a canonical type IS still offered — a
    blanket "just drop the hint" passes the injection tests while degrading extraction for every
    honest document, and a test pins each member is still offered.
  - It is NOT redundant with the #373/#389 ingress normalization, and "upstream already handles it" is
    a refuted suggestion: ADR 0045 records that legacy non-canonical rows were DELIBERATELY not
    laundered, so `Document.DocumentType` still legitimately holds arbitrary pre-#373 text. Point of
    use is the only place that does not depend on an invariant having held for every row ever written.
  - The OCR text stays VERBATIM — never stripped, escaped, or fence-sanitised. It is the thing the
    product exists to read, and the `---` fence is a reading aid the content can reproduce, which is
    why `SystemPrompt`'s UNTRUSTED CONTENT section says so out loud instead of leaning on it.
    "Escape the fence in the OCR text" is the bug. A per-request NONCE delimiter is DEFERRED, not
    refuted (ADR 0051 Option D — it breaks Anthropic prompt caching and the byte-exact cross-provider
    agreement pin); do not re-report it as a gap.
  - The region ANCHOR is the FIRST `"OCR text:"` line, and the reproduction-immunity clause names BOTH
    the `---` lines and that anchor line (round-2 copy fix): the content can print its own `OCR text:`
    line too, so a LAST-occurrence reading would leave an attacker's preamble in the trusted half.
    Both halves are pinned in `ExtractionPromptInjectionTests`. Re-narrowing the immunity clause back
    to the fence alone, or dropping `FIRST`, IS a real finding — and neither is the nonce fence.
  - The clause is a MITIGATION, and the ADR says so: the hint guard is structural/absolute, the prompt
    clause is probabilistic. The durable answers to a steered extraction are ADR 0042's confidence gate
    and ADR 0040's fail-closed reads, which already exist. "A prompt clause isn't a real defence" is
    recorded, not a new finding.
  - Any prompt edit bumps `ExtractionPrompts.Version` AND re-pins the SHA in
    `ExtractionPromptVersionTests` (the tripwire that makes the edit deliberate; `Version` is recorded
    per document). The hash covers the WHOLE wire prompt — `SystemPrompt` plus every branch of
    `BuildUserPrompt` (hint present/suppressed, empty OCR with AND without a hint, over-cap
    truncation), so the user-message half and `MaxOcrChars` are inside the pin, not just the system
    half. Weakening, deleting, or routing around that pin IS a real finding. The clause's own content
    pin lives in `ExtractionPromptInjectionTests` and asserts the FACTS the clause must state, not its
    full prose, so a reword stays possible.
  - Three properties of `WirePromptSurface` are load-bearing, not incidental (round-2 findings), and
    "simplify the surface" in any of these directions IS a real finding:
    - The rendered HINT inputs DISCRIMINATE the guard. `"coi"` and `null` alone did not — every
      candidate guard agrees on them, so a revert to the raw-interpolation or `IsAllowed(x) ? x : ""`
      echo-back shape stayed byte-identical and the pin never fired. A mis-cased canonical (`"COI"`), a
      non-canonical (`"Certificate of Insurance"`) and a padded fallback (`"  other  "`) must stay.
    - The over-cap rendering carries DISTINCT head/tail markers, so head- vs tail-truncation hash
      differently. A uniform `new string('A', N)` cannot see a flipped slice.
    - Named branch-marker assertions sit BESIDE the hash (`The_hashed_surface_still_covers_every_
      branch_of_the_wire_prompt`), because the hash pins the surface's VALUE and nothing pinned its
      SCOPE: dropping a branch reddened only the hash, and a one-line re-pin made it green while
      `Version` never moved (both are constants compared to each other). Deleting those markers, or
      the hint-line census inside them, re-opens the one-change hole.
  - `.gitattributes` sets `text diff` (NOT bare `text`) on `*.cs` and the other source/config types.
    That is deliberate and verified: `text` governs EOL conversion only, so git still auto-detects
    binary from a NUL and prints `Binary files ... differ` — and `diff=csharp` does not help either (a
    named driver leaves binary detection to the auto-heuristic). Only `diff` set to true forces a
    textual diff. A NUL typed into `ExtractionPromptVersionTests.cs` is what made the whole tripwire
    change invisible in the PR diff. "Drop the redundant `diff`" IS a real finding.
  - The UNTRUSTED CONTENT clause restricts what the model OBEYS, never what it may EXTRACT. The
    description-of-operations / remarks box stays readable DATA — a real ACORD can state a scheduled
    excess/umbrella limit or a renewal date only there, and the `additional_insured` FORMATTING rule
    already depends on reading a sentence out of it. Re-narrowing the clause into "never accept a
    sentence in place of the field" IS a real finding: fail-closed, but a verdict change on honest
    documents. Our own no-OCR notice sits ABOVE the `OCR text:` line for the same coherence reason —
    moving it back inside the fence is a finding.
  - `ExtractionWorker` passes `doc.DocumentType` through RAW. Suppressing the hint for `other` /
    unknown is `BuildUserPrompt`'s job ALONE; a re-introduced call-site pre-filter (especially one
    spelled as a raw `"other"` literal rather than `CanonicalDocumentTypes.Fallback`) is a second
    owner and a real finding.
  - Deliberately NOT flag-staged, unlike ADR 0043's wording: ADR 0047's asymmetry applies — a flag
    stages a string whose flip changes what a VERDICT asserts, while telling a model not to obey the
    document is one-directional, and default-OFF would leave the reported vulnerability live in prod.
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
  - The window is ONE constant SOURCED in `Services/ExtractionClaims.ZombieTimeout` (the
    `InputLengths` direction rule — a value two layers must agree on lives in `Services/`, worker-ONLY
    numbers stay on the worker). `ExtractionWorker.ZombieClaimTimeout` ALIASES it and `ClaimSql` is
    BUILT from it; the endpoint reads the `Services/` constant, so `Endpoints/` must NOT re-acquire a
    `using CompliDrop.Api.BackgroundServices;` — outside the composition root nothing does. A
    re-inlined `interval '5 minutes'` IS a real finding (pinned: the SQL's interval is parsed
    back and compared to the constant). But `AttemptTimeoutCeilingSeconds` deliberately stays the
    literal 240 rather than `ZombieClaimTimeout - margin`, and the boundary tests on BOTH sides keep
    their own `-4m30s` / `-5m30s` literals (#62): those pins exist to discriminate a drift in this
    value, so deriving them from it makes them vacuous. "Hoist the test literals too" is the bug.
  - The cutoff is computed INSIDE the predicate so Npgsql emits `"ProcessingStartedAt" < now() - …`:
    `ProcessingStartedAt` and `ClaimSql`'s own staleness test are both the DATABASE clock, so hoisting
    it into an app-clock local (`var cutoff = DateTime.UtcNow - …`) silently re-opens a clock-drift gap
    no behavioural test can see. Pinned by a test that reads the host's EF command log. The bare
    `now()` is ADR 0009-clean — do not "fix" it with `AT TIME ZONE`.
  - `ExecuteUpdateAsync` bypasses `AuditSaveChangesInterceptor`, so the explicit
    `document.reextract_queued` row (already there pre-#365) is now the WHOLE audit trace — the same
    trade `VendorPortalEndpoints`' upload-permit reservation makes. Not a lost audit trail. A REFUSED
    re-arm writes no audit row at all, deliberately: nothing was queued.
  - The 409 uses the plain `Error(...)` envelope (the `auth.email_taken` shape), NOT
    `IdempotencyResults.InProgressConflict()` — a different contract (ADR 0029 replays, it does not
    409), and `friendly(err)` surfaces the message as written.
  - "No frontend change, the button is already disabled while `isProcessing`" was the ORIGINAL ADR's
    reasoning and it is FALSE in exactly the state the 409 occupies — do not restore it (ADR 0050
    Amendment 1). `isProcessing` derives from the LAST SUCCESSFUL payload, so a tab whose payload said
    `Completed` has the button ENABLED — that tab is the only client state the 409 is reachable FROM,
    and it never self-corrects (`refetchInterval` returns false for a settled status,
    `refetchOnWindowFocus` is off). So `reextract.onError` INVALIDATES `["documents", id]` on this ONE
    code (`err instanceof ApiError && err.code === "document.extraction_in_progress"`): the badge flips
    to Processing, the 3s poll restarts and the button disables, instead of a "still reading" toast
    landing over a **Read** badge, the previous read's fields and verdict, and a button that keeps
    409-ing until a manual reload. Widening it to invalidate on ANY reextract error IS a finding — the
    other failures assert nothing about the document, and a blanket refetch fires a GET into a backend
    that just failed one and fights the #97 error short-circuit. Both directions are pinned by test.
  - **NO `(DocumentId, FieldName)` unique index** — the ticket asks for one and ADR 0050 Option D
    refutes it, the same reasoning that refuted it for the waitlist table (ADR 0046 + ADR 0016
    auto-migrate-on-boot). Duplicates are NOT only a race artifact: `PersistSuccess` inserts one row
    per `extraction.Fields` entry with no `GroupBy` while the two lines above it DO de-dupe for the
    jsonb mirror and typed columns, and `Clamp(f.Name, …)` collapses two over-length names to one.
    ADR 0049 already treats a duplicate-name row as a shape the read predicate must tolerate. Adding
    the index, or de-duping that insert loop (Option E — a DIFFERENT duplicate source, and no help at
    all for the concurrent one), needs its own ticket with a measured population and a signed-off
    dedupe. Neither is a finding here.
- The two PARTIAL document writers' concurrency guard is ADR 0030 **Amendment 1** (#366); the facts
  that follow are pointers into it, not a second copy of the rationale.
  - There is NO `Document` concurrency token and NO schema change, and that is the DECISION, not an
    omission. The ticket suggested `xmin` first, on the premise that the retry could be confined to
    `UpdateFields`/`UpdateDocument` and leave other paths undisturbed; `UseXminAsConcurrencyToken` is
    ENTITY-level, so it makes every tracked `Document` write optimistic whether or not that path
    handles the exception. The worst landing is `ExtractionWorker.PersistSuccess`, whose window is the
    whole OCR + LLM run and whose own remarks record the cost of a throw there (the catch's bookkeeping
    save re-throws on the same context, `FailedAttempts` never increments, zombie reclaim every 5 min,
    **re-paying Document AI + the LLM each time**). "Just add the xmin token" is a refuted suggestion,
    not a finding — and so is `FOR UPDATE` (ADR 0030 § Option B): it takes the `Documents` row lock
    BEFORE the transaction touches `ComplianceChecks` while every other writer's EF batch takes them
    the other way round, a lock-order inversion the current code does not have.
  - The mechanism is `REPEATABLE READ` on those two writers ONLY
    (`Endpoints/DocumentWriteConcurrency.RunAsync`), so Postgres raises `40001` instead of applying a
    stale-basis UPDATE. Every other `Document` writer — worker, `MarkVerified`, delete, re-grade
    fan-outs, nightly sweep — keeps `READ COMMITTED` last-writer-wins ON PURPOSE. A change that widens
    the guard to "all document writes" IS a real finding, and it is pinned:
    `An_unrelated_document_writer_still_wins_last_without_conflicting`.
  - The retry RELOADS and RECOMPUTES — the whole callback re-runs against a fresh read, so the winner's
    committed change is an INPUT to the retried verdict. A version that re-applies the losing snapshot
    (or retries inside the SAME transaction, whose snapshot is exactly what lost) fixes nothing. Both
    endpoint bodies must therefore stay re-runnable: deriving anything from state captured OUTSIDE the
    callback — including `UpdateFields`' `before` audit snapshot — is a real finding.
  - Exhaustion commits NOTHING and answers `409 document.concurrent_update`. Do NOT flag this as
    violating ADR 0030's degrade-to-`Pending` rule: that rule is for a RECOMPUTE failure, where the
    inputs are committing regardless. Here the whole unit of work is still abandonable, so rolling back
    leaves the last successful writer's consistent tuple — strictly stronger. Committing the edit with
    `Pending` would BE the half-applied write #366 removes.
  - "Commits nothing" covers the AUDIT trail too, and that costs a mechanism: `RunAsync` takes a
    required `onAttemptAbandoned` and invokes it in the same catch that clears the change tracker, so
    one abandonment discards both the entity state and whatever the caller kept outside it.
    `UpdateFields` uses it to un-say `saved` (its "an attempt wrote a field edit" fact, read AFTER the
    retry loop because `IAuditLogger` writes on a separate connection the rollback cannot reach);
    `UpdateDocument` passes `null` because it keeps nothing outside the callback. Moving that reset to
    the START of each attempt looks equivalent and is a real finding: `RunAsync` wraps `CommitAsync` in
    the same conflict catch as the write, so the LAST attempt can set the fact and be abandoned with no
    next attempt to clear it — a `document.fields_edited` row beside a 409 that says nothing committed.
    It would also make the code depend on WHERE Postgres reports the conflict (REPEATABLE READ reports
    first-updater-wins at the UPDATE; SSI under SERIALIZABLE, and any future row lock, report at
    COMMIT). Pinned by `The_attempt_that_loses_at_the_LAST_commit_leaves_no_audit_row_behind_it`.
  - On these two writers the degrade-to-`Pending` rule is now CONDITIONAL, and that is RECORDED (ADR 0030
    Amendment 1), not overlooked. A recompute failure that is a server-side POSTGRES error has already
    aborted the enclosing transaction, so `EvaluateIntoUnitOfWorkAsync`'s catch sets `Pending` but the
    following `SaveChanges` answers `25P02` and the request 500s having committed nothing — the same
    fail-closed landing as exhaustion. Adding a `25P02` arm to `IsConcurrentUpdateConflict` (retrying a
    transaction Postgres already aborted) or opening a SECOND transaction to force the `Pending` commit
    are both the bug, not the fix. Note the pinned `ThrowingComplianceCheckService` throws without
    touching the DB, so it deliberately does not discriminate the two failure kinds.
  - `Services/DocumentConcurrency.IsConcurrentUpdateConflict` walks the WHOLE inner-exception chain,
    unlike the one-level 23505 siblings (`SampleData.IsDocumentUniqueViolation`,
    `IdempotencyService.IsKeyConflict`). Load-bearing: Npgsql reports 40001 as TRANSIENT and EF (no
    retrying execution strategy configured) re-wraps a transient `DbUpdateException` in an
    `InvalidOperationException`, so the cause sits TWO levels down out of `SaveChanges` — "make it
    consistent with its siblings" turns every conflict into a 500. A unique violation is not transient,
    which is why the siblings can look one level in. Pinned by test.
  - It deliberately does NOT match `40P01 deadlock_detected` even though Postgres calls it retryable:
    the guard reorders no lock acquisition, so a deadlock here would be a NEW inversion that must
    surface. "Also retry deadlocks" is the bug, not the fix.
  - The pure re-grade paths (`EvaluateAsync` / `EvaluateForSystemAsync` / the fan-outs) keep their
    read→compute→write window and are OUT of scope by DECISION — not because they are harmless. They
    write no INPUTS so they cannot lose an update, but `EvaluateInternalAsync` is a bare
    `FirstOrDefaultAsync → ApplyEvaluationAsync → SaveChangesAsync` with no lock or token, so a
    "Check again" that loaded before an `UpdateFields` LOWERED a limit writes its stale verdict back
    after it: EF marks only `ComplianceStatus`/`UpdatedAt`, so the row keeps the lowered limit with a
    stored `Compliant` and passing check rows citing values it no longer holds, and NOTHING re-grades
    it (the sweep does date transitions only). Recorded in ADR 0030 Amendment 1 residual 1 and
    ticketed as [#461](https://github.com/neboxdev/complidrop/issues/461) — not a NEW finding, but do
    not let a diff or a doc call this window benign or self-healing either.
  - The worker's STALE-BASIS grading is ADR 0030 **Amendment 2** (#460), the closing half of Amendment
    1's scenario B. `ExtractionWorker.PersistSuccess` holds a tracked snapshot across an OCR + LLM run
    that lasts minutes and EF writes back only what it MODIFIED, so every canonical verdict input it
    leaves unmodified used to keep a request's committed value beside a verdict computed from the
    pre-run one — `VendorId` always, `DocumentType` whenever `NormalizeExtracted` returns the STORED
    value, any typed column whose field the model OMITTED. It now grades
    `Services/DocumentGradingBasis.AfterPendingCommitAsync`: the row's CURRENT committed values overlaid
    with exactly the properties the CHANGE TRACKER reports modified — i.e. a detached prediction of the
    row this commit will LEAVE. Facts that look like bugs and are not:
    - It is derived from `IsModified`, NOT from a list of columns, and that is the decision. All three
      named instances fall out of the one rule and so does the next column added to `Document`.
      Re-writing it as an enumeration (a vendor-id parameter, a per-column patch) IS a real finding —
      ADR 0030 Amendment 2 Option E.
    - The basis is READ-ONLY with respect to the TRACKED entity — no basis VALUE is copied back onto it, so
      the worker still emits exactly the verdict INPUTS it emitted before. (It DOES write the basis's own
      `Vendor` navigation; "read-only" scopes to `doc`. And it is about inputs, not about the column count:
      the worker DOES now force `ComplianceStatus` — see the forced-verdict bullet below.) Assigning a
      re-read input onto the tracked entity (or
      onto its `Vendor` navigation, which EF fixup turns into the same thing) makes the worker WRITE
      that column and clobber a request that landed mid-run — a LOST UPDATE the code does not have.
      Still a real finding. So is widening the RR guard or an `xmin` token to the worker (see the first
      bullet of this block: a throw out of that `SaveChanges` re-pays Document AI + the LLM).
      Pinned TWICE, because no value assertion can see it — every `DuringExtract` interleave commits
      BEFORE the basis read, where an assign-back writes the value the row already holds and every
      assertion still passes. `The_persist_emits_what_it_extracted_and_no_verdict_input_it_only_read`
      reads the persist's `UPDATE "Documents"` SET clause off the host's EF command log (the
      `Marking_verified_on_an_unsettled_row_emits_trust_WITHOUT_the_status_it_read` shape) and requires
      `"VendorId" =` / `"DocumentType" =` / `"GeneralLiabilityLimit" =` absent with `"ExtractionStatus" =`
      present as the anti-no-op. The statement it reads is picked by `"ExtractionCompletedAt"`, a column
      only the SUCCESS path writes — with `"ExtractionStatus"` as the picker, `RecordFailedAttempt` /
      `MarkFailed`'s UPDATE satisfied all four assertions, so a THROWN persist passed. Weakening that
      discriminator (or dropping the `ExtractionStatus == Completed` sanity assertion beside it) makes the
      pin vacuous again. `A_write_that_lands_AFTER_the_basis_read_is_not_clobbered_by_the_persist`
      drives a competing PATCH from inside the worker's own `SavingChanges` through the test harness's
      `ConcurrentSystemWriteInterceptor` (the `SystemDbContext` twin of the #366 hook — a SEPARATE
      singleton on purpose, since `IAuditLogger` saves on `SystemDbContext` during ordinary requests).
      Neither is decoration: the mid-run request in the first must move all three columns or the
      absences prove nothing, and the two vendors in the second must share ONE checklist that does NOT
      govern the document's type (see #468 — a competing regrade that deletes the check rows this
      persist has staged throws out of `SaveChanges`). "Simplify" either and the pin goes vacuous.
    - The basis overload ENFORCES two preconditions with `ArgumentException` — same `Id` as the tracked
      doc (check rows are stamped from the BASIS while the clear-existing predicate keys on the TRACKED
      one) and DETACHED (its `Vendor` navigation is assigned from an `AsNoTracking` query, which on a
      tracked principal EF turns into spurious inserts). Today's single caller satisfies both by
      construction, so "dead guard, drop it" IS a real finding. Pinned by
      `The_basis_overload_refuses_a_basis_that_is_not_this_document` and
      `The_basis_overload_refuses_a_TRACKED_basis`.
    - The basis vendor chain is a NEW query (`context.Set<Vendor>()` off the basis's own FK) rather than
      the tracked navigation, and its correctness rests on the Vendor soft-delete GLOBAL FILTER still
      applying. Adding `IgnoreQueryFilters()` there would read as idiomatic (this block blesses it inside
      background workers) and would grade a document against a DELETED vendor's checklist — a persisted
      false-affirmative. Pinned by `A_vendor_soft_deleted_mid_extraction_grades_as_no_checklist`.
    - Do NOT read it as a counter-example to `ExtractionWorker.SetTrust`, which DOES force its column.
      `SetTrust` makes the worker's OWN conclusion durable (ADR 0052 §2 says it owns it); #460 is about
      verdict INPUTS a REQUEST owns. Forcing those is Amendment 2 Option G, refuted. Blurring the
      distinction in either direction is a defect.
    - The VERDICT itself is on the `SetTrust` side of that line, and `ExtractionWorker.ForceVerdictWrite`
      writes it accordingly — `IsModified` on `ComplianceStatus` after the try/catch, so BOTH the graded
      verdict and the degrade-to-`Pending` land. Removing it, or narrowing it to one arm, IS a real
      finding: `ApplyEvaluationAsync` only ASSIGNS, and a verdict EQUAL to the minutes-old snapshot emits
      no SET clause while the `ComplianceCheck` rows are rewritten unconditionally and `UpdatedAt` keeps
      the UPDATE running — so a mid-read PATCH's verdict survives beside this read's inputs and this
      read's checks (strict→lenient reassignment, recomputed `NonCompliant` == the pre-run value, row
      commits `Compliant` over a lowered limit and a FAILING check row; the catch arm's twin is a
      `Pending` snapshot swallowing the degrade). Equally a finding in the other direction: extending the
      force to a verdict INPUT is Option G, and extending it to the REQUEST-path callers contradicts
      Amendment 1, whose `REPEATABLE READ` exists so a stale-basis UPDATE is REFUSED rather than forced.
      Pinned per arm by `A_verdict_equal_to_the_stale_snapshot_is_still_WRITTEN_over_a_competitors` (SET
      clause AND terminal tuple) and `A_failing_basis_read_degrades_the_verdict_without_requeuing_the_extraction`.
    - Grading the freshly-read row WHOLESALE is not a simplification of this, it is Option F and it is
      wrong in the false-affirmative direction: it discards the worker's own extraction, so a re-read
      that LOWERS a limit gets graded against the value the row is about to lose. Pinned by
      `The_workers_own_extracted_value_still_decides_the_verdict_where_it_wrote_it`.
    - The basis read sits INSIDE `PersistSuccess`'s degrade-to-`Pending` `try` deliberately — it must
      never become a new way for the persist to THROW — and a `null` basis falls back to grading the
      tracked entity. Hoisting it out of the `try`, or making the null case throw, IS a real finding.
      Pinned by `A_failing_basis_read_degrades_the_verdict_without_requeuing_the_extraction`, which faults
      the read's own `SELECT` through the test harness's `SystemCommandFaultInterceptor` (an
      `IDbCommandInterceptor` on `SystemDbContext`, callback-armed like the two hooks above, self-disarming
      after one fire) and requires the row to end `Completed` + `Pending` with `FailedAttempts` still 0
      rather than back in the queue. That pin had to be BUILT: what
      `A_document_deleted_mid_extraction_persists_without_a_second_extraction` pins is that a mid-run SOFT
      delete lands as an ordinary completed persist through the NON-null branch (it also reassigns the
      vendor first, so its verdict says which branch ran) — its basis read never fails and never returns
      null, so hoisting the read left the whole suite green. A HARD delete is not an alternative pin: the
      persist's own UPDATE would then throw for an unrelated reason.
    - `null` means a HARD delete, not "deleted mid-run". `GetDatabaseValues` issues an
      `AsNoTracking().IgnoreQueryFilters()` key lookup, so the SOFT delete every API path performs still
      yields a basis and the document is still graded as the row the commit leaves; no production path
      hard-deletes a `Document`, so the fallback is DEFENSIVE, not live. An earlier draft of ADR 0030
      Amendment 2 and of this file said otherwise and cited a test that could not tell the branches
      apart — both branches are now pinned on the helper itself by
      `The_grading_basis_is_null_only_when_the_row_is_genuinely_gone`, with the composition pinned by
      `The_grading_basis_overlays_only_the_properties_the_writer_modified`. A doc or comment that
      re-describes the null case as the soft-delete path IS a finding.
    - The REQUEST-path callers keep the tracked-entity overload ON PURPOSE. `UpdateDocument` grades
      against a `VendorId` it assigned in memory and has not committed, so a fresh basis would grade the
      OLD checklist (pinned by
      `Assigning_a_vendor_commits_the_verdict_against_the_new_checklist_atomically`), and both partial
      writers already have the strictly stronger RR + 40001 re-run. "Use the basis everywhere for
      consistency" IS a real finding.
    - KNOWN residual, ADR 0030 Amendment 2 § What stays open — do not re-report as new: the
      re-read → commit window is NOT closed, only shrunk from the whole extraction run to one round
      trip, which makes it the same shape and size as #461's. Closing it needs conflict DETECTION on the
      commit, and every detecting shape re-pays the extraction. Equally, do not let a diff or a doc
      describe #460 as fully closed.
    - TWO more recorded residuals in the same section, also not new findings. (a) `unreadableFields` —
      and therefore `distrusted`, `ExtractionStatus` and `ExtractionTrust` — is STILL asked of the
      pre-run tracked entity twenty lines above the basis read, so a mid-run edit that fixes a typed
      column can leave `ManualRequired` + `Distrusted` on a row whose read-time `unreadableFields` is
      empty (vendor reads Action needed, `ManualReviewCard` names nothing). NOT swapped onto the basis
      on purpose: that redefines trust from "what this read produced" to "what the row holds", an ADR
      0052 §2 decision, [#467](https://github.com/neboxdev/complidrop/issues/467). (b) A concurrent
      regrade that deletes the `ComplianceCheck` rows `ApplyEvaluationAsync` has STAGED for removal makes
      the persist's DELETE affect 0 rows, so EF throws `DbUpdateConcurrencyException` out of
      `PersistSuccess` — the re-paid-extraction landing, not the "cosmetic" display desync ADR 0030
      § Consequences describes. Predates #460 (it arrived with #337),
      [#468](https://github.com/neboxdev/complidrop/issues/468).
  - NO frontend change: the 409's message is already jargon-free copy that both call sites surface
    through their generic `err.message` toast. This is NOT the ADR 0050 Amendment 1 situation (there
    the client held a payload that actively CONTRADICTED the 409); here the user's edits survive in the
    page's `edits` overlay and the copy tells them to reload.
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
