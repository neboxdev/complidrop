# 0017. Manual field edits sync the canonical compliance inputs; re-extraction overwrites manual edits

- **Status:** accepted
- **Date:** 2026-06-05
- **Deciders:** Ruben G.

## Context

A document carries the extracted values in **two** places that serve different readers:

1. **`DocumentField` rows** — one row per field (`FieldName`, `FieldValue`, `Confidence`,
   `IsManuallyEdited`, `OriginalValue`). This is the human-facing, per-field, provenance-tracked
   record the detail page renders and lets the user edit.
2. **`Document.ExtractionFields` (a `jsonb` dict) + three typed columns** (`GeneralLiabilityLimit`,
   `EffectiveDate`, `ExpirationDate`). This is what `ComplianceCheckService.LookupValue` reads when
   it evaluates a rule — it never reads `DocumentField` rows.

Both are written together, once, by `ExtractionWorker.PersistSuccess`.

`PUT /api/documents/{id}/fields` (`UpdateFields`) — the "correct a misread value" path behind the
detail page's edit UI — only wrote (1). So when a user opened a *Needs your review* document, fixed
a value the OCR got wrong (e.g. a general-liability limit misread as `$500,000` when the policy says
`$1,500,000`), and clicked Save, the `DocumentField` row updated but the typed column / JSON did not.
Compliance had nothing new to read, so re-running it recomputed the **identical** verdict — the
document stayed `NonCompliant`. The only way to refresh the inputs was a full re-extraction, which
re-introduces the same OCR error.

