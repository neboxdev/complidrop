# 0040. An unreadable canonical value fails closed — "absent" and "unreadable" are different facts

- **Status:** accepted
- **Date:** 2026-07-22
- **Deciders:** Ruben G. (founder), Claude (implementing #383)

## Context

Three extracted fields live in typed `Document` columns rather than only in the `ExtractionFields`
JSON — `ExpirationDate`, `EffectiveDate`, `GeneralLiabilityLimit` — because the date windows, the
dashboard counts, the documents-list filters and the reminder queries all need to compare them in
SQL. `CanonicalDocumentFields.ApplyToTypedColumn` is the **only** writer of those three columns, and
both write paths funnel through it (ADR 0017): `ExtractionWorker.PersistSuccess` and
`DocumentEndpoints.UpdateFields`.

That helper **clears the column to null when the value fails to parse**, deliberately, so a stale
prior value can never contradict the field the user is looking at. But a null column is the same null
whether the certificate carried **no** value or carried one we **could not read** — and those two
facts have opposite compliance meanings. Nothing downstream could tell them apart, so an unreadable
value failed **open** in three places at once, all pointing the same way:

1. **The verdict.** `ComputeOutcome`'s Expired / ExpiringSoon branches are guarded on
   `doc.ExpirationDate is DateTime exp`. A null column skips both, so the document can never enter
   Expired however old the certificate is.
2. **The rule.** The seeded `expiration_date required` rule **passed anyway**: `LookupValue`
   preferred the typed column but fell back to the raw `ExtractionFields` string when it was null,
   and that string was non-empty. `required` is `!string.IsNullOrWhiteSpace(actual)`, so unreadable
   text satisfied it.
3. **The reminder backstop.** `ReminderBackgroundService`'s windows key on the same null
   `ExpirationDate`, so nothing was ever mailed.

The composite user-visible result, reproduced live by the founder on the running dev stack: setting a
document's expiration to `2020-01-01 (per endorsement)` flipped the badge from Expired back to
**Compliant**, showed the "Expires" tile as **"—"**, and rendered the affirmative sentence
**"Insurance has not expired"** with a green check (`requirements.ts` `coi_not_expired`) — on a
certificate any human reads as five years expired. High confidence, no `needsReprocessing` signal, no
processing error: nothing anywhere flagged it.

This is the same compliance-safety class as #362 — a silent false-Compliant concealing a real
liability — and for a product whose core promise is "never miss an expiration" it was treated as a
pre-launch blocker rather than backlog.

A secondary, opposite-direction defect surfaced from the same parse: `NumberStyles.Any` +
`CultureInfo.InvariantCulture` allows a currency symbol, but the invariant culture's symbol is `¤`,
**not** `$`. So `"$1,000,000"` — the most natural way for both a model and an owner to write a
coverage limit — failed to parse, nulled `GeneralLiabilityLimit`, and then failed the `min_value`
comparison with "Unable to parse numeric comparison": a false **NonCompliant** on a certificate that
genuinely met the floor.

## Decision

**A canonical value we could not read certifies nothing, and says so.** Three parts:

**1. The parse reports what it did.** `ApplyToTypedColumn` returns a `TypedColumnResult`:
`NotCanonical` / `Blank` / `Parsed` / `Unreadable`. `Unreadable` means *a non-blank value for one of
the three canonical fields that failed to parse* — precisely the case where the column ends up null
while the document really does carry a value. Blank stays `Blank`: an absent value is an **honest**
reading of a certificate that shows none, and must not be treated as a defect.

**2. Rule evaluation fails closed.** `ComplianceCheckService.EvaluateRule` guards **ahead of the
operator switch**: an unreadable canonical value fails every operator, carries the raw text as
`ActualValue` so the user can see what needs correcting, and gets a note that says we could not read
it — deliberately distinct from `"Field missing."`, because the two assert opposite facts about the
certificate and only one is a cue to go fix a value. The guard sits ahead of the switch rather than
inside each operator because `contains "2026"` would otherwise **substring-match** the raw text
`"12/31/2026 (per endorsement)"` — a hit on a date the system cannot actually evaluate.

`LookupValue`'s raw-string fallback is independently narrowed to values that **re-parse**. A legacy
row whose typed column is null but whose JSON holds a readable value keeps resolving exactly as
before; only the unreadable case loses the fallback. `LookupValue` is `internal` and reachable
outside `EvaluateRule`, so it is closed at the source too, not only behind the guard.

**3. Both writers route the document to a human.** `ExtractionWorker.PersistSuccess` and
`DocumentEndpoints.UpdateFields` each degrade the document to `ExtractionStatus.ManualRequired` when
any canonical field came back `Unreadable`. This is the backstop for the case part 2 cannot reach:
an org whose checklist carries **no rule** on the field would otherwise still grade Compliant with a
silently-null column. On the extraction path this is a *third* trigger alongside low average
confidence and the model's own reprocess signal — neither of which fires for a confidently-read date
in a shape the parser can't handle.

**Secondary:** `CanonicalDocumentFields.TryParseAmount` strips **edge** currency symbols and
whitespace before parsing, and is used for **both** sides of the `min_value` comparison, so an
owner-typed `"$1,000,000"` minimum reads the same as the document's. The strip is edge-only and
narrow on purpose: anything it cannot reduce to a bare number (`"1,000,000 USD"`, `"$1M"`) stays
`Unreadable` and reaches a human rather than being coerced into a number we invented.

### What this deliberately does NOT do

- **No new `ComplianceStatus` value.** A distinct "unverifiable" verdict would ripple through the
  frontend badges, dashboard counts, the documents-list filters, the audit export and the plan
  surfaces. The existing states carry the meaning: the *rule* fails (NonCompliant), and the
  *document* is flagged for review (`ExtractionStatus.ManualRequired`).
- **No override of a computed verdict.** `ComputeOutcome`'s status mapping is untouched. In
  particular an unreadable value is never softened to `Pending` — a failing rule yielding
  NonCompliant is strictly more alarming and more accurate than a non-committal Pending, and
  degrading it would re-hide the liability this ADR exists to surface.
- **No rejection of the edit (400).** The extraction path *cannot* reject — the model already
  produced the value, and dropping it would discard what the certificate says. Both writers therefore
  behave the same way, and the user keeps the ability to record verbatim what a document shows.
- **Non-canonical fields are untouched.** Only the three typed columns have the
  absent-vs-unreadable ambiguity. Free-text fields (`policy_number`, `auto_liability_limit`, …) keep
  whatever the document says; flagging those would put much of the corpus into manual review for no
  compliance gain. `min_value` on them already fails closed.

### Interaction with the existing invariants

- **ADR 0030 (combined unit of work) is preserved.** Both writers set the review flag *before* their
  single `SaveChanges`, so inputs + verdict + review flag still commit as one unit. No new
  transaction, no torn pair.
- **ADR 0017 (last-writer-wins on re-extraction) is preserved.** Clear-on-parse-failure still
  overwrites a stale prior value; the change is that the writer now *knows* it happened.
- **The manual-edit escalation fires only from a settled status** (`Completed` / already
  `ManualRequired`). `ExtractionWorker` claims rows on `ExtractionStatus == Pending`, so overwriting
  Pending would silently **de-queue** the document — trading a bad verdict for a document that never
  gets read at all. `Failed` is left alone as its own louder error state. The extraction path
  re-decides the flag when it lands, so nothing is lost.

## Consequences

### Positive

- The reported repro is closed at its root: an expiration we cannot read can no longer produce a
  Compliant verdict, and the affirmative "Insurance has not expired" sentence disappears with the
  passing check that produced it.
- All three symptoms fall to one fix. The verdict fails closed, the UI sentence follows the check
  row, and the reminder gap is covered by the document surfacing as "Needs your review".
- The check row shows the user the **raw text** plus a note explaining we could not read it — an
  actionable correction path, not a dead end.
- The currency fix removes a *false-NonCompliant* on certificates that genuinely meet their floor,
  and cuts how many documents land in manual review in the first place.

### Negative

- **Documents that previously graded Compliant on an unreadable canonical value now grade
  NonCompliant** and appear in manual review. That is the point — the prior verdict was wrong — but
  it is a visible verdict change on existing rows, appearing the next time each document is
  evaluated (a Check-again, a rule edit, a field edit, or a re-extraction). There is no back-fill
  sweep: `ComplianceSweepBackgroundService` only does date transitions and never re-runs rule
  evaluation, so a stale Compliant persists until something re-grades that document.
- **A noisier `ManualRequired` population.** An org whose model output regularly carries annotated
  dates will see more "Needs your review" documents. Mitigated by the narrowness of the trigger
  (blank never fires it; the currency strip removes the most common false trigger).

### Neutral

- No schema change, no migration, no new status value — so the dashboard counts, the export, the
  plan surfaces and the frontend badges are all untouched.
- `EvaluateRule` gains one cheap guard per rule evaluation: two string comparisons and, only on the
  canonical three with a null column, one parse attempt. Not on a hot path.
- The detail page renders the rule's own `errorMessage` plus the actual value, so the failing
  requirement already reads sensibly without a frontend change. The check's `Notes` field carries the
  precise reason and is available to the UI if a future ticket wants to surface it.

## Alternatives considered

### Option A — Keep the raw-string fallback, fix only `ComputeOutcome`
Make the Expired branch consult the raw string when the column is null. **Rejected**: it cannot
work — the whole reason the column is null is that nothing could turn that text into a date, so there
is no instant to compare. It would also leave the `required` rule still passing, which is what
renders the affirmative "Insurance has not expired".

### Option B — A new `ComplianceStatus.Unverifiable`
Model "we could not read this" as its own verdict. **Rejected** for this ticket as out of scope: a
new status value ripples through frontend badges, dashboard counts, list filters, the audit export
and the plan surfaces. The existing pair (failing rule → NonCompliant; document → `ManualRequired`)
carries the meaning without that blast radius. Revisitable if the review population proves it needs
its own bucket.

### Option C — Reject the manual edit with a 400
Refuse to store a canonical value we cannot parse. **Rejected**: the extraction path cannot reject
(the value already exists and discarding it loses what the certificate says), so the two writers
would diverge; and a user must be able to record verbatim what a document shows. Failing closed and
flagging for review keeps the data and the warning.

### Option D — Drop the field's confidence below the 0.7 gate instead of setting `ManualRequired`
The ticket's own alternative suggestion. **Rejected** as indirect: it would corrupt
`ExtractionConfidence` (a measured quantity the UI uses to tier field borders) in order to move a
status, and it only works on the extraction path — the manual-edit path has no confidence average to
lower, and that is the path the reported repro used.

### Option E — Parse amounts with `en-US` instead of stripping currency symbols
`CultureInfo.GetCultureInfo("en-US")` would accept `$`. **Rejected**: the codebase deliberately pins
`InvariantCulture` on this parse and has a regression test defending it (a `CurrentCulture` refactor
would flip every slash date's month and day — `Slash_format_dates_parse_month_first_under_invariant_culture`).
An explicit edge strip fixes the currency case without touching the culture contract.

## Amendment 1 — the review flag is a property of the DOCUMENT, not of a request (2026-07-22)

From the #383 verified review, which found the escalation of part 3 leaky in two directions at once.
The decision above is unchanged; this records how it is actually enforced.

**The flag must be re-raised from the document's resulting state, not from the submitted field
names.** As first written, `UpdateFields` collected the unreadable fields *of that request* and
`ResolveManualReview` cleared `ManualRequired` unconditionally. So any save that did not happen to
mention the unreadable field resolved the review — and **nothing re-raises it afterwards**
(`ComplianceCheckService` never writes `ExtractionStatus`; only `ExtractionWorker` does), so the flag
was gone until a full re-extraction. Three reachable routes, all in normal use:

- an **empty-fields save** — the detail page deliberately enables Save with no edits precisely when
  `extractionStatus` is `ManualRequired`, and posts an empty array;
- a save touching only an **unrelated field** (the user fixes the policy number);
- **`PUT /api/documents/{id}/verify`** (`MarkVerified`), the second `ResolveManualReview` caller,
  which the escalation never covered at all.

Under a checklist carrying no rule on the field — the exact configuration part 3 exists to cover —
the verdict stays Compliant, the column stays null, the reminder windows stay silent, and after two
clicks nothing flags the document. That is the original #383 state, restored.

The re-raise therefore moved **inside `ResolveManualReview`**, so every caller inherits it, and asks
`DocumentFieldReadability.HasUnreadableCanonicalValue(doc)` of the document's own resulting state. The
settled-status guard survives verbatim, now captured **before** the resolve so `Completed`/
`ManualRequired` is still measured against the incoming status; `Pending`, `Processing` and `Failed`
are each pinned by a `[Theory]` case so a loosened `!= Pending` cannot pass. `UpdateFields` no longer
carries its own copy of the rule.

**A JSON `null` is an absence, on both sides.** `FieldUpdateRequest.FieldValue` is `string?`, so
`PUT /fields` with `fieldValue: null` stores a JSON null in `ExtractionFields`. `RawFieldValue`'s
`GetRawText()` fallback returned the literal 4-character string `"null"` for it, which `IsUnreadable`
then reported as unreadable — while `ApplyToTypedColumn` classified the very same edit `Blank`. One
value, two contradictory readings: the ambiguity class this ADR exists to remove, and a direct
violation of part 1's "Blank stays Blank". The user-visible result was a check row noting we could
not read the value with `ActualValue` `"null"`, rendered verbatim, and a document dragged into manual
review over a value it does not have — sticky, since the stored JSON null was re-read on every later
evaluation. `RawFieldValue` now maps `JsonValueKind.Null`/`Undefined` to null ahead of the fallback
arm. This also closes a pre-existing fail-open on **non-canonical** fields, where `required` was
satisfied by that same 4-character string.

**Last-value-wins on the extraction path.** `PersistSuccess` accumulated its unreadable set on *any*
occurrence of a duplicated field name, while the JSON mirror, the typed columns and the sibling
writer are all last-wins — so a model emitting a canonical field twice (unreadable, then readable)
parsed its column correctly and was still routed to review over a value the document no longer holds.
Now de-duped by name, last value wins. Fail-safe direction, so this was a consistency fix rather than
a defect.

## References

- Tickets: [#383](https://github.com/neboxdev/complidrop/issues/383), [#48](https://github.com/neboxdev/complidrop/issues/48)
- ADRs: [0017](0017-manual-field-edits-sync-compliance-inputs.md) (both writers share one parse),
  [0030](0030-compliance-verdict-combined-unit-of-work.md) (inputs + verdict in one unit of work),
  [0027](0027-compliance-date-window-boundaries.md) (the date windows that read the typed column),
  [0025](0025-reminder-catch-up-window-and-failed-send-retry.md) (the reminder windows that read it too)
- Code: `Services/CanonicalDocumentFields.cs` (`TypedColumnResult`, `IsUnreadable`, `TryParseAmount`, `All`),
  `Services/DocumentFieldReadability.cs` (`TypedColumnValue`, `RawFieldValue`, `TryGetUnreadableValue`,
  `UnreadableCanonicalFields`, `HasUnreadableCanonicalValue`),
  `Services/ComplianceCheckService.cs` (`EvaluateRule` guard, `LookupValue`),
  `BackgroundServices/ExtractionWorker.cs` (`PersistSuccess`),
  `Endpoints/DocumentEndpoints.cs` (`ResolveManualReview` — shared by `UpdateFields` and `MarkVerified`)
