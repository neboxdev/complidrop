# 0046. Bound every request string before it reaches a bounded column — reject what the user typed, clamp what a machine chose

- **Status:** accepted
- **Date:** 2026-07-27
- **Deciders:** Ruben G. (founder), Claude (implementing #389)

## Context

[ADR 0044](0044-audit-client-input-clamped-at-the-boundary.md) (#372) fixed one instance of a systemic
problem and named the rest as #389. This is the rest.

**Npgsql does not truncate.** An over-length string written to a bounded `varchar(n)` fails the
**whole** `SaveChanges` with Postgres `22001`, and nothing catches `DbUpdateException` on these paths —
so the caller got a generic **500** where the honest answer is a **400**. `FileValidationService`
checks magic bytes and size but never the filename; the DTOs carry no validation attributes; the only
guard that existed anywhere was `UpdateOrganization`'s hand-rolled `name.Length > 200`.

The **public/anonymous** routes are the priority, because there a third party chooses the input:

- **`POST /api/portal/{token}/upload`** — `OriginalFileName` (`varchar(500)`) went in **raw**:
  `SanitizeFileName`'s 120-char clamp only ever applied to the **blob** name. The 22001 landed inside
  the permit transaction, after the blob had been uploaded, so the vendor lost the upload and got a
  500 they could not retry out of. `documentType` (`varchar(100)`, no allow-list) was stored verbatim
  from a public form field — a second, quieter failure: any non-canonical value writes a type no
  compliance rule can ordinally match, so the document grades against **nothing** while still reading
  as coverage ([ADR 0045](0045-canonical-document-type-vocabulary.md)). #373's oversize-`documentType`
  500 is a subset of this item.
- **`POST /api/auth/register`** — `CompanyName` (200), `Industry` (100), `CompanySize` (**20**) and
  `FullName` (200), all written straight from the body. `CompanySize` at 20 is the narrowest column in
  the app fed by request input.
- **`POST /api/waitlist`** — email checked only for `@` with no 256 cap, plus `CompanyName` /
  `Industry` / `Source`. Plus a **check-then-insert TOCTOU**: two concurrent submissions of the same
  address both saw "not on the list" and the loser hit the `(Email)` unique index — a 500 on a public
  marketing form for a visitor who did nothing wrong.

**One aggravator, dashboard-only.** `DocumentEndpoints.UploadDocument` uploads the blob **before** the
insert and only its `IsKeyConflict` catch deleted it, so a 22001 (or any other `SaveChanges` failure)
left a paid-for blob with no row pointing at it and nothing that would ever find it. The portal twin
already had the right shape — a `documentPersisted` flag and a `finally` — so the portal does **not**
orphan; the round-2 review's claim that it did was refuted by the ticket's owner.

The **authenticated** set is contract-only (a 500 where a 400 belongs, no data loss): the raw
`Idempotency-Key` header on the upload / sample / checkout endpoints (the portal already bounded it at
128 and the other three did not), the vendor `Name` / `ContactPhone` / `Category` (`ContactEmail` was
already bounded by [ADR 0038](0038-vendor-contact-email-mirrored-validation.md)), the manual field
correction, the reminder subject, and the checklist + rule fields.

## Decision

### 1. One width source, consumed by the EF model

`Services/InputLengths.cs` holds the width of every bounded column an endpoint writes from request
input, and `ModelConfiguration` **consumes** it — `HasMaxLength(InputLengths.X)` — exactly as it
already does for `AuditColumnLengths` (ADR 0044 §4) and `ContactEmail.MaxLength` (ADR 0038). The
column and the edge guard that feeds it agree **by construction**; a widened column cannot silently
leave its guard behind, and a hand-copied number cannot drift.

Only columns fed by request input are listed. Columns written from compile-time literals
(`UploadedBy`), from a server-derived value (`BlobStoragePath`, `Token`), or from a vocabulary that is
length-safe by construction (`DocumentType` via `CanonicalDocumentTypes`, `TimeZone` via `TimeZones`)
are deliberately **absent** — listing them would imply a guard that does not and need not exist.

`ExtractionWorker`'s two `DocumentField` widths become one-line **aliases** of `InputLengths` rather
than a second pair of literals. Two writers hold opposite policies on those same two columns (see §2),
and that only stays coherent while there is exactly one width. Same one-line-delegate shape as
`ExtractionWorker.Clamp` → `ColumnClamp.To`.

### 2. Reject vs clamp is **per-field**, and the axis is *who authored the value*

- **REJECT (400)** what the **user typed** — company name, industry, company size, full name, vendor
  name/phone/category, checklist name/description, the four rule fields, the reminder subject, a
  manual field correction. Silently truncating a person's own words is data loss they never consented
  to and cannot see. On a compliance product it is worse than that: a rule's `ErrorMessage` is the
  owner's plain-English requirement text, printed on the auditor-facing export, so a **truncated
  requirement is a changed requirement**.
- **CLAMP (`ColumnClamp.To`)** what a **machine chose** and the user never reads back — the uploaded
  file NAME, the waitlist `Source` attribution tag, the client-minted `Idempotency-Key`. Refusing a
  vendor's certificate because their phone named the file something long would block the exact job the
  product exists to do. This follows the clamps already in the repo (the portal's 128-char key,
  `SanitizeFileName`'s 120).

The same two columns can legitimately take **both** policies: `ExtractionWorker` **truncates**
`DocumentField.FieldName`/`.FieldValue` (ADR 0045 §4 — it is salvaging a model response nobody typed,
and a clipped `description_of_operations` beats a lost document) while `UpdateFields` **rejects** them
(it is the user's own correction). Two writers, two policies, one width.

`documentType` takes a third form on the ingress paths — **coerce** to the vocabulary, unknown becomes
`other` — because rejecting there would cost the vendor a file over a stray form value, and blank
already meant `other`. `UpdateDocument` (PATCH) and `UpsertRule` still **reject**: a human deliberately
re-typing a document or writing a rule is choosing what gets graded, and answering `other` silently
would change it (the asymmetry of ADR 0045 §5).

### 3. Rejection messages follow the frontend error policy

One code — `validation.too_long` — so a client has one rule, with the actionable detail in
`error.message`, which the frontend renders verbatim (CLAUDE.md § Frontend error-message policy).
`InputLength.TooLongMessage` produces `"{Label} must be {n} characters or fewer."`, which is
byte-identical to the tone `UpdateOrganization` already set. Never HTTP jargon, never a column-width
dump. "N or fewer", not "under N": exactly N is a legal value, pinned by a boundary test on every
guarded field.

Length is measured in **UTF-16 code units** while `varchar(n)` counts **code points**, so a string of
astral characters is judged conservatively — the guard can refuse a value Postgres would have accepted,
never the reverse. That is the safe direction (a 400 the user can act on, never a 500), and it matches
`ColumnClamp.To`, which cuts by the same measure.

Every guard measures the value the row **actually receives** (post-`Trim()` where the endpoint trims).
Checking the raw property would refuse input the write would have accepted.

### 4. The `Idempotency-Key` clamp must happen where the header is READ

The three authenticated routes clamp at **128** — the bound the portal already used, now the shared
`InputLengths.ClientIdempotencyKey` so all four agree. The clamp is applied at the **single point the
header is read**, before it is used for the lookup *and* before it is used for storage. Clamping only
one of the two would make a repeat of a long key **miss its own record** and duplicate the side effect
— idempotency silently broken rather than loudly failed. This is the `CurrentUserService` shape of
ADR 0044 §1: clamp where the value enters, not at each sink.

The public portal keeps **ignoring** an oversize key instead of clamping it, and that asymmetry is
deliberate. Truncation manufactures collisions between two distinct keys sharing a prefix (ADR 0044 §2
rejected it for the trace id on the same grounds), and on the portal the key is chosen by an untrusted
third party who could force such a collision on purpose — making a second, unrelated upload silently
replay the first. An authenticated caller can only do that to their **own** org, so there the clamp's
upside (a long key still dedupes a double-submit) outweighs it.

### 5. The dashboard blob cleanup becomes unconditional

`UploadDocument` gains the portal's `documentPersisted` + `finally` shape, so **every** failure path
deletes the blob, not just the idempotency-conflict one.

ADR 0029/0032 semantics are preserved exactly, and the reasoning is the load-bearing part:

- `blobName` embeds **this request's own** `Guid`, so a concurrent same-key **loser deletes only its
  own blob** and can never touch the winner's.
- A **sequential replay** returns at the fast-path `TryGetAsync` **before any blob is uploaded**, so a
  committed record still replays the winner's exact response — pointing at the winner's still-present
  blob.
- The flag flips to true only **after** `SaveChangesAsync` returns.

`TryDeleteBlobAsync` keeps swallowing its own failures (logged loud enough for an operator to find the
orphan). That is what lets it sit in a `finally` without masking the real exception on the way out.

### 6. The waitlist duplicate race is caught, not indexed away

The `(Email)` unique index **already exists**, so the fix is to catch its violation — matched on the
**index name**, the `IdempotencyService.IsKeyConflict` shape, so an unrelated unique violation is never
swallowed as a duplicate signup — and return the same friendly `200` the sequential duplicate already
gets. By the time the loser fails, the address genuinely **is** on the list.

**Adding** a unique index was explicitly rejected: migrations auto-apply at startup and fail fast
([ADR 0016](0016-apply-ef-migrations-on-startup.md)), so creating one over a table that might already
hold duplicates would take production down on the next deploy. The index name is now pinned in
`ModelConfiguration` via `HasDatabaseName` — it is EF's own default name, so this needs no migration
and only guards against a future rename.

The waitlist endpoint is **kept and hardened**, not removed. It has no frontend caller today (the
homepage gate was removed), but deleting a public endpoint is a product decision, not an
implementation one.

### 7. The duplicate document-type literal is collapsed

`DocumentEndpoints.AllowedDocumentTypes` — the second copy ADR 0045 § "Option E" deferred to this
ticket — is **deleted**. All three sites in that file (the PATCH type edit and both upload paths) now
call `CanonicalDocumentTypes` directly. The set-equality test that pinned the duplication is retired
with it rather than left comparing the vocabulary to itself; the contract is asserted **over HTTP**
instead, which is strictly stronger than two C# sets being equal.

## Consequences

### Positive

- No request string can 500 an insert on any of these routes; the public ones answer a 400 the caller
  can act on, or clamp and succeed.
- A vendor's upload can no longer be lost to a long filename or a stray `documentType`, and a
  `documentType` from either ingress path can no longer create a never-graded document.
- The dashboard upload leaks no blob on any failure, closing the ticket's one data-loss-adjacent item.
- A public marketing form no longer 500s on a concurrent duplicate signup.
- All four idempotent routes agree on one client-key bound, and a long key still dedupes.
- One vocabulary literal instead of two.

### Negative

- **Input that used to 500 now 400s** — which is the point, but any client that treated the 500 as
  "retry later" will now see a permanent failure. No known client sends over-length values.
- `UpdateOrganization`'s length rejection changes code from `validation.required` to
  `validation.too_long`. Its **message is unchanged**, and the frontend renders the message, so this is
  invisible in the UI. Its blank-name rejection still answers `validation.required`.
- `UpdateTemplate` now rejects a blank/null `name` with a 400 where it previously NRE'd into a 500 —
  behaviourally a new refusal, and it matches `CreateTemplate`, which has always refused one.
- A conservative UTF-16 measure can refuse an all-emoji value at exactly the limit that Postgres would
  have stored. Judged the right direction.

### Neutral

- **No migration and no data migration.** Every width is numerically unchanged and the waitlist index
  name is EF's own default; `dotnet ef migrations has-pending-model-changes` reports no model changes.
  Existing rows are untouched — including the pre-#373 non-canonical `DocumentType` residue, which
  ADR 0045's "Known limitation — legacy rows are NOT laundered" already records as needing a measured,
  human-signed-off cleanup rather than a blind `UPDATE`.

## Alternatives considered

### Option A — Clamp everything
One rule, no 400s to design copy for. **Rejected**: it turns a user's own words into a silent partial
value on a compliance record. A truncated `ErrorMessage` is a changed requirement that still prints to
an auditor as if it were the whole one.

### Option B — Reject everything
Symmetric the other way, no truncation anywhere. **Rejected** for the public upload paths specifically:
refusing a vendor's certificate over the filename their phone chose, or over a `documentType` form
value they never saw, fails the job the product exists to do — and the portal 400 arrives after the
blob is already stored.

### Option C — Validation attributes on the DTOs
`[MaxLength]` + automatic model validation. **Rejected**: Minimal APIs do not run DataAnnotations by
default (adding a filter for it is a cross-cutting behavior change), the attributes would be a THIRD
copy of each width alongside the column and the guard, and the resulting `ProblemDetails` body is not
this app's error envelope — the frontend reads `error.message` and would fall back to the generic
message on every one of these, losing exactly the actionable detail the guard exists to provide.

### Option D — Widen the columns / make them `text`
**Rejected**: an unbounded column lets an anonymous caller write megabytes per request, and it fixes
none of the non-length halves (the `documentType` never-graded bug, the duplicate race, the orphan).

### Option E — Add a unique index to fix the waitlist race
**Rejected** — it already exists, and creating one over possibly-duplicated production rows would fail
the startup auto-migration (ADR 0016). See §6.

### Option F — Remove the unused waitlist endpoint
Mooting two of its issues by deleting dead code. **Rejected as out of scope**: removing a public
endpoint is a product decision.

## References

- Tickets: [#389](https://github.com/neboxdev/complidrop/issues/389),
  [#372](https://github.com/neboxdev/complidrop/issues/372) (the first instance),
  [#373](https://github.com/neboxdev/complidrop/issues/373) (the oversize-`documentType` 500 deduped
  here), [#48](https://github.com/neboxdev/complidrop/issues/48) (rolling bug-fix epic)
- ADRs: [0044](0044-audit-client-input-clamped-at-the-boundary.md) (`ColumnClamp`, the
  constant-consumed-by-`ModelConfiguration` pattern, and why a clamp belongs where the value is read),
  [0045](0045-canonical-document-type-vocabulary.md) (the vocabulary these paths now speak, and the
  §"Option E" collapse this ticket owed), [0038](0038-vendor-contact-email-mirrored-validation.md)
  (the already-bounded `ContactEmail` and the both-paths rule),
  [0029](0029-idempotency-co-commit-reservation.md) /
  [0032](0032-portal-upload-idempotency.md) (the replay semantics the blob cleanup preserves),
  [0030](0030-compliance-verdict-combined-unit-of-work.md) (why a `UpdateFields` 22001 took the verdict
  with it), [0016](0016-apply-ef-migrations-on-startup.md) (why no index is added)
- Code: `Services/InputLengths.cs` (`InputLengths` + `InputLength`), `Data/ModelConfiguration.cs`,
  `Endpoints/{VendorPortal,Auth,Waitlist,Document,Sample,Billing,Vendor,Compliance,Reminder}Endpoints.cs`,
  `BackgroundServices/ExtractionWorker.cs` (the aliased widths)
- Tests: `RequestInputLengthTests` (every guarded field at both boundaries, the portal + dashboard
  clamps and coercions, the waitlist duplicate race, the blob-orphan assertion on the blob store, the
  long-key replay, and the PATCH vocabulary behaviour that replaced the retired set-equality pin)
