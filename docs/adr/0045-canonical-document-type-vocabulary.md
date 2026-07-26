# 0045. One canonical document-type vocabulary, coerced at the extraction choke point and validated at the rule write boundary

- **Status:** accepted
- **Date:** 2026-07-25
- **Deciders:** Ruben G. (founder), Claude (implementing #373)

## Context

`Document.DocumentType` looks like a display label. It is not. It is compared with **ordinal string
equality** on two compliance-critical paths:

1. **`ComplianceCheckService.ComputeOutcome`'s applicable-rules filter** — `r.DocumentType ==
   doc.DocumentType`. A document stored as `"COI"` matches zero `"coi"` rules, so the checklist yields
   **zero applicable rules** and the document is **never graded against anything**, with no error anywhere.
2. **`DocumentSupersession`** (ADR 0033), which groups on `(VendorId, DocumentType)`. A renewal stored as
   `"COI"` forms its own group and never supersedes the `"coi"` cert it replaces — so the old expired copy
   keeps inflating the Expired liability and keeps drawing reminders at a vendor who already renewed.

**Never-graded is not a fail-safe silence — it is an affirmative-coverage overclaim.** Trace the four
links:

- `ComplianceCheckService.ComputeOutcome`'s zero-applicable-rules branch stores `expiringSoon ?
  ComplianceStatus.ExpiringSoon : ComplianceStatus.Pending`. A never-graded document whose expiry is
  inside the 30-day window is stored **`ExpiringSoon`**, not `Pending`.
- `ComplianceStatusDeriver.Effective`'s read overlay promotes even a stored `Pending`: `expiry <=
  todayDate.AddDays(ExpiringSoonWindowDays) && stored is Compliant or ExpiringSoon or Pending ?
  ExpiringSoon : stored`.
- `VendorEndpoints.ComputeCoverage` counts `Compliant or ExpiringSoon` as **in-force coverage** (its
  `inForce` filter) and so returns `new VendorCoverage("Covered", [], coveredThrough)`.
- `ExportService`'s vendor-package line prints `— Certificate of Insurance — expires {date} — Expiring
  soon` into the **auditor-facing PDF** — while the document-detail "What we checked" panel is EMPTY for
  that same document, because no rule ever ran.

So the product tells the customer, the vendor rollup and the auditor that a document is covered when
nothing has been checked. That raises the priority of the un-laundered legacy population below: the reason
not to launder is that it is a destructive data operation needing human sign-off, **not** that the residue
is harmless. Hardening the read surfaces so a never-graded document cannot read as coverage is
[#443](https://github.com/neboxdev/complidrop/issues/443) — out of scope here; this ADR's job is to stop
NEW documents from joining that population.

Nothing guaranteed the value was in any particular vocabulary. `ExtractionWorker.PersistSuccess` assigned
the model's `documentType` **verbatim** into the column:

- Only the **Gemini** `responseSchema` pinned an `enum`; the **Anthropic** tool schema left `documentType`
  a free string, so that provider could answer `"COI"` or `"Certificate of Insurance"` against a prompt
  asking for `"coi"`. A schema is in any case the provider's promise, not ours — an off-spec response, a
  provider bug, or a future client would write whatever it liked.
- The column is `varchar(100)`, so a runaway value threw Postgres **22001** out of `PersistSuccess`'s single
  `SaveChanges` — which the worker counts as a failure and **retries**, re-paying Document AI + LLM cost on
  every doomed attempt.

The same untrusted response also carries the sibling `DocumentSubType` (`varchar(100)`) and every
`DocumentField` row's `FieldName` / `FieldValue` / `FieldType` (`varchar(200)` / `varchar(2000)` /
`varchar(50)`), written verbatim in the same unit of work. `description_of_operations` — which the prompt
explicitly asks for — is routinely long on an ACORD 25 with an ACORD 101 continuation, so this is an
ordinary certificate, not an adversarial one. That failure does **not** degrade gracefully:
`ProcessDocumentAsync`'s catch calls `SaveChangesAsync` on the SAME context, which still tracks the poisoned
inserts, so the bookkeeping write throws too — `FailedAttempts` never increments and the document is
zombie-reclaimed every 5 minutes until `ProcessingAttempts` exceeds `MaxClaims`. Reachable from the
unauthenticated portal upload route.

Finally, the comparison has **two** operands, and the rule side was equally unguarded: `UpsertRule` stored
`req.DocumentType` verbatim with no vocabulary check.

## Decision

**One vocabulary, `Services/CanonicalDocumentTypes.cs`** — `coi`, `license`, `permit`, `certification`,
`contract`, `other` — the companion to `CanonicalDocumentFields` (which does the same job for extracted
FIELD names). Every in-repo mirror is pinned equal to it by `CanonicalDocumentTypeTests`.

**1. Coerced at the single `PersistSuccess` choke point.** The model's answer goes through
`CanonicalDocumentTypes.NormalizeExtracted` before it overwrites the stored type. Because `Normalize` only
ever returns a **member of the vocabulary** (the `HashSet.TryGetValue` hands back the STORED literal, never
the caller's string), the `varchar(100)` column is length-safe **by construction** rather than by a clamp —
pinned against the EF model by a test.

**2. A blank or ABSENT answer keeps the STORED type; it does not become `"other"`.** `documentType` is
`required` in both providers' structured-output schemas, so a non-answer is off-spec and carries **no
information**, while the stored type is normally the uploader's own pick from the document-type dropdown
(and is passed to the model as its type hint). Demoting a deliberate `license` to `other` on the strength of
a protocol violation would drop every license rule from the checklist and strand the document in the
zero-applicable-rules never-graded state (which reads as coverage inside the 30-day window — see
Context) — recreating the exact failure this decision closes. The fallback is itself
normalized, so a non-canonical stored value is laundered on the way through rather than preserved.

For that branch to be reachable, **both clients' `MapResult` map a missing or JSON-null `documentType` to
`null`**, not to the literal `"other"`. The shape a provider actually produces when it violates `required`
is an **omitted property**, not an empty string; coercing it in the client forged a positive classification
the model never made, and left the blank branch firing only for a shape no provider emits. A model that
POSITIVELY answers `"other"` still arrives as `"other"` and still overwrites — that is a real answer.

**3. An unknown answer becomes `"other"`.** `other` is not a new state: it is already what the prompt tells
the model to emit when the type is unclear, what the column defaults to, and what the UI labels an
unclassified document.

**4. An over-length `DocumentSubType` is DROPPED (null); over-length field/error strings are TRUNCATED.**
The sub-type has no vocabulary — it is free text the model coins per document — so there is nothing to
coerce to; an over-length value is off-spec noise, not a sub-type, and no obligation could match a truncated
half-value anyway. The `DocumentField` columns and `Document.ProcessingError` are the opposite case: an
extracted field is **user-facing content** shown on the detail page, so a clipped
`description_of_operations` beats a vanished one. Truncation is surrogate-safe: the cut backs off
whenever the character at `maxLength - 1` is a high surrogate — **unconditionally**, not only when the
next character is its low half, so an input already carrying an unpaired high surrogate there cannot be
clipped into the very 22021 the guard exists to remove. (Same shape as the `Services/ColumnClamp.To` that
#372 adds, so the two collapse to one body when it lands.)

**AS EXTRACTED, the verdict path is unaffected** — `ExtractionFields` (jsonb, no width) and the typed
columns are both written from the FULL value in the same `PersistSuccess`, so grading reads exactly what
the model returned even when the `DocumentField` row is clipped (pinned by a JSON-mirror test and by a
verdict-level test whose `contains` match lands past character 2000). That guarantee stops at the first
**manual edit**: the detail page binds its editable input to the clamped `DocumentField.FieldValue` and
`DocumentEndpoints.UpdateFields` writes the submitted text back into `ExtractionFields`, so saving an
untouched clipped field narrows the canonical value — and `description_of_operations` IS a verdict input
(the additional-insured `contains` fallback). Marking a clipped field in the UI so a user cannot silently
save the truncation is [#444](https://github.com/neboxdev/complidrop/issues/444).

Every width is pinned against the EF model.

**5. The rule write boundary VALIDATES rather than coerces.** `ComplianceEndpoints.UpsertRule` rejects a
`documentType` outside the vocabulary with `400 validation.document_type` and stores the `Normalize`d
spelling. The asymmetry with the worker is deliberate: a background worker parsing a model response has no
one to ask, but a human editing a checklist does — and silently retyping a compliance RULE would change
**what it governs**, quietly switching a requirement off. Recognized-but-mis-cased input is still folded:
that changes the spelling, not the meaning. `/api/compliance` is reachable without the rules page, so the
API is the authoritative guard, not the UI's type picker.

**6. Normalization never touches the forensic trail.** `ExtractionRawJson` still records what the provider
actually said, so a provider that went off-spec stays diagnosable. Pinned by a test.

### What this deliberately does NOT do

- **It does not launder rows already in the database.** See "Known limitation" below.
- **It does not touch the ingress upload paths.** `DocumentEndpoints` / `VendorPortalEndpoints` are owned by
  [#389](https://github.com/neboxdev/complidrop/issues/389), which adds the upload-path allow-list and
  length caps. `DocumentEndpoints`' private `AllowedDocumentTypes` literal therefore SURVIVES here, made
  `internal` and pinned EQUAL to the vocabulary by a compile-checked test rather than deleted; #389 should
  collapse it. **Drift between the two is a real finding; the duplication itself is not.**
- **It does not delete `ComputeOutcome`'s blank-`DocumentType` wildcard arm.** `applicableRules` still
  treats `string.IsNullOrEmpty(r.DocumentType)` as "applies to every document type", so the two layers now
  narrow at different points: for any rule created or edited from here on, a blank type is **impossible**
  (`UpsertRule` 400s), while a rule row that predates this decision keeps its wildcard meaning. That
  asymmetry is deliberate — deleting the arm would silently change grading for existing blank-type rules,
  a live-data behaviour change, not a code fix. A legacy blank-type rule must be **re-typed before it can
  be saved again**, and the arm carries a comment saying it exists only for those rows.
- **It does not touch the read surfaces that make a never-graded document read as coverage.** See
  Context; that is [#443](https://github.com/neboxdev/complidrop/issues/443).
- **It adds no new `ComplianceStatus` value, no schema change and no migration.**

## Consequences

### Positive
- The stored type can no longer be a value that grades against zero rules, and a renewal can no longer form
  its own supersession group because the model shouted its answer.
- The `varchar(100)` 22001 on the type is gone by construction, and the three sibling untrusted strings in
  the same `SaveChanges` no longer burn ~15 paid OCR + LLM re-runs on an ordinary long ACORD 101.
- Both operands of the compliance-critical comparison now speak one vocabulary.

### Negative
- **A provider whose classification we would previously have kept verbatim is now flattened to `other`.**
  If the vocabulary ever needs a seventh type, adding the literal is not enough — the prompt block, both
  schemas, the endpoint allow-list, `DisplayLabels` and the frontend list all have to move together. Five of
  those six are pinned by tests, so only the frontend can drift silently.
- **`UpsertRule` now 400s on input it used to accept.** Any API client posting a non-canonical type gets an
  error where it previously got a silently-useless rule. That is the intended trade.

### Neutral
- No migration, no new status value, no behaviour change for the overwhelmingly common case (a provider that
  answers a canonical type, which both schemas already constrain it to).

### Known limitation — legacy rows are NOT laundered

Normalization happens only the next time a document is **extracted**, and nothing re-extracts an
already-processed row. So a row written before this deploy with a non-canonical `DocumentType` is never
graded, stays out of its supersession group, and is invisible to the documents list's exact-match
`?type=coi` filter — **forever**, unless a human re-types it via the PATCH endpoint or triggers a
re-extraction. #389 (the ingress fix) does not touch already-stored rows either.

Per Context, that residual population is **not harmless**: any of those rows expiring inside 30 days reads
"Expiring soon", rolls its vendor up to "Covered", and prints as covered in the auditor package, with an
empty "What we checked" panel behind it. So this limitation is **not** justified by "it fails safe anyway"
— it does not.

It is left undone for one reason only: **laundering the rows is a destructive data operation that needs
human sign-off.** It means an ad-hoc `UPDATE` over production `Documents` rows with no dry-run and no
per-row review, against a population whose size is currently unknown (the upload path has always
lower-cased its own writes; the exposure is API clients and any pre-existing import), and the autonomy
contract stops a session at exactly that boundary. The right sequence is to MEASURE first — a read-only
query for `DocumentType NOT IN (vocabulary)` — and then decide with a human in the loop, rather than to
bundle a blind data migration into a code fix.

The overclaim itself does not depend on the laundering happening: **#443 fixes it at the read surfaces**
(a document with zero applicable rules must not read as in-force coverage), which covers every ungraded
row regardless of how its type got there. Laundering is the follow-on cleanup, not the mitigation.

### Known limitation — one unpinned mirror

`frontend/src/lib/document-types.ts` carries the same list and **cannot** be pinned by a .NET test (no
shared fixture, unlike the contact-email corpus of ADR 0038). It is named explicitly in
`.claude/reviewers.md`, in `CanonicalDocumentTypeTests`' summary and here, so "ONE list" is never read as
"no other copies exist". The five in-repo mirrors that a .NET test CAN reach are all pinned: both provider
schemas, the extraction prompt's DOCUMENT TYPES block, `DocumentEndpoints.AllowedDocumentTypes`, and
`DisplayLabels.DocumentTypes`.

There is a **sixth** in-repo document-type set, and it is deliberately **not a mirror**:
`RuleEngine/RuleSetLoader`'s private `DocumentTypes` (`coi | license | certification | other`), which
validates an obligation's `documentType` against the RD-c schema. A .NET test could reach it, so it is
named here rather than left for a future author to trip over: it is a **SUBSET on purpose** (the
obligation schema accepts fewer types than the product stores) and **must not be pinned equal to `All`**.
Concretely, the "add a seventh type" checklist has to ask a question about it — *should the obligation
schema accept this type?* — and record the answer, instead of mechanically appending the literal.

## Alternatives considered

### Option A — Demote a blank/absent answer to `"other"`
Treat "the provider said nothing" as "the provider said other" — simpler, one code path. **Rejected**: it
throws away the uploader's own classification on the strength of a protocol violation, and lands the
document at zero applicable rules. It also inverts the failure direction: coercion is meant to make the
value *more* usable, not to discard the most reliable signal we have.

### Option B — Truncate the over-length sub-type instead of dropping it
Symmetry with the field columns. **Rejected**: unlike a field value, a sub-type is matchable metadata, not
content shown to the user; a truncated half-value could never match any obligation, so storing it invents a
value that is neither what the model said nor useful. (The field columns go the other way for exactly the
mirror-image reason — they ARE content.)

### Option C — Validate at the ingress upload paths now
Reject a non-canonical type at upload rather than coerce at extraction. **Rejected as scope**, not as
wrong — it is the right complement and it is [#389](https://github.com/neboxdev/complidrop/issues/389).
Ingress validation alone would not help anyway: the model's answer OVERWRITES the uploaded type, so the
extraction choke point has to be guarded regardless.

### Option D — Launder legacy rows with a data migration
An `UPDATE Documents SET "DocumentType" = <normalized>` over existing rows. **Rejected for now** — see
"Known limitation" above. Not because the residue is harmless (it is not: an ungraded row inside the
30-day window reads as in-force coverage on the vendor rollup and in the auditor package), but because
laundering is a **destructive, unreviewable data operation on production rows** whose target population
has not been measured — the class of change that needs human sign-off before it runs. The ADR records the
gap so it is a known open item rather than an invisible one, and the overclaim itself is closed at the
read surfaces by [#443](https://github.com/neboxdev/complidrop/issues/443), which does not depend on the
laundering happening at all.

### Option E — Delete `DocumentEndpoints.AllowedDocumentTypes` and reuse the vocabulary directly
The desired end state. **Deferred**: those endpoint files are owned by the in-flight #389 branch and editing
them here is a near-certain conflict. The duplication is made safe in the meantime by an equality test that
is now a COMPILE-checked reference (the field is `internal`), so a rename is a build error rather than a
runtime failure whose message invites deleting the guard.

## References

- Tickets: [#373](https://github.com/neboxdev/complidrop/issues/373), [#389](https://github.com/neboxdev/complidrop/issues/389) (the ingress half), [#385](https://github.com/neboxdev/complidrop/issues/385) (partially addressed by the column clamps), [#443](https://github.com/neboxdev/complidrop/issues/443) (a never-graded document must not read as in-force coverage), [#444](https://github.com/neboxdev/complidrop/issues/444) (surface a clipped field so a manual save can't narrow it), [#48](https://github.com/neboxdev/complidrop/issues/48) (rolling bug-fix epic)
- ADRs: [0033](0033-document-supersession-expired-liability.md) (the `(VendorId, DocumentType)` supersession key this protects), [0030](0030-compliance-verdict-combined-unit-of-work.md) (the single unit of work the coercion stays inside), [0040](0040-unreadable-canonical-value-fails-closed.md) (the sibling "absent and unreadable are different facts" decision for FIELD values), [0038](0038-vendor-contact-email-mirrored-validation.md) (the shared-fixture pattern the frontend mirror would need)
- Code: `Services/CanonicalDocumentTypes.cs` (the vocabulary), `BackgroundServices/ExtractionWorker.cs` (`PersistSuccess`, `Clamp`, the width constants), `Services/Extraction/{Gemini,Anthropic}ExtractionClient.cs` (`MapResult`, both schemas), `Endpoints/ComplianceEndpoints.cs` (`UpsertRule`), `Endpoints/DocumentEndpoints.cs` (`AllowedDocumentTypes`), `Services/DisplayLabels.cs`, `frontend/src/lib/document-types.ts` (unpinned mirror)
