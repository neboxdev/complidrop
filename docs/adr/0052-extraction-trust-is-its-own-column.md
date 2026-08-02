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
`Failed` + `Trusted` (a distrusted basis rolling up as Covered — the ADR 0042 hole, in a shape this ADR
otherwise records as reachable only through a deploy overlap), or the mirror image, a clean re-read that
no-ops and strands the document at ActionNeeded.

This is **not** the [ADR 0030](0030-compliance-verdict-combined-unit-of-work.md) stale-snapshot residual
([#460](https://github.com/neboxdev/complidrop/issues/460)) and must not be filed under it. That residual is
about a writer *grading from* verdict INPUTS the row has moved on from and re-asserting them; nothing is
re-asserted here. The value is this read's own conclusion, computed from what the model just returned, and
the table above says these writers own it — "last writer wins on the whole tuple" is the intended
semantics, and forcing the column is what makes this writer actually be one.

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
    deliverable. Both halves are UNREACHABLE through the new writers, which is what keeps them a
    deploy artifact rather than a live state machine: `PersistSuccess` pairs `Completed` with `Trusted`,
    and `ResolveManualReview` only withdraws trust on a row whose status it simultaneously raises to
    `ManualRequired` — or cannot move at all.

- **A bounded in-flight window where the rollup demotes and no document surface says why.** Distinct from
  the deploy residue above and a consequence of Amendment 3 itself: a re-armed DISTRUSTED document sits at
  `Pending`/`Processing` + `Distrusted`, so the vendor reads Action needed while the extraction badge reads
  `Reading…` rather than `Needs your review`. Same disclosure premise, same gap — but unlike the fail-closed
  twin it is bounded by one poll and self-heals the moment the read lands, and the direction is the safe
  one (the document read Action needed the instant before the click, and the user is watching the read they
  just started). Accepted for those reasons rather than by surfacing trust on the wire, which is the
  frontend change this decision declines. Reachable from the sequence pinned by
  `A_distrusted_cert_that_is_re_extracted_and_then_fails_terminally_still_reads_ActionNeeded`.

### Neutral

- **Read-time only, unchanged.** The stored `ComplianceStatus` is untouched — extraction trust and rule
  verdict remain separate axes, exactly as in ADR 0042.
- **ADR 0042's document-level carve-out survives.** Dashboard counts, the `?status=` list and its badges, the
  CSV/PDF export and the per-document compliance badge do NOT move on the trust axis; the documents list
  already renders a distinct extraction badge beside the compliance badge, which is Amendment 1's test for
  when a demotion may stay confined to the rollup. Enforced mechanically.
- **No frontend change.** Trust is not surfaced on the wire: the detail page already shows the extraction
  status badge and the review / error cards, which is the disclosure the carve-out rests on.
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

## References

- Tickets: [#459](https://github.com/neboxdev/complidrop/issues/459), [#48](https://github.com/neboxdev/complidrop/issues/48) (rolling bug-fix epic); the read-time predecessors [#401](https://github.com/neboxdev/complidrop/issues/401) and [#365](https://github.com/neboxdev/complidrop/issues/365)
- ADRs: [0042](0042-distrusted-extraction-per-field-gate-and-coverage-exclusion.md) (the distrust signal and the coverage exclusion this makes durable — Amendment 3 records the reversal of its in-flight carve-out), [0040](0040-unreadable-canonical-value-fails-closed.md) (the unreadable-value escalation `ResolveManualReview` re-raises), [0048](0048-never-graded-document-asserts-no-affirmative-verdict.md) (the OTHER axis that withholds an unread document from coverage), [0050](0050-reextract-refuses-a-live-extraction-claim.md) (the re-arm this must survive), [0016](0016-apply-ef-migrations-on-startup.md) (auto-migrate on boot — why the migration is additive and cheap), [0030](0030-compliance-verdict-combined-unit-of-work.md) (the unit of work `PersistSuccess` writes both columns inside)
- Code: `Entities/Document.cs` (`ExtractionTrust`), `Data/ModelConfiguration.cs` (mapping + store default), `Migrations/20260802080136_AddDocumentExtractionTrust.cs` (the additive migration + seed), `BackgroundServices/ExtractionWorker.cs` (`PersistSuccess`, `MarkFailed`, `RecordFailedAttempt`), `Endpoints/DocumentEndpoints.cs` (`ResolveManualReview`, `Reextract`), `Endpoints/VendorEndpoints.cs` (`ComputeCoverage`, `DocCoverageInfo`), `DTOs/Vendors/VendorDtos.cs` (`VendorCoverage`'s contract comment), `CompliDrop.Api.Tests/Adr0052EnforcementTests.cs` (the read-surface / writer-set gate), `CompliDrop.Api.Tests/TestHelpers/SourceScan.cs` (the shared scanner the gates use)
