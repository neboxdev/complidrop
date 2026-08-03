# 0052. Extraction trust is its own column, so a pipeline re-arm cannot destroy it

- **Status:** accepted
- **Date:** 2026-08-02
- **Deciders:** Ruben G. (founder), Claude (implementing #459)

## Context

`Document.ExtractionStatus` carried **two orthogonal facts in one column**:

1. **Pipeline position** — where the document sits in the extraction queue
   (`Pending` → `Processing` → `Completed` / `Failed`).
2. **Extraction trust** — whether the system stands behind the values a verdict was computed from.
   `ManualRequired` is [ADR 0042](0042-distrusted-extraction-per-field-gate-and-coverage-exclusion.md)'s
   distrust signal: raised by the average-confidence gate, the per-verdict-bearing-field gate, the model's
   own reprocess signal, or [ADR 0040](0040-unreadable-canonical-value-fails-closed.md)'s unreadable
   canonical value.

Because they shared a column, **moving the pipeline destroyed the trust**. `DocumentEndpoints.Reextract`
re-arms the queue by writing `Pending` over whatever was there, `ManualRequired` included. So one click on
"Read again" flipped the vendor rollup from ActionNeeded to **Covered**, on the strength of the very
extraction the system had flagged unreliable — and if the re-read then failed terminally the row landed on
`Failed` with nothing left to say it had ever been distrusted. `ExtractionWorker.MarkFailed` and
`RecordFailedAttempt` write `ExtractionStatus` only: the `ComplianceStatus`, the `ComplianceCheck` rows and
the `DocumentField`s all survive exactly as the distrusted read left them.

[ADR 0042 Amendment 2](0042-distrusted-extraction-per-field-gate-and-coverage-exclusion.md#amendment-2-2026-08-01--a-terminally-failed-extraction-nobody-confirmed-is-excluded-from-in-force-coverage-too)
(#365) closed the **permanent** half at read time by also excluding a terminally `Failed` extraction nobody
had confirmed. That was the right call with the levers available, and it kept the human exit — but it had to
reconstruct trust from where the document happened to *land*, and it had to borrow `IsManuallyVerified` as
the exit because the status could not be one. It recorded two consequences of that:

- **The in-flight window still reads Covered.** A re-armed distrusted document contributes coverage for as
  long as the re-read takes, tolerated because the only available lever was the status and excluding
  `Pending`/`Processing` would have sunk every legitimately-compliant vendor during any ordinary re-extract.
- **`IsManuallyVerified` is sticky.** Nothing clears it, so a document confirmed once, then *successfully
  re-extracted* (which overwrites the fields with machine values no human has seen), then failed again reads
  as confirmed on a basis nobody vouched for.

Both are artifacts of the conflation, not of the exclusion. This decision removes the conflation.

## Decision

### 1. `Document.ExtractionTrust` (`Trusted` / `Distrusted`) is the distrust signal

A second enum column beside `ExtractionStatus`, mapped as text like its neighbour
(`HasConversion<string>().HasMaxLength(50)`).

**`Trusted` is the ABSENCE of a distrust event, not a positive endorsement.** A freshly-uploaded document
nothing has read yet is `Trusted` on this axis and is withheld from affirmative coverage by a *different*
one — [ADR 0048](0048-never-graded-document-asserts-no-affirmative-verdict.md)'s never-graded demotion. Two
axes, each with one job, is the whole point of the change; making this column mean "vouched for" would have
started it back down the road to carrying everything.

### 2. It is written by the events that establish or undermine the basis — and by nothing else

| Writer | Writes | Why |
| --- | --- | --- |
| `ExtractionWorker.PersistSuccess` | `Distrusted` when it routes to `ManualRequired`, `Trusted` otherwise | ONE boolean, TWO columns. This read *is* the document's new basis, so it decides both. |
| `ExtractionWorker.MarkFailed` | `Distrusted` | ADR 0042 Amendment 2's premise, made durable: an extraction the system could not COMPLETE is at least as untrustworthy as one it distrusted. |
| `ExtractionWorker.RecordFailedAttempt` | `Distrusted` on its **terminal** arm only | While retries remain the document is merely back in the queue and its basis is unchanged; distrusting it over one transient hiccup would sink a covered vendor for the length of the retry cycle. |
| `DocumentEndpoints.ResolveManualReview` | `Distrusted` when the document STILL carries an unreadable canonical value, `Trusted` otherwise | The human exit, from the RESULTING state. The one helper behind `PUT /fields` and `PUT /verify`. |

**Everything in the queue path deliberately leaves it alone**: `Reextract`'s `ExecuteUpdateAsync`,
`RecordFailedAttempt`'s retry arm, `RequeueInterruptedAsync`. That absence is the fix. A
`.SetProperty(d => d.ExtractionTrust, …)` added to the re-arm restores the original bug exactly.

**The three worker writers must FORCE the column into the `UPDATE`** — they go through
`ExtractionWorker.SetTrust`, which sets `IsModified` (#459 review). `ProcessDocumentAsync` loads the
document *before* OCR + the LLM call and holds that tracked snapshot for the minutes the read takes, and EF
Core emits only properties whose current value differs from the snapshot. So assigning the snapshot's own
value produces no `SET` clause — while the row itself may have moved, because a human clicking "Mark
verified" mid-read commits the opposite value on another connection. Without the force, the extraction that
was supposed to *re-decide* trust silently leaves the other writer's value standing: `ManualRequired`/
`Failed` + `Trusted` (a distrusted basis rolling up as Covered — the ADR 0042 hole), or the mirror image, a
clean re-read that no-ops and strands the document at ActionNeeded. Forcing the column closes this
writer's half of that pair only; § Consequences records the two remaining paths to it, and neither is a
deploy overlap alone — the request-side `MarkVerified` is an unforced partial write, so a commit from
*here* landing inside *its* window produces the same shapes
([#465](https://github.com/neboxdev/complidrop/issues/465)).

This is **not** the [ADR 0030](0030-compliance-verdict-combined-unit-of-work.md) stale-snapshot residual
([#460](https://github.com/neboxdev/complidrop/issues/460)) and must not be filed under it. That residual is
about a writer *grading from* verdict INPUTS the row has moved on from and re-asserting them; nothing is
re-asserted here. ADR 0030 Amendment 2 has since closed #460 on this very method, and the contrast is the
clearest statement of the boundary, and it is a boundary of OWNERSHIP rather than of mechanism: Amendment 2
grades a freshly-read basis and writes no verdict INPUT back, because those inputs belong to a request —
while FORCING `ComplianceStatus` exactly as this forces `ExtractionTrust`, because the verdict, like trust,
is this read's own conclusion. A change that makes the two consistent along the wrong axis — unforcing
either conclusion, or forcing an input — is a defect in whichever direction it goes. The value is this read's own conclusion, computed from what the model just returned, and
the table above says these writers own it — "last writer wins on the whole tuple" is the intended
semantics, and forcing the column is what makes this writer actually be one.

**AMENDED by Amendment 1** ([#467](https://github.com/neboxdev/complidrop/issues/467)): *"computed from
what the model just returned"* named the wrong SUBJECT, and only for the readability trigger. That half of
`PersistSuccess`'s decision now reads ADR 0030 Amendment 2's grading basis — the row this commit will
LEAVE — because "is a canonical value unreadable?" is a question about a ROW and the pre-run snapshot is
not the row. Everything above about OWNERSHIP stands unchanged: this table is still the whole writer set,
the queue path still writes nothing, the three worker writers still go through `SetTrust`, and `SetTrust`
still forces the column. The other three triggers (both confidence gates, `NeedsReprocessing`) describe
the READING, have no mirror on the row, and are untouched.

### 3. `ComputeCoverage` consults trust directly, and the `IsManuallyVerified` clause is RETIRED

The in-force test's extraction clause collapses from two status-shaped clauses plus an escape hatch to one:

```csharp
d.ExtractionTrust is not ExtractionTrust.Distrusted && ComplianceStatusDeriver.Effective(…) is Compliant or ExpiringSoon
```

`DocCoverageInfo` **drops `ExtractionStatus` and `IsManuallyVerified` entirely** and carries
`ExtractionTrust` instead, in both projections. That is deliberate over-correction: with the status not even
on the record, "a re-arm cannot move coverage" is structural rather than a rule someone has to remember.

**Trust and status take the same question but not the same gate** (#459 review). Both ask ADR 0040's
"is a canonical value still unreadable?" — one predicate, `DocumentFieldReadability`, asked once, of the
document's RESULTING state. But the escalation's `wasSettled` guard belongs to the STATUS write alone: its
only job is not to DE-QUEUE (overwriting `Pending` strands the document, since the worker claims on it, and
`Failed` is its own louder error state). **Withdrawing trust de-queues nothing** — no worker, sweep or
endpoint dispatches on this column; the vendor rollup merely reads it. Gating trust on the status too was a
hole: on a re-armed row at `Pending`, or on a `Failed` row where a human typed an expiration the parser
rejects, one click bought `Trusted` over a value nothing can read — exactly the clean bill of health this
decision says the click can no longer buy, and on the `Failed` path nothing would ever have taken it back.

Retiring the `IsManuallyVerified` clause is what closes Amendment 2's sticky-flag residue. The flag itself
stays on the entity and on the detail DTO — it is a real fact about the document, surfaced in the UI; it
just no longer gates coverage. The exit it provided is preserved, and improved: `ResolveManualReview` writes
`Trusted`, so a confirmation restores coverage whatever the status (including `Failed`, where the status can
never move), **and a later extraction can take it back**.

### 4. The existing-row seed is a decision, and the migration makes it

The migration is additive: `ADD COLUMN … NOT NULL DEFAULT 'Trusted'`, then one `UPDATE` writing **only the
new column**, from two values already on the same row:

```sql
UPDATE "Documents" SET "ExtractionTrust" = 'Distrusted'
WHERE "ExtractionStatus" = 'ManualRequired'
   OR ("ExtractionStatus" = 'Failed' AND NOT "IsManuallyVerified");
```

That predicate is the **pre-#459 read-time exclusion, verbatim** — turning the old read into stored state, so
no row excluded before the deploy is silently re-covered after it, and no row covered before it is newly
dropped. Note the `ManualRequired` arm ignores `IsManuallyVerified` on purpose: `ResolveManualReview` can set
the flag and then re-raise the review (ADR 0040), and such a row is excluded today.

Migrations auto-apply at boot and fail fast ([ADR 0016](0016-apply-ef-migrations-on-startup.md)), so the
statement's cost matters: one `UPDATE` over a table in the low thousands of rows
(`.claude/reviewers.md` § Scale), well inside the boot budget.

The store default is load-bearing rather than cosmetic. During a Railway deploy the old container keeps
serving until the new one's health check passes, and it still `INSERT`s `Documents` without this column; EF's
implicit default for a required text column is `""`, which no enum member can read, so every such row would
throw on materialization.

## Consequences

### Positive

- The reported bug is closed at its root: clicking "Read again" on a distrusted document no longer buys a
  clean bill of health, for the length of the read or permanently.
- Amendment 2's sticky-`IsManuallyVerified` residue is closed — trust is re-decided by every extraction.
- One question, one column, one read site. The rollup can no longer disagree with itself about whether a
  document's basis is trustworthy, and the exclusion has ONE exit instead of two shapes of exit.

### Negative

- **A distrusted document being re-read now reads ActionNeeded throughout the read**, where before it read
  Covered. This reverses the in-flight half of ADR 0042 Amendment 2's carve-out and is recorded there as
  Amendment 3. It is a change in the safe direction and it is *continuous*: the document read ActionNeeded
  the instant before the click. The carve-out's actual protection — an ordinary re-extract of a **trusted**
  document must not sink its vendor — is untouched and separately pinned, because the queue writers never
  touch trust.
- **One more column to keep in lockstep at four writers.** Mitigated by a behavioural test at each writer
  AND at each deliberate non-writer (`Reextract`, `RecordFailedAttempt`'s retry arm,
  `RequeueInterruptedAsync`), plus a source-scanning gate (`Adr0052EnforcementTests`) that pins the read
  surface, the file-level mention set, and — per WRITER rather than per file — that the worker holds
  exactly three trust writes, all funnelled through `SetTrust`, with `ClaimSql` mentioning none.
- **A deploy-overlap window that the design cannot close, in BOTH directions.** Between the new container's
  boot migration and the old container's last request, the OLD code writes `ExtractionStatus` without ever
  writing trust — it does not know the column exists. Not fixable by the column default: the exposed
  transition is an `UPDATE` by the old code, not an `INSERT`, so no default applies. Bounded by the
  health-check overlap, during which an extraction must also complete or a user must confirm. Both halves
  are pinned by a test so the shapes are known rather than surprising.

  - **Fail-OPEN** — the old container writes `ManualRequired` or `Failed`, leaving a distrusted document
    reading `Trusted`: covered until it is re-read or confirmed. This is the pre-#401 behaviour rather than
    a new class of harm, and it SELF-HEALS — the next extraction re-decides trust, and the document
    meanwhile keeps its own `Needs your review` / `Couldn't read` extraction badge, so the ADR 0042
    carve-out's disclosure premise holds. Pinned by
    `A_ManualRequired_row_the_backfill_never_reached_reads_Covered_by_design`.
  - **Fail-CLOSED** — the old container's `PersistSuccess` (a clean re-read) or `ResolveManualReview` (a
    confirmation) writes `Completed` onto a row the boot backfill just marked `Distrusted`. The row lands
    `Completed` + `Distrusted`: excluded from vendor coverage while the extraction badge reads `Read` and
    the compliance badge reads `Compliant`. **State it plainly: this half has NO badge and NO self-heal.**
    Nothing in the read ever forgives a `Distrusted` row, so the vendor sits at Action needed with no
    reason shown on any document surface — the one place ADR 0042's carve-out premise ("the documents list
    already renders a distinct extraction badge beside the compliance badge") is false. **The remedy IS
    user-reachable, and it is the same exit the exclusion always had:** any NEW-container writer rewrites
    trust, so either "Read again" landing a clean read or one "Mark verified" clears the row permanently.
    Pinned, remedy included, by
    `A_Completed_row_the_boot_backfill_distrusted_reads_ActionNeeded_with_nothing_disclosing_why`.
    Recorded rather than closed (#459 review): the alternative is Option E's permanent status→trust
    inference, or splitting the release so the read switch ships a deploy after the writers — real, but it
    trades a bounded one-overlap residue for a second release whose intermediate state (a column four
    writers maintain and nothing reads) has its own drift risk, and the read switch is this ticket's
    deliverable.

- **A THIRD path to both of those pairs, with no deploy overlap involved**
  ([#465](https://github.com/neboxdev/complidrop/issues/465), round 2 of the #459 review). No single new
  writer produces an incoherent pair — `PersistSuccess` pairs `Completed` with `Trusted`, and
  `ResolveManualReview` only withdraws trust on a row whose status it simultaneously raises to
  `ManualRequired`, or cannot move at all. But two of them **interleaved** do, because
  `DocumentEndpoints.MarkVerified` is an unforced READ COMMITTED partial write: it SELECTs, and EF then
  emits only the properties that differ from that snapshot. On an unsettled row `ResolveManualReview`
  leaves `ExtractionStatus` alone, so the UPDATE carries trust WITHOUT the status it was decided beside.
  Land `PersistSuccess`'s whole-tuple commit inside that window and the row ends `ManualRequired` +
  `Trusted` — the fail-OPEN pair, reachable in ordinary operation. (The mirror, `Completed` + `Distrusted`,
  comes from a legacy row carrying an unreadable canonical value at `Pending` + `Trusted`.) This is the
  [ADR 0030](0030-compliance-verdict-combined-unit-of-work.md) last-writer-wins class — the same family as
  [#460](https://github.com/neboxdev/complidrop/issues/460) and
  [#461](https://github.com/neboxdev/complidrop/issues/461), and plausibly absorbed by whatever shape #461
  lands. (#460 has since landed as ADR 0030 Amendment 2, and it does NOT reach this: its grading basis
  fixes what a verdict is computed FROM, while this pair is two columns a partial write left disagreeing.) `UpdateFields` is NOT in it: ADR 0030 Amendment 1 puts that writer under `REPEATABLE READ` with a
  `40001` re-run.

  **Accepted here rather than closed**, and the two narrow closures are why. Widening
  `DocumentWriteConcurrency`'s guard to `MarkVerified` is ruled out by `.claude/reviewers.md` (a change
  that widens the guard to all document writes is itself a finding, pinned). A conditional
  `ExecuteUpdateAsync` predicated on the status it read — the `Reextract` shape — bypasses
  `AuditSaveChangesInterceptor`, so a **human confirmation**, the action that most wants a real audit
  trail, would lose its Before/After diff row and keep only the flat `document.verified` event; ADR 0050
  accepted that trade for a re-arm that already had its own explicit audit row. It also would not make the
  decision fresh — `ResolveManualReview` reads readability from the same stale snapshot — so it refuses
  without fixing, and refuse-then-reload-and-retry is `DocumentWriteConcurrency` under another name. The
  PROPERTY that makes the interleave reachable is pinned instead, by
  `Marking_verified_on_an_unsettled_row_emits_trust_WITHOUT_the_status_it_read`, which reads the host's EF
  command log: closing #465 means changing that test deliberately.

- **A bounded in-flight window where the rollup demotes and the extraction badge does not say why.**
  Distinct from the deploy residue above and a consequence of Amendment 3 itself: a document at
  `Pending`/`Processing` + `Distrusted` makes its vendor read Action needed while the extraction badge reads
  `Reading…` rather than `Needs your review`, because the `ManualRequired` escalation is de-queue-gated and
  the badge is the status. Bounded by one poll and self-healing the moment the read lands. **Two paths reach
  it, and only the first is continuous** (the second was added by this review's round-1 fix and this bullet
  was corrected in round 2 to name it):

  - **A re-armed DISTRUSTED document.** The queue writers leave trust alone, so it read Action needed the
    instant before the click and the user is watching the read they just started. Reachable from the
    sequence pinned by
    `A_distrusted_cert_that_is_re_extracted_and_then_fails_terminally_still_reads_ActionNeeded`.
  - **A save that leaves an unreadable canonical value on an in-flight row.** `ResolveManualReview` decides
    trust from readability on EVERY status, so a `PUT /fields` / `PUT /verify` can withdraw trust at
    `Pending`/`Processing` — a NEW demotion, not a continuation, so the "it read Action needed the instant
    before" clause does not cover it. Accepted anyway: it is fail-CLOSED (ADR 0040), user-initiated one
    request earlier, and the detail page's `ManualReviewCard` DOES name the unreadable field, because it
    renders off `unreadableFields` rather than off the extraction status. Pinned by
    `A_field_save_that_leaves_an_unreadable_value_demotes_a_cert_that_is_already_in_flight`.

  Accepted for those reasons rather than by surfacing trust on the wire, which is the frontend change this
  decision declines.

### Neutral

- **Read-time only, unchanged.** The stored `ComplianceStatus` is untouched — extraction trust and rule
  verdict remain separate axes, exactly as in ADR 0042.
- **ADR 0042's document-level carve-out survives.** Dashboard counts, the `?status=` list and its badges, the
  CSV/PDF export and the per-document compliance badge do NOT move on the trust axis; the documents list
  already renders a distinct extraction badge beside the compliance badge, which is Amendment 1's test for
  when a demotion may stay confined to the rollup. Enforced mechanically.
- **Trust is not on the wire, and ONE frontend change goes with that.** No new field is surfaced: the
  disclosure the carve-out rests on is the detail page's existing extraction badge and review / error
  cards. But the card that carries the actionable half — `ManualReviewCard`'s unreadable variant, the one
  that NAMES the field and says how to clear it — was gated on `extractionStatus === "ManualRequired"`,
  and since this decision an unreadable canonical value withdraws TRUST on every status. It is now gated
  on `unreadableFields` being non-empty as well (#459 review round 2). The two statuses that reach it
  without `ManualRequired` are exactly the ones where the escalation is refused: `Failed`, where the
  page's own manual-entry affordance invites a human to type a value the parser may reject and nothing
  would ever move the status, and `Pending`/`Processing` during a re-extract. Without it those users saw
  "Verified: Yes — A person confirmed these fields" with nothing naming the value blocking their
  coverage.
- **No index.** The column is read only inside per-vendor document projections already filtered by
  `VendorId`; it filters nothing on its own.

## Alternatives considered

### Option A — Keep inferring trust from the status (do nothing beyond #365)

The status quo. **Rejected**: it is the conflation itself. Every consequence above follows from one column
answering two questions, and each fix layered on top (Amendment 2's `Failed` clause, its `IsManuallyVerified`
exit) adds a second place the answer can be wrong. It also leaves the read-time predicate reconstructing a
fact that was *deleted* — no predicate over `(status, flags)` can recover a signal the re-arm overwrote.

### Option B — Persist `Pending` (or a new `ComplianceStatus`) for a distrusted document

**Rejected**, as in ADR 0042 Option A and ADR 0040/0041: nothing re-runs rule evaluation when trust changes,
so the document would be stranded at a stale verdict, and `Pending` is what `ExtractionWorker` claims on.
A read-time exclusion self-heals; a stored one does not.

### Option C — Seed every pre-existing row `Trusted` (a plain column default, no backfill)

**Rejected outright.** It silently re-covers every document the rollup excludes today — the one regression
this change may not cause. The seed for existing data has to be decided, not defaulted.

### Option D — Seed every pre-existing row `Distrusted` ("earn trust by being re-read")

Superficially the fail-closed choice. **Rejected**: it drops every currently-covered vendor to ActionNeeded
on deploy, with no route back short of re-extracting the whole corpus — re-paying Document AI + the LLM per
document — or hand-confirming each one. A mass false-negative is not a safe default; it is a different wrong
answer plus a bill. The chosen seed is fail-closed *relative to today*, which is the property that matters.

### Option E — A nullable column with a legacy fallback (`NULL` ⇒ infer from the status the old way)

The textbook expand/contract shape, and it would close the deploy-overlap window in Consequences. **Rejected**
as a bad trade here: it keeps the status→trust inference alive inside `ComputeCoverage` indefinitely (there is
no forcing function for the contract step), which means keeping `ExtractionStatus` and `IsManuallyVerified` on
`DocCoverageInfo` and keeping two ways to answer one question — the exact shape this ADR exists to remove, and
the shape `.claude/reviewers.md` calls out elsewhere as "a second mechanism nothing pins equal". The window it
buys is one health-check overlap during which an extraction must also complete, against a residue that is
permanent by construction.

### Option F — Also demote the document-level surfaces on the trust axis

**Rejected**, inheriting ADR 0042's carve-out and Amendment 1's test for it: the documents list already
renders a distinct extraction badge (`Needs your review` / `Couldn't read`) beside the compliance badge, and
the detail page carries the review and processing-error cards, so the document surfaces already disclose this
state. Demoting the counts too would be the #294-class count-vs-badge split. (Contrast ADR 0048, which
mirrors its demotion everywhere precisely because no second badge says "nothing graded this".)

### Option G — Make trust a `bool IsDistrusted` instead of an enum

**Rejected** for a reason the codebase has already paid for once: a bool has no room for a third state, and
the plausible next question here ("distrusted *why* — low confidence, unreadable, or unread?") is exactly the
kind of thing that gets crammed into an existing column. An enum whose members are named states leaves that
door open without forcing it, and it reads the same way `ExtractionStatus` and `ComplianceStatus` already do.

## Amendment 1 (2026-08-03) — trust is this READ's conclusion about the ROW THIS COMMIT WILL LEAVE

§2 says the trust value is "the worker's OWN conclusion, computed from what the model just returned". The
first half of that is a statement about OWNERSHIP and is unchanged. The second half named a SUBJECT, and
it named the wrong one — the conclusion has to be about a row, and the row it was about had ceased to
exist. [#467](https://github.com/neboxdev/complidrop/issues/467).

**The mechanism, and it is ADR 0030 Amendment 2's exactly.** `ExtractionWorker.PersistSuccess` asked
`DocumentFieldReadability.UnreadableCanonicalFields(doc)` of the TRACKED entity —
`ProcessDocumentAsync`'s pre-run snapshot, held across the whole OCR + LLM run. Its result drives
`distrusted`, hence `ExtractionStatus` and `SetTrust`, hence ADR 0042 coverage exclusion.
`CanonicalDocumentFields.ApplyToTypedColumn` **assigns**, and an assignment equal to the minutes-old
snapshot leaves the property unmodified, so EF omits the column and whatever a request committed in the
window survives. The walk therefore judged values the commit was about to leave behind.

Reachable in ordinary use: a document sits at `ExpirationDate = null` from a prior
[ADR 0040](0040-unreadable-canonical-value-fails-closed.md) unreadable read; the user clicks **Read
again** and, while it runs, types the correct expiration and saves; the model returns the same
unparseable text (`expiration_date: "12/31/2026 (per endorsement)"`). `ApplyToTypedColumn` writes null
over a null snapshot, the column stays out of the UPDATE, and the row keeps the user's valid date — while
the walk, looking at the snapshot, sees a null column beside non-blank unparseable raw text and commits
`ManualRequired` + `Distrusted`.

**What that costs is the DISCLOSURE, not the verdict.** `DocumentFieldReadability` re-derived at READ
time against the persisted row finds nothing (`TypedColumnValue` is non-null), so
`GET /api/documents/{id}` reports `unreadableFields: []` while `ComputeCoverage` drops the document from
in-force coverage and the vendor reads **Action needed**. § Neutral's last bullet is the premise that
fails: the disclosure this decision rests on is the detail page's review card, and `ManualReviewCard`
picks its variant off `unreadableFields`. With that list empty it renders the LOW-CONFIDENCE copy —
*"the ones outlined in amber are the least certain"* — on a document read at 0.95 and 1.0, so nothing is
outlined. That is the ADR 0040 Amendment 2 dead end reached from the other side, and it is the state the
#459 round-2 gate exists to avoid. (The ticket described it as rendering NO card; the gate is
`ManualRequired || unreadableFields.length > 0`, an OR, so a card does render — it just points at
something that is not there. Recorded because the record should be right, not because it changes the
call.) `Mark verified` is the exit, so it is recoverable rather than stuck.

### Decision

**The readability trigger reads the [ADR 0030 Amendment 2](0030-compliance-verdict-combined-unit-of-work.md#amendment-2-2026-08-02--the-worker-grades-the-row-its-own-commit-will-leave)
grading basis** — `Services/DocumentGradingBasis.AfterPendingCommitAsync`, the row's current committed
values overlaid with exactly the properties EF is about to write — falling back to the tracked entity
when there is no basis. One line in `PersistSuccess`:

```csharp
var unreadableFields = DocumentFieldReadability.UnreadableCanonicalFields(basis ?? doc);
```

The trust decision and the `ExtractionStatus` write move below the grading `try` so they can be decided
from it, and `basis` is hoisted out of that `try` so a failure in `ApplyEvaluationAsync` — which happens
strictly AFTER a successful basis read — does not cost the basis.

**This is not the same question as ownership, and §2 is amended rather than reversed.** Four things §2
asserts are untouched: these four writers are the only ones, the queue path still writes nothing, the
three worker writers still go through `SetTrust`, and `SetTrust` still FORCES the column. The force is
what makes the conclusion this writer's; the basis is what makes it about the right row. ADR 0030
Amendment 2 already shipped exactly this pair for the verdict on this very method — grade the basis
(`ApplyEvaluationAsync(db, doc, basis, ct)`), then force the answer in (`ForceVerdictWrite`) — and the
sentence in §2 that draws the contrast with #460 stays true, because that contrast is about verdict
INPUTS a REQUEST owns and no input is re-asserted here either. The basis is read-only with respect to
`doc`; the worker still emits exactly the columns it emitted before.

**Only ONE of the four `ManualRequired` triggers moves, and the split is the point.** Low average
confidence, a low-confidence verdict-bearing field ([ADR 0042](0042-distrusted-extraction-per-field-gate-and-coverage-exclusion.md))
and the model's `NeedsReprocessing` signal are facts about the READING. They have no mirror on the row
and never will, so there is nothing to ask a basis about and they keep describing this read. Readability
is a fact about a ROW. The two live in one boolean because they answer one question — *should a human
look at this?* — but they are measured against different things, and conflating them is what produced the
bug.

### The invariant this buys, and why it holds

**On the persisted row, a `ManualRequired` raised by the READABILITY trigger agrees with the read-time
`unreadableFields` list** — so a document routed to review *for that reason* always names a cause. Both
are now derived from the same row: the basis IS the row this commit leaves (for every property this
writer sets), and the read-time list is derived from that row. It is agreement **by construction**, the
same argument Amendment 2 makes for the verdict, and it is bounded by the same residual — a request
committing between the basis read and the `SaveChanges` (ADR 0030 Amendment 2 § What stays open, still
open, still not given a remedy that costs an extraction).

**The invariant is SCOPED to that trigger, and saying otherwise would misread the split above** (review
round 1, C2). The other three triggers commit `ManualRequired` + `Distrusted` with an EMPTY
`unreadableFields` list, by design: they describe the reading, and the reading has no row mirror. So
**`Distrusted` beside an empty list is a common, legitimate shape** — a reviewer holding a diff against
this section must not read one as a violation. The tests state the same scope: their biconditional is
only legitimate because every fixture holds the other three triggers off (0.95 on every field,
`NeedsReprocessing: false`).

**What names each of those three — CORRECTED in review round 2.** This paragraph first said the two
confidence gates "are named on the detail page by the amber field outline". That was loose in one way
and false in another, and the false way is this amendment's own doing:

- *Loose.* The outline is TIERED rather than amber: `page.tsx`'s `fieldBorderClass` returns nothing at
  or above 0.9, amber below that, and ROSE below 0.7. The per-verdict-bearing-field gate ([ADR
  0042](0042-distrusted-extraction-per-field-gate-and-coverage-exclusion.md)) fires below 0.7, so the
  field it measures is outlined **rose**; only the AVERAGE gate can leave an amber-outlined field
  behind.
- *False, and introduced here.* § *The raw copies come too* pins a RECONCILED row's confidence to 1.0.
  So when the field that tripped a confidence gate is ALSO the field the reconciliation corrected,
  nothing on the document is outlined at all. Reachable on this amendment's own interleave: the model
  re-reads an expiration at 0.5 and answers with the value the row held at claim time, while a mid-run
  save moved that column — the gate fires from the response, the column stays out of the `UPDATE`, and
  the row's copies are reconciled to the user's date at 1.0. Pinned by
  `A_reconciled_field_that_tripped_the_confidence_gate_names_itself_on_the_row`.

**That row still names itself, and by the more honest mechanism.** A reconciled row carries
`IsManuallyEdited` (the page prints `✎ Manually edited`) and `OriginalValue` (printed *was: …*), so the
field the user is being asked to look at is exactly the one visibly marked as no longer the model's
reading, with the model's reading beside it. **Keeping the outline instead — leaving the model's
confidence on the row — is REJECTED** (the remedy round 2's finding proposed): after the reconciliation
the value in that input is the USER's committed value, so a 0.5 there outlines their own typed date and
prints *Please verify* about a value the model never produced. A false statement about content is worse
than a marker in a different colour, and this triple was already decided for the human edit — the same
three assignments, now literally the same code (§ *The raw copies come too*). Moving
`hasLowConfidenceVerdictField` onto the basis is refused for the reason the split above gives: it is a
fact about a READING, and "make the four triggers consistent" is the bug. `NeedsReprocessing` on its own
still names nothing at all, unchanged.

Note what this does NOT reintroduce. The ADR 0040 dead end is the confidence copy shown on a document
"whose flag they can't clear until the named value is corrected" (`page.tsx`). Here nothing is
unreadable, so the very **Save changes** the card instructs runs `ResolveManualReview`, clears
`ManualRequired` and writes `Trusted` — the instruction is actionable, which is what separates this
shape from the one this amendment closes.

Two further facts make the change safe in the direction that matters:

- **The new distrust set is a strict SUBSET of the old one, so nothing that failed closed now fails
  open.** For the basis to read unreadable, the basis's typed column must be null while its raw value is
  non-blank and unparseable. The raw value comes from the response mirror; a field present in the mirror
  is a field `ApplyToTypedColumn` ran for; an unparseable value makes it assign `null`. So the TRACKED
  column is null too, whether or not the assignment counted as modified — i.e. basis-unreadable implies
  tracked-unreadable. The difference between the two answers is exactly the false-positive population:
  documents where a request committed a readable value the row keeps.
- **The fallback is the old answer, and by the line above that is the fail-CLOSED one.** A `null` basis
  (the row is genuinely gone — a hard delete, which no production path performs) or a basis read that
  threw both fall back to the tracked entity. Over-distrusting is recoverable by one `Mark verified` or
  one clean re-read; under-distrusting is a green badge over a value nothing can parse.

The inverse interleave — a mid-run save that BREAKS a previously-readable value — lands correctly for a
different reason, and is pinned rather than argued: the worker's own extracted values overwrite BOTH
copies (the typed column is modified because it differs from the snapshot, and the JSON mirror is
rewritten wholesale), so the row the commit leaves is the clean one and the persist RESTORES the trust
the save withdrew (Amendment 3 path 2 in
[ADR 0042](0042-distrusted-extraction-per-field-gate-and-coverage-exclusion.md) is what withdrew it).

### The raw copies come too (review round 1, C1)

Fixing WHICH ROW the conclusion is about exposed a second copy of the same mechanism, one layer down.
`ApplyToTypedColumn` assigns and can be omitted; the `ExtractionFields` mirror and the `DocumentField`
rows are rewritten from the response **unconditionally**. So the ticket's own interleave committed a row
whose expiration column held the user's valid date while both copies the field editor renders held
`"12/31/2029 (per endorsement)"` — under a `Completed`/`Trusted` badge, a `Compliant` verdict graded from
the surviving column, and `unreadableFields: []`, because `DocumentFieldReadability` short-circuits on the
non-null typed column before it ever looks at the raw value the user is being shown. The row was "clean"
only in the one place the predicate looked.

`ExtractionWorker.ReconcileCanonicalCopiesWithTheRow` closes it, on the same basis and before the grade:
where `CanonicalDocumentFields.SameTypedColumn` says the basis and the tracked entity disagree, the
response's value is one the row will not be graded from, so both copies take the basis's rendering and the
model's own answer is demoted to `DocumentField.OriginalValue` — which the detail page already renders as
*was: …*, and `ExtractionRawJson` keeps verbatim regardless. It runs before `ApplyEvaluationAsync` because
the mirror is itself a verdict input (`LookupValue`'s raw-string fallback). Typed columns are untouched:
forcing one is ADR 0030 Amendment 2 Option G and stays refuted, and this worker still emits exactly the
columns it emitted before — it now clobbers strictly LESS of a request's value than it did.

**The demotion has ONE owner** (review round 2, C1 + S5). `DocumentField.ApplyCorrection` carries the
four-assignment "this row now holds a corrected value" shape — capture-once `OriginalValue`, the new
`FieldValue`, `IsManuallyEdited`, the pinned `Confidence` — and BOTH writers call it: this reconciliation
and `DocumentEndpoints.UpdateFields`, which had the identical four lines with one difference that turned
out to matter. Its `OriginalValue` assignment was GUARDED and the worker's copy was not, and the worker
is the one that can reach the same row twice: `fieldsDict` is keyed ORDINALLY (it is built straight from
the response) while the staged-row match is `OrdinalIgnoreCase`, so a response answering one canonical
field under two spellings (`expiration_date` and `Expiration_Date`, both unparseable so neither reaches
the column) reconciled those rows on both passes. The second pass demoted the value the first had just
written, leaving `OriginalValue == FieldValue` — which the detail page's guard
(`f.originalValue && f.originalValue !== f.fieldValue`) renders as NOTHING. The model's own answer
erased from the row, i.e. the exact disclosure this section promises is preserved. Pinned by
`One_canonical_field_answered_TWICE_still_keeps_each_rows_own_answer`.

Three consequences worth recording rather than rediscovering:

- **It closes the other direction too**, where the harm is fail-OPEN rather than merely confusing: a
  mid-run CLEAR that the read did not overwrite used to leave both copies showing a date the column does
  not carry, on a document that can therefore never turn `Expired` and never triggers a reminder.
- **Once it has run, the tracked entity and the basis give the SAME readability answer**, by construction
  — a reconciled field's raw copy is blank or a canonical rendering, so it is never unreadable on either,
  and an unreconciled one has equal typed columns on both. `basis ?? doc` therefore states the intent and
  owns the fallback (no basis ⇒ no reconciliation ⇒ the pre-#467 answer); what the tests can still
  discriminate is the ORDER — moving the readability walk back above the reconciliation returns the
  ticket's bug intact, and does so in two tests. The ORDER against the GRADE is load-bearing for a second
  reason and is now pinned by a VERDICT rather than by prose (review round 2, S3): the mirror is a
  verdict input via `LookupValue`'s raw-string fallback, which is reachable only where a canonical
  field's typed column is NULL, so `A_mid_run_CLEAR_…` carries an `expiration_date` `required` rule and
  goes red on a torn pair if the reconciliation is moved below `ApplyEvaluationAsync`.
- **A reconciled row's `Confidence` is pinned to 1.0, and that is a decision about what the number
  means, not bookkeeping.** The detail page outlines an input by confidence and prints "Please verify"
  beside it — claims about how well the MODEL read the value shown. After a reconciliation the value
  shown is the row's committed one, which in the interleave that reaches here is a value a USER typed,
  so carrying the reading's uncertainty onto it would flag their own text as doubtful about content the
  model never produced. What discloses the row instead is the other two assignments; § *The invariant
  this buys* records the one case where this removes an outline the record used to promise, and why the
  trade goes this way. `Confidence` has exactly one backend consumer — the detail DTO projection — so
  no gate, verdict, rule operator or coverage predicate moves either way.

### Consequences

- **The bug closes at its root**, and closes for every canonical field at once rather than for the one
  the ticket happened to reach: nothing is enumerated, the basis is derived from the change tracker.
- **Trust's SUBJECT is now the same row the verdict's is**, so the two conclusions this writer commits
  can no longer be about different documents. That is a coherence gain the ticket did not ask for and is
  the strongest argument for this shape over the alternatives below.
- **`ExtractionStatus` and `ExtractionTrust` fall out of the basis's own overlay**, because they are now
  assigned after it is read. Immaterial by inspection and recorded so it is not rediscovered as a defect:
  `ComplianceCheckService` reads neither column, and `DocumentFieldReadability` reads only the canonical
  fields. `SetTrust` still forces its column, so the ordering costs the write nothing. Noted on
  `DocumentGradingBasis.AfterPendingCommitAsync` beside the pre-existing `UpdatedAt` exception.
- **`Services/DocumentGradingBasis.cs` joins the allow-list in `Adr0052EnforcementTests`**, for a
  comment. § Consequences/Negative's "one more column to keep in lockstep at four writers" is what that
  gate defends, so a new file naming the column has to be admitted deliberately — and admitted as PROSE
  ONLY, which is now enforced rather than promised in the allow-list's own value string
  (`Services_that_merely_TALK_about_trust_never_touch_it` strips comments and requires the identifier to
  be gone). That distinction earns its keep here specifically: the helper MATERIALIZES a `Document` via
  `PropertyValues.ToObject()`, so assigning or reading trust on that instance is one line away and would
  read as tidy prediction while being a fifth writer or a second read surface.
- **A second consumer now depends on the basis read**, so its failure mode matters twice. It still sits
  inside the degrade guard and still never throws out of `PersistSuccess` — a throw there is the re-paid
  Document AI + LLM run this codebase measures every worker change against — and that placement keeps its
  existing pin (`A_failing_basis_read_degrades_the_verdict_without_requeuing_the_extraction`) plus a new
  one for the trust arm.
- **The deploy-overlap and `MarkVerified` residues in § Consequences are unchanged**, in both directions.
  They are about two writers disagreeing on a PAIR of columns, not about which row a single writer judged.

### Alternatives considered (Amendment 1)

- **Option H — keep trust describing THIS READ's output, and make the disclosure agree by persisting the
  unreadable-field list.** The literal reading of §2, and the only way to keep it. **Rejected**: it needs
  a new column (or a re-derivation from `ExtractionRawJson` / the `DocumentField` rows), and what it would
  then disclose is FALSE about the document — the card would say *"We couldn't read the expiration date
  on this document"* beside an input holding the valid date the user typed and saved, and tell them to
  correct it. Naming a cause that is not true is worse than naming none; the ticket's requirement is that
  a review NAMES a cause, not that it names a memory.

  **Correction (review round 1, C1).** As first written this rejection rested on a premise that was
  FALSE at the time: the persist rewrote BOTH raw copies from the response, so the input the card would
  have sat beside held the model's unparseable text, not the user's date. The rejection was right; the
  reason given was not, and the true state was worse than either — see § *The raw copies come too*
  below, which makes the sentence above TRUE rather than merely well-intentioned. Option H is still
  rejected on its first clause: a persisted list is a memory of a reading, and this decision is that the
  conclusion must be about the row.
- **Option I — leave the walk on the tracked entity and drop the coverage exclusion for this case.**
  Cheaper, and it removes the user-visible harm (the vendor stops reading Action needed). **Rejected**: it
  is ADR 0042's exclusion with a hole cut in it, and the hole is unprincipled — the exclusion would apply
  or not depending on whether a request happened to commit inside an extraction window. It also leaves the
  stored `ManualRequired` in place, so the document still reads "Needs your review" with nothing to fix.
- **Option J — gate the trust write on the status the way `ResolveManualReview` gates its escalation.**
  §3 already refutes this for the request path (withdrawing trust de-queues nothing, so `wasSettled` has
  no business gating it), and it does not address this bug at all: the persist's write is not being
  refused, it is being decided from the wrong row.
- **Option K — `REPEATABLE READ` or an `xmin` token on the worker so the pre-run snapshot cannot go
  stale.** The general fix for the whole family. **Rejected, unchanged from ADR 0030 Option A, Amendment 1
  and Amendment 4 Option P**: every detecting shape throws out of the `SaveChanges` whose failure costs a
  re-paid extraction.
- **Option L — compute readability from the RESPONSE alone, ignoring the row entirely** (did the model
  return a canonical value we could not parse?). Simple, and it is arguably the purest statement of "this
  read's output". **Rejected**: it is the shape #383 review round 2 already removed. It re-introduces a
  second mechanism for "is this document in the #383 state?" beside `DocumentFieldReadability`, with
  nothing pinning the two equal — and it is wrong in the ticket's own case, where the model's unparseable
  answer never reaches the column.
- **Option M — leave a reconciled row's `Confidence` at the model's value, so a confidence gate's field
  keeps its outline** (review round 2). The one thing the reconciliation takes away from the record as
  first written, and the finding that raised it proposed exactly this. **Rejected**: the outline and the
  "Please verify" hint sit in the SAME field row as the value, and after the reconciliation that value
  is the row's committed one — the user's, in every interleave that reaches here. A 0.5 there marks
  their own typed date as doubtfully READ, about content the model never produced. It also splits a
  triple the codebase decided once and now shares as one method (`DocumentField.ApplyCorrection`, §
  *The raw copies come too*): a corrected row is a corrected row whichever writer corrected it. The
  disclosure that replaces the outline is `IsManuallyEdited` + `OriginalValue`, and it is strictly more
  specific — it names the value AND shows what it replaced.
- **Option N — give reconciled canonical fields their own wire list, the way `unreadableFields` already
  works, so the confidence trigger keeps a name of its own.** The finding's fallback if 1.0 stays.
  **Rejected for this ticket, on cost rather than principle**: it is a new DTO field, a new frontend
  marker and a second "look at this field" mechanism beside the two the page already has, bought for a
  case where the row is ALREADY marked (`✎ Manually edited`, *was: …*) and where the instructed remedy
  works — `Save changes` clears the review, because nothing is unreadable. Recorded here so a future
  reader can see the option was weighed rather than missed.

## References

- Tickets: [#459](https://github.com/neboxdev/complidrop/issues/459), [#467](https://github.com/neboxdev/complidrop/issues/467) (Amendment 1), [#48](https://github.com/neboxdev/complidrop/issues/48) (rolling bug-fix epic); the read-time predecessors [#401](https://github.com/neboxdev/complidrop/issues/401) and [#365](https://github.com/neboxdev/complidrop/issues/365)
- ADRs: [0042](0042-distrusted-extraction-per-field-gate-and-coverage-exclusion.md) (the distrust signal and the coverage exclusion this makes durable — Amendment 3 records the reversal of its in-flight carve-out), [0040](0040-unreadable-canonical-value-fails-closed.md) (the unreadable-value escalation `ResolveManualReview` re-raises, and the walk Amendment 1 re-points at the basis), [0048](0048-never-graded-document-asserts-no-affirmative-verdict.md) (the OTHER axis that withholds an unread document from coverage), [0050](0050-reextract-refuses-a-live-extraction-claim.md) (the re-arm this must survive), [0016](0016-apply-ef-migrations-on-startup.md) (auto-migrate on boot — why the migration is additive and cheap), [0030](0030-compliance-verdict-combined-unit-of-work.md) (the unit of work `PersistSuccess` writes both columns inside; Amendment 2 is the grading basis Amendment 1 here borrows, and its § What stays open is where #467 was recorded)
- Code: `Entities/Document.cs` (`ExtractionTrust`), `Data/ModelConfiguration.cs` (mapping + store default), `Migrations/20260802080136_AddDocumentExtractionTrust.cs` (the additive migration + seed), `BackgroundServices/ExtractionWorker.cs` (`PersistSuccess`, `MarkFailed`, `RecordFailedAttempt`), `Endpoints/DocumentEndpoints.cs` (`ResolveManualReview`, `Reextract`), `Endpoints/VendorEndpoints.cs` (`ComputeCoverage`, `DocCoverageInfo`), `DTOs/Vendors/VendorDtos.cs` (`VendorCoverage`'s contract comment), `frontend/src/app/(dashboard)/documents/[id]/page.tsx` (the one frontend change — `ManualReviewCard`'s gate), `CompliDrop.Api.Tests/Adr0052EnforcementTests.cs` (the read-surface / writer-set gate), `CompliDrop.Api.Tests/TestHelpers/SourceScan.cs` (the shared scanner the gates use); for Amendment 1 `Services/DocumentGradingBasis.cs` (the basis, now read by both of this writer's conclusions), `Services/DocumentFieldReadability.cs` (the one predicate, unchanged — only its SUBJECT moved) and, for § *The raw copies come too*, `BackgroundServices/ExtractionWorker.cs` (`ReconcileCanonicalCopiesWithTheRow`) + `Services/CanonicalDocumentFields.cs` (`SameTypedColumn`, the typed comparison it keys on) + `Entities/Document.cs` (`DocumentField.ApplyCorrection`, the ONE owner of the corrected-row shape, shared with `DocumentEndpoints.UpdateFields`)
- Tests (Amendment 1): `CompliDrop.Api.Tests/ExtractionWorkerStaleBasisTests.cs` — six pins sharing `AssertTrustAgreesWithTheRowAsync`, which reads the persisted row AND `GET /api/documents/{id}` and requires the committed trust/status pair and the wire `unreadableFields` list to agree. The biconditional is only legitimate because every fixture holds the other three triggers off (0.95 on every field, `NeedsReprocessing: false`), so readability is the sole trigger — it is NOT the general invariant (see § The invariant this buys). The raw-copy half is asserted through `ReadRenderedFieldAsync`, which reads the `fields[]` row value, the `extractionFields` mirror entry and `originalValue` off the same wire response the detail page renders from; `A_mid_run_CLEAR_the_read_did_not_overwrite_leaves_no_field_value_claiming_otherwise` is its fail-OPEN direction, and `A_failing_GRADE_still_leaves_the_trust_decision_judging_the_basis_it_already_read` covers the arm where the basis read SUCCEEDS and the recompute throws. `A_canonical_value_a_mid_run_edit_FIXED_leaves_no_review_with_nothing_to_name` is the ticket's interleave and the one that goes red on the pre-fix code; `A_canonical_value_this_read_leaves_unreadable_still_routes_to_review_and_NAMES_it` pins fail-CLOSED and doubles as the discriminator against a readability walk over a plain re-read of the row (ADR 0030 Amendment 2 Option F's mistake in a new place — that shape would call the document clean while its committed column is null); `A_mid_run_edit_that_BREAKS_a_value_the_read_replaces_does_not_strand_the_document` is the inverse direction, with an in-window probe asserting the save really withdrew trust first so the restoration is not vacuous; and `A_failing_basis_read_falls_back_to_what_this_read_produced_and_still_withdraws_trust` pins the fallback arm, the degrade, and that the failure still costs no second OCR + LLM run. Review round 2 adds four that are deliberately NOT under the biconditional: `One_canonical_field_answered_TWICE_still_keeps_each_rows_own_answer` (the ordinal-vs-ignore-case double demotion), `A_reconciled_field_that_tripped_the_confidence_gate_names_itself_on_the_row` (the per-field gate arm every other fixture holds off, via the per-field-confidence `ExtractedAt` helper — it asserts the `ManualRequired` + `Distrusted` + empty-list + nothing-outlined shape directly, and goes red under Option M), `A_reconciled_AMOUNT_takes_the_columns_own_rendering_and_keeps_the_models_answer` (the money branch of `SameTypedColumn`, and the `numeric(18,2)` rendering the reconciliation lands in the field editor) and `A_field_whose_column_the_row_AGREES_on_is_left_exactly_as_the_model_read_it` (an ordinary re-extraction the reconciliation must NOT touch — the integration half of the typed-not-a-rendering property, whose pure half is `CanonicalDocumentFieldsTests.An_amount_compares_on_the_NUMBER_not_on_its_rendering`)