This was invisible until [#193](https://github.com/neboxdev/complidrop/issues/193) added the *"Why
isn't this compliant?"* explainer, which now renders the stale failure (with the misread `$500,000`)
directly next to the field the user just corrected. Tracked as
[#216](https://github.com/neboxdev/complidrop/issues/216). #193's review added — then reverted — an
inline re-evaluation, because re-evaluating *without syncing the inputs first* changes nothing; the
revert comment in `UpdateFields` explicitly deferred the real fix (this ADR) to its own ticket
because it is a **data-semantics** decision, not a drive-by change: it mixes manual edits into the
"what the extractor produced" record, and it interacts with re-extraction overwriting those edits.

## Decision

**1. A manual field edit is mirrored into the canonical compliance inputs, then compliance
re-evaluates.** In `UpdateFields`, after applying each edit to its `DocumentField` row, the same
value is written into `Document.ExtractionFields` (every edited field) and, for the three
date/amount fields, into the typed column (`CanonicalDocumentFields.ApplyToTypedColumn`). The JSON
mirror is built from the *existing* `ExtractionFields` object so untouched keys keep their original
value and JSON type; only edited keys are overwritten (as strings — a form value is inherently a
string). The endpoint then calls `IComplianceCheckService.EvaluateAsync` **best-effort** (try/catch,
log-and-continue), exactly as `UpdateDocument` does after a vendor/type change: a recompute failure
must not fail the save the user just made.

**2. Both write paths share one parse helper.** `CanonicalDocumentFields.ApplyToTypedColumn` is the
single place that maps a field name + string value onto the three typed columns. Both
`ExtractionWorker.PersistSuccess` and `UpdateFields` call it, so the extraction pipeline and a manual
correction parse dates and amounts **identically** (UTC dates via
`AssumeUniversal | AdjustToUniversal`; amounts via `NumberStyles.Any` + `InvariantCulture`). A value
that fails to parse **clears the column to null** rather than leaving a stale value that would
contradict the field the user can see — `LookupValue` then falls back to the raw string now sitting
in `ExtractionFields`. (For the extraction path the columns start null, so clear-on-failure and the
old leave-unchanged behavior coincide; this is a behavior-preserving refactor there.)

**3. Re-extraction wins over manual edits.** `POST /api/documents/{id}/reextract` re-queues the
document; `PersistSuccess` then **overwrites** `ExtractionFields`, the typed columns, and *all*
`DocumentField` rows (it already `RemoveRange`s them and re-adds with `IsManuallyEdited = false`).
A manual edit is a correction to *one specific extraction result*; re-extraction is an explicit
"read the source document again from scratch" action that produces a *new* result. Trying to replay
manual edits onto a fresh extraction (which fields? what if a field no longer exists, or the new
read is higher-confidence?) is ambiguous and out of scope. So the precedence is: **last write wins,
and re-extraction is a deliberate last write that resets everything, manual edits included.** This is
the pre-existing behavior for `DocumentField` rows; this ADR makes it the documented contract for the
typed columns and JSON too.

## Consequences

### Positive

- A correction now does what the user expects: fixing a misread GL limit above the required minimum
  flips `NonCompliant → Compliant`, and the detail-page explainer reflects it on the next load — the
  exact gap #216 was filed for, closed where compliance actually reads its inputs.
- One shared parse helper removes the latent risk of the two write paths drifting (e.g. the manual
  path accepting a date format the pipeline rejects, so a "saved" correction silently never matched a
  rule).
- No schema change, no migration: `ExtractionFields` and the typed columns already exist; only the
  write path widened.

### Negative

- `ExtractionFields` is no longer strictly "what the extractor produced" — it now also holds manual
  overrides. Provenance for *which* values were hand-edited still lives on the `DocumentField` rows
  (`IsManuallyEdited` / `OriginalValue`); the JSON dict deliberately carries no such marker (it is the
  evaluation input, not the provenance record). Anyone reasoning about extractor accuracy must read
  the `DocumentField` flags, not infer it from the JSON.
- A user who re-extracts *after* hand-correcting loses the corrections (by design, item 3). The UI
  copy around the re-extract action should make "this re-reads the file and discards manual edits"
  clear; the audit log preserves the prior values via the `document.fields_edited` / interceptor rows
  regardless.

### Neutral

- The edit path now emits an extra audit row (the compliance recompute's `SaveChanges`) on top of the
  field-diff `document.fields_edited` row — same multi-row shape `UpdateDocument` already produces and
  consistent with the audit model in CLAUDE.md.
- Manually editing one of the three typed fields to a value the canonical parse can't read
  (e.g. `"approximately $1M"`) nulls the typed column and leaves the raw string in the JSON; a
  `min_value` rule then reports "unable to parse" rather than a stale pass/fail. This is the correct,
  visible-to-the-user outcome and matches what the field now shows.

## Alternatives considered

### Option A — re-key compliance off the `DocumentField` rows instead of `ExtractionFields`

Make `LookupValue` read the per-field rows so the manual edit is read directly with no mirroring.
Rejected: it would re-plumb every read path (and the extraction pipeline writes the JSON + typed
columns first and foremost), turn the three typed-column fast paths into row lookups, and still need
a date/amount parse step somewhere — the mirroring approach reuses the columns the evaluator already
reads and keeps the hot path on typed columns.

### Option B — preserve manual edits across re-extraction

Replay `IsManuallyEdited` rows onto each fresh extraction. Rejected for MVP: ambiguous merge
semantics (field renames, disappearing fields, confidence conflicts) for a rare flow, and it
contradicts the user's intent when they explicitly ask to re-read the source. Revisit only if users
report losing corrections to accidental re-extracts.

### Option C — leave it documented-but-unfixed

The shape #193's revert left in place. Rejected: the acceptance criterion is *"correcting a field
that feeds a rule updates the verdict."* A documented no-op does not satisfy it, and #193's explainer
now puts the stale verdict in the user's face.

## Test coverage

- `CanonicalDocumentFieldsTests` (pure unit) — pins the shared parse: a valid GL amount / date lands
  in the typed column; grouped thousands (`1,500,000`) parse; an unparseable value clears the column
  to null; a non-canonical field name is a no-op.
- `DocumentEndpointsTests`:
  - `Editing_general_liability_limit_above_the_minimum_flips_noncompliant_to_compliant` — the marquee
    regression (AC #1 / #4): seed a `NonCompliant` COI, `PUT` the corrected limit, assert the stored
    `ComplianceStatus` is `Compliant`.
  - `Editing_a_field_refreshes_the_compliance_checks_on_the_detail_payload` — AC #2: after the save,
    `GET /api/documents/{id}` reports the passing check / updated verdict the explainer renders.
  - `Editing_a_json_field_that_feeds_a_required_rule_updates_the_verdict` — proves the JSON mirror
    (not just the typed columns) reaches compliance for a non-typed field.
  - `Editing_a_correct_value_down_below_the_minimum_flips_compliant_to_noncompliant` — symmetry: the
    sync moves the verdict in both directions.

## References

- Ticket: [#216](https://github.com/neboxdev/complidrop/issues/216) (latent bug surfaced by
  [#193](https://github.com/neboxdev/complidrop/issues/193); rolling bug epic
  [#48](https://github.com/neboxdev/complidrop/issues/48))
- Code: `api/CompliDrop.Api/Services/CanonicalDocumentFields.cs` (shared parse),
  `api/CompliDrop.Api/Endpoints/DocumentEndpoints.cs` (`UpdateFields`),
  `api/CompliDrop.Api/BackgroundServices/ExtractionWorker.cs` (`PersistSuccess`),
  `api/CompliDrop.Api/Services/ComplianceCheckService.cs` (`LookupValue`)
