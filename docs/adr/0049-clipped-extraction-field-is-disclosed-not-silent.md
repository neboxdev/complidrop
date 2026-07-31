# 0049. A clipped extraction field is DISCLOSED at read time, derived from the two copies the system already keeps

- **Status:** accepted
- **Date:** 2026-07-31
- **Deciders:** Ruben G. (founder), Claude (implementing #444)

## Context

[ADR 0045](0045-canonical-document-type-vocabulary.md) §4 decided that an over-length extracted field
value is **truncated, not dropped**: `DocumentField.FieldValue` is `varchar(2000)`, Npgsql does not
truncate, and a 22001 out of `ExtractionWorker.PersistSuccess` is not a graceful failure but ~15
re-paid Document AI + LLM runs. An extracted field is user-facing content, so a clipped
`description_of_operations` beats a vanished one. **That decision is correct and this ADR does not
touch it.** What it lacked was a marker.

The clip is invisible on three axes at once, and each one on its own would be survivable:

1. **The detail page shows the clipped value as if it were whole.**
   `frontend/src/app/(dashboard)/documents/[id]/page.tsx` binds its editable input to
   `edits[f.fieldName] ?? f.fieldValue ?? ""`, under the comment *"the value on screen must be exactly
   the value a Save would send"* — which is right for every other field and precisely the problem here.
   The clip is also invisible to the two markers the page already has: the amber outline keys on
   **confidence**, and a clipped value is read with high confidence (the model was sure; the column was
   short), so nothing is outlined. There is no ellipsis, no character count, nothing.

2. **Saving that field makes the clip canonical.** `DocumentEndpoints.UpdateFields` does
   `fields[update.FieldName] = update.FieldValue;` → `doc.ExtractionFields = JsonDocument.Parse(...)`.
   `ExtractionFields` (jsonb, no width) is the **canonical compliance input** — every rule read goes
   through `ComplianceCheckService.LookupValue` → `DocumentFieldReadability.RawFieldValue`, which reads
   the jsonb, never the `DocumentField` rows. So PUTting back exactly what the page displayed replaces
   the full extracted record with the clipped copy.

3. **`description_of_operations` is a verdict input.** `ComplianceCheckService.EvaluateRule`'s
   additional-insured fallback does `operations?.Contains(rule.ExpectedValue, …)`, and an ACORD 25 with
   an ACORD 101 continuation routinely carries the additional-insured wording past character 2000 —
   an ordinary certificate, not an adversarial one.

ADR 0045 §4 already stated the guarantee and its boundary: *"AS EXTRACTED, the verdict path is
unaffected — `ExtractionFields` and the typed columns are both written from the FULL value … That
guarantee stops at the first manual edit."* The reproduction is the whole ticket: upload a COI whose
description exceeds 2000 characters with the venue name near the end, watch the additional-insured
check pass (the jsonb holds the full text), edit one character in that field, save — the check flips
to failed, because the canonical value is now the clipped copy.

The verdict effect is **fail-CLOSED** — removing text can only turn a `contains` pass into a fail,
never the reverse — which is why this is a disclosure defect rather than a safety hole, and why the
answer is a marker rather than a behaviour change.

## Decision

**1. The DTO carries the clip; the client does NOT re-derive it.** `DocumentFieldDto` gains
`FieldValueTruncated` (`fieldValueTruncated` over JSON), populated in
`DocumentEndpoints.GetDocument`. This is the same shape and the same reasoning as
`DocumentDetail.UnreadableFields` ([ADR 0040](0040-unreadable-canonical-value-fails-closed.md)
Amendment 2): the browser is technically sent both copies (the clamped `fieldValue` and the parsed
`extractionFields` object), so a client-side re-derivation is *possible* — and wrong. Answering "is
this row the clamp of that value?" means reproducing the .NET column width **and** `ColumnClamp.To`'s
surrogate back-off, and a TypeScript copy of that drifts from the clamp it mirrors. Sourcing the
answer from the backend walk is the point, not incidental.

**2. Derived at READ time from the two copies the system already keeps — no column, no migration.**
`Services/DocumentFieldTruncation.ValueWasClipped(doc, field)` compares `Document.ExtractionFields`
(the full value) against `DocumentField.FieldValue` (the clamped one). Both inputs are already loaded
by `GetDocument` (`ExtractionFields` is a column on the document; `Fields` is `Include`-loaded), so the
flag costs no extra query.

Deriving beats persisting on the property that matters most here: it **self-clears correctly**. Once
the user saves the field, `UpdateFields` writes the submitted text into BOTH copies, they agree, and
the flag goes false — because the record now genuinely holds what is shown. A persisted flag written
at extraction time would keep asserting a fuller value that no longer exists, and would need its own
clearing logic in every writer.

**3. It reproduces the clamp rather than testing for difference.** The predicate is
`field.FieldValue == ColumnClamp.To(full, InputLengths.DocumentFieldValue)` where `full` is the jsonb
value and `full.Length > InputLengths.DocumentFieldValue` — the same helper `ExtractionWorker.Clamp`
delegates to, at the same constant. A bare "they differ" test would fire on legitimate divergence; a
length test alone would miss that the surrogate back-off makes a clipped value 1999 characters long.
The two shapes that legitimately differ WITHOUT being a clip both read false:

- **A JSON `null`** — an ABSENCE on both sides. `DocumentFieldReadability.RawFieldValue` maps
  `JsonValueKind.Null`/`Undefined` to `null` (ADR 0040), so the reader and the writer agree it is not
  a value. Reusing that one reader is deliberate: a second jsonb reader here would re-open the
  same-value-two-verdicts split ADR 0040 closed.
- **The second row of a duplicate field name** — `PersistSuccess` writes one `DocumentField` row per
  extracted field but only the LAST value per name into the jsonb mirror, so an earlier row differs
  from the mirror without being a clip of it. It matches the clamp only when its first 2000 characters
  are identical to the later value's — in which case the row IS clipped and the flag is right anyway.

A field NAME longer than `InputLengths.DocumentFieldName` is itself clamped, so its jsonb lookup (keyed
by the full name) misses and the flag reads false. That is the safe direction: no hint, exactly today's
behaviour, rather than a hint pointing at a value we cannot line up.

**4. The hint states BOTH facts, and offers the remedy that actually exists.** Copy:

> **Shown shortened.** This value is longer than we can show here — open **View file** above to read
> all of it. We check requirements against the full text, so editing this field and saving replaces it
> with just what's in the box.

"It's shortened" alone leaves the user believing the record is fine. "Saving replaces it" alone does
not explain why the box is short. Both are required, because the user's decision — whether to touch
this field at all — depends on both.

The remedy is **read the original**, never "retype it". [ADR 0046](0046-request-input-length-guards.md)
REJECTS an over-length correction (`validation.too_long`) — user-TYPED content is never clamped — so
there is no path by which a person restores the full text through this input, and copy implying
otherwise would send them into a 400 loop. Pinned by a test, so the two decisions cannot drift apart.

**5. No amber border.** The amber input outline means *"we couldn't read this"* (ADR 0040) or *"low
confidence"*. A clipped value is read correctly and is high-confidence; it is simply not shown whole.
Reusing the marker would make two different states indistinguishable and would tell the user to
"correct" a value that is right.

### What this deliberately does NOT do

- **It does not change the truncation.** Truncate-not-drop, the 2000-character width, and the
  surrogate back-off are exactly as ADR 0045 §4 states them. This ADR changes **disclosure only**.
- **It does not change any verdict.** The flag is presentational. `ExtractionFields` still holds the
  full value as extracted, grading still reads it, and saving a clipped field still narrows it — the
  user is now told before they do.
- **It does not block or reject the narrowing save.** Rejecting the edit would strand a document whose
  clipped field the user genuinely needs to correct, and the narrowing is fail-closed. Warning is the
  proportionate answer to a fail-closed data loss the user can see coming.
- **It adds no column, no migration and no new status.**

## Consequences

### Positive
- A user can no longer save a truncation believing it is the whole extracted value, on the one field
  where that silently unmakes a compliance match.
- The disclosure self-heals: it appears exactly while the record holds more than the screen shows, and
  disappears the moment that stops being true — including after a re-extraction, which rewrites both
  copies.
- ADR 0045's "Known limitation — a clamped field can be narrowed by a manual edit" is closed at the
  point the user acts, without touching the clamp that limitation describes.

### Negative
- **The flag is a derivation, so it is only as good as the agreement between the two writers.** If a
  future change clamped the jsonb copy too, the two would agree, the flag would go false, and the page
  would stop warning while still showing a clipped value. Pinned in `ExtractionWorkerTests` against a
  document the REAL worker wrote, not a hand-built one, precisely so that change goes red.
- **One more field on a DTO the detail page already reads in full.** The alternative (a persisted
  column) would have been worse on every axis that matters here; this is the accepted cost.

### Neutral
- No behaviour change for the overwhelmingly common case: every field that fits its column reports
  `fieldValueTruncated: false` and renders exactly as before.
- The detail page's PUT sends only the fields the user actually edited (`Object.entries(edits)`), so
  editing an unrelated field does not narrow a clipped one. The warning is therefore scoped to the
  field it is attached to, and says "editing **this field** and saving".

## Alternatives considered

### Option A — Persist a `FieldValueTruncated` column written by `ExtractionWorker.Clamp`
The most literal reading of the ticket ("emit the flag from the same `Clamp` decision"). **Rejected**:
it needs a migration, and — worse — it does not self-clear. `UpdateFields` would have to remember to
reset it, and any writer that forgot would leave a permanent warning asserting a fuller value the
record no longer holds. Deriving from the two copies the system already keeps gets the same answer
from state that cannot go stale.

### Option B — Re-derive the flag in the browser
Both copies are on the wire (`fieldValue` and the parsed `extractionFields`), so the page could compare
them itself. **Rejected** for the ADR 0040 Amendment 2 reason: it means a TypeScript re-implementation
of the .NET column width and `ColumnClamp.To`'s surrogate back-off, and drift there means the UI warns
about the wrong field or none at all. Nothing could pin the two together — there is no shared fixture
across the boundary (the ADR 0038 contact-email corpus is the only such mechanism in the repo, and it
exists precisely because a hand-kept mirror was not trusted).

### Option C — Store the full value in `DocumentField.FieldValue` (widen or drop the column bound)
No clip, no disclosure needed. **Rejected**: the bound is what keeps an ordinary long ACORD 101 from
22001-ing `PersistSuccess` and burning ~15 re-paid OCR + LLM runs (ADR 0045 § Context). Widening to
`text` trades a bounded, understood clip for an unbounded untrusted write on a row rendered per field
on a page — and it would not help the deeper issue, that the input is bound to a display copy at all.

### Option D — Reject the save when it would narrow a clipped field
Make `UpdateFields` 400 on a submission equal to the stored clamp. **Rejected**: it strands a user who
genuinely needs to fix a clipped field (they cannot submit the full text either — ADR 0046 rejects
it), it guesses intent from a value equality that a deliberate edit could also produce, and it answers
a fail-closed loss the user can be told about with a hard block they cannot clear. The same family of
rejection as ADR 0040 § Alternatives' "reject the edit with a 400".

### Option E — Truncate the DISPLAY with an ellipsis and a "show full value" expander
Show the clip visually rather than in prose. **Rejected as insufficient, not as wrong**: an ellipsis
communicates "there is more" but says nothing about what a Save does, which is the half that costs the
user a verdict. It is also a bigger UI change (the field is an `<input>`, not a text block) for a state
that is rare per document. If the field editor is ever rebuilt as a textarea, an expander is the
natural place for this hint to live.

## References

- Tickets: [#444](https://github.com/neboxdev/complidrop/issues/444), [#373](https://github.com/neboxdev/complidrop/issues/373) (the clamp this discloses), [#48](https://github.com/neboxdev/complidrop/issues/48) (rolling bug-fix epic)
- ADRs: [0045](0045-canonical-document-type-vocabulary.md) §4 (truncate-not-drop — unchanged here; its "a clamped field can be narrowed by a manual edit" limitation is what this closes), [0040](0040-unreadable-canonical-value-fails-closed.md) Amendment 2 (the DTO shape mirrored, and `RawFieldValue`'s JSON-null mapping reused), [0046](0046-request-input-length-guards.md) (`UpdateFields` rejects an over-length correction — why the remedy is "read the original"), [0017](0017-manual-field-edits-sync-compliance-inputs.md) (manual edits write the canonical inputs), [0030](0030-compliance-verdict-combined-unit-of-work.md) (the unit of work the narrowing save commits in)
- Code: `Services/DocumentFieldTruncation.cs` (`ValueWasClipped`), `Services/ColumnClamp.cs` (`To` — the clamp reproduced), `Services/InputLengths.cs` (`DocumentFieldValue` — the one width), `DTOs/Documents/DocumentDtos.cs` (`DocumentFieldDto.FieldValueTruncated`), `Endpoints/DocumentEndpoints.cs` (`GetDocument`, `UpdateFields`), `BackgroundServices/ExtractionWorker.cs` (`Clamp`, `PersistSuccess`), `frontend/src/app/(dashboard)/documents/[id]/page.tsx` (the hint)
