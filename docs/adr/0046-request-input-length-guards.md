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
(One later entry — `DocumentFieldUpdatesPerRequest`, §4 Amendment 1 — bounds a collection COUNT rather
than a column width, and is therefore outside the `ModelConfiguration` binding by design.)

`ExtractionWorker`'s two `DocumentField` widths become one-line **aliases** of `InputLengths` rather
than a second pair of literals. Two writers hold opposite policies on those same two columns (see §2),
and that only stays coherent while there is exactly one width. Same one-line-delegate shape as
`ExtractionWorker.Clamp` → `ColumnClamp.To`.

> **Amendment 1 (#389 review) — the binding is now enforced, and two more columns joined it.**
>
> This section's decision shipped with no mechanical guard, while sibling ADR 0044 pins exactly the
> same property with two tests. Those two tests — `AuditClientInputClampTests`'
> `Every_clamped_column_takes_its_width_from_the_shared_constant` (built EF model vs the constant) and
> `ModelConfiguration_names_the_width_constants_rather_than_a_numeric_literal` (source text, which is
> the half that catches an **equal-valued** re-inline) — now cover **every** `InputLengths` width too.
> The source-text assertion is scoped to each `builder.Entity<T>` block, because property names are not
> unique across the file (`Name` appears on three entities).
>
> `User.Email` and `EmailVerificationToken.NewEmail` were left unbound in the original pass while the
> identical anonymous-signup email column (`WaitlistEntry.Email`, also 256) was bound in the same
> commit. Both now read a new `InputLengths.UserEmail`, and `AuthEndpoints.IsValidEmail`'s
> `email.Length <= 256` reads it as well — one number instead of three hand-copied ones. **Only the
> number is shared:** that guard keeps its own `validation.email` code (§3 Amendment 1) and its
> deliberate laxness (ADR 0038 — an account email is proven by the verification mail, a vendor contact
> email never is). No schema change: 256 was already the width.
>
> Two relocations, no behaviour change:
>
> - The `IResult`-producing guard `InputLength` moved to `Endpoints/`, beside `IdempotencyResults`. It
>   was the only type in `Services/` touching `Microsoft.AspNetCore.Http.Results`. It stays a shared
>   **envelope** rather than a `(code, message)` pair each endpoint shapes itself, because the property
>   §3 promises is that a client sees ONE code and ONE message shape — returning the finished result
>   makes that true by construction instead of by eight call sites getting it right. The widths stay in
>   `Services/`, where `ModelConfiguration` reads them.
> - `EmailUniqueIndexName` moved off `WaitlistEndpoints` into `Services/WaitlistSignup`, with the
>   `IsDuplicateEmail` predicate (the `IdempotencyService.KeyIndexName` / `IsKeyConflict` shape). It had
>   made `ModelConfiguration` compile against `Endpoints` — the only Data→Endpoints dependency in the
>   assembly. See §6 Amendment 1 for how the name is now verified.

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

One code — `validation.too_long` — for **every rejection this guard makes**, so a client has one rule
for the #389 family, with the actionable detail in `error.message`, which the frontend renders verbatim
(CLAUDE.md § Frontend error-message policy).

> **Amendment 1 (#389 review) — that is "one code for these guards", not "the app's only over-length
> rejection".** Two older rejections stay separate **on purpose**, and unifying either is the bug:
>
> - `ContactEmail`'s `validation.contact_email` (#369 / ADR 0038), reachable on the **same vendor
>   request** as this guard. Over-length is one of several ways a vendor contact email can be invalid,
>   and they all answer with one code plus a message naming the specific problem; splitting length off
>   would give a single field two codes. `.claude/reviewers.md` records unification here as a finding.
> - `AuthEndpoints.IsValidEmail`'s `validation.email`, likewise one "enter a valid email" answer
>   covering shape *and* length. Its 256 now reads `InputLengths.UserEmail` (§1 Amendment 1) — the
>   number is shared, the error code is not.
>
> That cap is the ONLY thing between the **anonymous** register route (and the authenticated
> change-email route) and a 22001-as-500 on `User.Email` / `EmailVerificationToken.NewEmail` — the
> register guard block explicitly declines to list email on exactly that basis. So it is now asserted
> rather than assumed, on both routes and at both boundaries, **including the code**: exactly
> `InputLengths.UserEmail` succeeds and stores whole; one over answers `validation.email`. A future
> "consistency" pass that folds this into `validation.too_long` goes red (#389 re-review).
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

> **Amendment 1 (#389 re-review) — a bounded ELEMENT is not a bounded REQUEST.**
>
> `UpdateFields` bounded each correction's `FieldName`/`FieldValue` but left
> `FieldsUpdateRequest.Fields` uncapped. Kestrel admits a 10 MB body, so ~45-byte entries buy a single
> authenticated PUT a six-figure element count — walked twice by the guard, grouped, then written row by
> row against the tracked document. Cheap to send, expensive to serve.
>
> `InputLengths.DocumentFieldUpdatesPerRequest` (200) bounds it, checked **before** the walk, and it is
> the one entry in that file measuring a COUNT rather than a column width — kept there so it is pinned
> and reviewed beside its siblings, and deliberately absent from the `ModelConfiguration` binding tests
> because there is no column for it to agree with. 200 is an order of magnitude above reality (the
> extraction schema defines ~20 canonical fields, and the detail page renders one input per extracted
> field).
>
> Its own code, `validation.too_many_fields`, **not** `validation.too_long`: "you sent too many things"
> and "one thing was too long" are different problems with different fixes, and the frontend renders the
> message verbatim. Pinned at both boundaries like every length guard here.

### 5. The dashboard blob cleanup is attempted on every failure path

`UploadDocument` gains the portal's `documentPersisted` + `finally` shape, so **every** failure path
runs the cleanup, not just the idempotency-conflict one. (As first shipped this section said every
failure path *deletes* the blob; Amendment 2 below is why that is no longer the claim — the cleanup is
now attempted everywhere and deletes only what it can prove is an orphan.)

ADR 0029/0032 semantics are preserved exactly, and the reasoning is the load-bearing part:

- `blobName` embeds **this request's own** `Guid`, so a concurrent same-key **loser deletes only its
  own blob** and can never touch the winner's.
- A **sequential replay** returns at the fast-path `TryGetAsync` **before any blob is uploaded**, so a
  committed record still replays the winner's exact response — pointing at the winner's still-present
  blob.
- The flag flips to true only **after** `SaveChangesAsync` returns.

`TryDeleteBlobAsync` keeps swallowing its own failures (logged loud enough for an operator to find the
orphan). That is what lets it sit in a `finally` without masking the real exception on the way out.

> **Amendment 1 (#389 review) — the cleanup must not use the REQUEST's `CancellationToken`.**
>
> As shipped, the `finally` passed `ct` to the delete. `BlobStorageService.DeleteAsync` forwards it to
> `BlobClient.DeleteIfExistsAsync`, which throws the moment it sees an **already-cancelled** token — it
> never issues the DELETE. So a **client abort between the blob upload and the commit** (tab closed,
> phone loses signal) cancelled `ct`, failed `SaveChangesAsync(ct)`, ran the cleanup, and the cleanup
> threw and was swallowed. The failure path most likely to strand a blob was the one whose cleanup could
> not run — which contradicted this section's own "**every** failure path deletes the blob".
>
> Both `TryDeleteBlobAsync` helpers (documents + sample) now take **no** `CancellationToken` and pass
> `CancellationToken.None`, so a caller cannot reintroduce the doomed token. The **portal** twin — the
> shape this one was mirrored from — carried the identical flaw, plus a
> `when (ex is not OperationCanceledException)` filter that let the aborted delete's own exception
> escape the `finally` and **replace** the real one; it now passes `CancellationToken.None` and swallows
> unfiltered like its sibling. Deleting an orphan is milliseconds of best-effort work that has to
> outlive the request that triggered it.
>
> Pinned by tests that abort the request the instant the blob is stored, with no timing race: a
> test-only `IStartupFilter` swaps `HttpContext.RequestAborted` for a token the fake blob store cancels
> from inside `UploadAsync`, and `FakeBlobStorageService.DeleteAsync` now honours `ct` the way the real
> client does (a fake that ignored it would report a green cleanup Azure would have refused). The
> pre-existing 22001-driven orphan test cannot catch this — its token is never cancelled. All **three**
> sites are pinned, the sample seed included (a later review pass found only the two uploads were).

> **Amendment 2 (#389 re-review) — the cleanup must CONFIRM ABSENCE before it deletes, and its own
> token is short-lived rather than `None`.**
>
> Amendment 1 moved the trigger from a signal that PROVES the row rolled back (`IsKeyConflict`) to one
> that only means "`SaveChangesAsync` did not return normally". **Those are not the same set.** A
> failure arriving AFTER Postgres committed — a connection reset while reading the COMMIT
> acknowledgement, or a cancellation racing that round trip (the very client-abort path Amendment 1
> made the delete *work* on) — leaves the `Document` row **persisted** while the `finally` deletes its
> blob. The result is a document the customer can never view or download, whose extraction can never
> succeed (`DownloadAsync` returns null), and which still appears on the audit export. That is strictly
> worse than the orphan this section set out to fix.
>
> So the trigger stays broad and a **confirming read** decides: `Endpoints/OrphanBlobCleanup` re-queries
> `Documents.AnyAsync(d => d.Id == documentId)` on a **fresh scope's `SystemDbContext`** — a different
> connection from the one that just faulted, which may be mid-unwind, holding a rolled-back
> transaction, or bound to a cancelled token — and **skips the delete when the row is there**. The read
> uses `IgnoreQueryFilters` (a system context, which is where CLAUDE.md permits it) because a
> soft-deleted row owns its blob too: ADR 0013 deliberately RETAINS the blob of a deleted document, so
> answering through the soft-delete filter would report "absent" for a row that is merely hidden.
>
> **If the read itself fails we do NOT delete.** Absence was not proved, and the asymmetry is the whole
> point: an orphan costs storage and is named in an operator log; a wrongly-deleted blob costs the
> customer their document.
>
> Narrowing the trigger back to `IsKeyConflict` is **not** the fix — that re-opens the original orphan
> on every failure class nobody enumerated, and reviewers.md rules it out. The confirming read is what
> lets the trigger stay broad AND stay safe.
>
> Second change, same helper: the delete runs on a **short-lived `CancellationTokenSource` (10s)** the
> cleanup owns, not `CancellationToken.None`. `None` fixed the aborted-delete but left a best-effort
> delete free to burn the full Azure retry budget (`MaxRetries` x a 30s network timeout plus back-off,
> ~90s) inside the `finally` while the caller waits — including on the PUBLIC portal route. The
> short-lived token is never pre-cancelled, so Amendment 1's property holds.
>
> All three sites (dashboard upload, portal upload, sample seed) now delegate to that one helper, so
> the policy cannot drift between them. Pinned by: a post-commit fault on the dashboard upload and on
> the sample seed (a test-only interceptor throws from EF's `SavedChangesAsync`, which fires only after
> `StateManager.SaveChangesAsync` — the implicit transaction's commit — has returned), asserting the row
> exists AND the blob survives; a direct both-branches test of the helper; and a token assertion
> (`CanBeCanceled` is true for a real source and false for `None`, so the two are told apart without
> waiting on the deadline).

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

> **Amendment 1 (#389 review) — the index name is checked against Postgres, not against itself.**
>
> A **wrong** constant silently turns the concurrent-duplicate race back into a 500 on a public
> marketing form, and the only thing exercising it was the probabilistic five-racer test: if the race
> does not materialise on a given run, a wrong constant ships green. The EF-model form of the check
> would be **vacuous**, because `ModelConfiguration` takes the name FROM the same constant
> (`HasDatabaseName`) — it would compare the constant to itself.
>
> So `WaitlistEndpointsTests` now reads `pg_indexes` on the migrated test database and asserts the index
> Postgres actually carries is the one `WaitlistSignup.IsDuplicateEmail` matches on. That is the only
> independent witness, and it is deterministic. (The constant itself moved to `Services/` — §1
> Amendment 1.)

> **Amendment 2 (#389 re-review) — the rule applies to the sample-demo index too, and the predicates
> have their own tests.**
>
> This section establishes that an index name matched by a 23505 `catch` must be **one** constant owned
> by a layer both sides may depend on. `SampleEndpoints` held the other instance of exactly that shape —
> a hand-copied `IX_Documents_OrganizationId_SampleUnique` in the endpoint and a second literal in
> `ModelConfiguration` — so it moves to `Services/SampleData` as
> `DocumentUniqueIndexName` / `IsDocumentUniqueViolation`, and `ModelConfiguration` names it in
> `HasDatabaseName`. The value is unchanged, so no migration (`has-pending-model-changes` reports none).
> `SampleData` rather than a new file: it already owns the sample-demo constants shared across layers.
>
> Naming the constant makes the EF-model form of the check vacuous for this index too, so
> `SampleEndpointsTests` gains the same `pg_indexes` witness the waitlist has.
>
> Both **predicates** also gain the deterministic unit test they lacked (the
> `IdempotencyService.IsKeyConflict` shape: true for a 23505 naming the right index, false for one
> naming a different index, false for a non-Postgres inner exception). Until now the only thing
> exercising `IsDuplicateEmail` was the probabilistic five-racer test, which passes whether or not its
> losing arm ever runs — so a predicate broadened to bare SqlState (swallowing an unrelated 23505 as a
> duplicate signup) or narrowed to never match (a public 500) could ship green.

### 7. The duplicate document-type literal is collapsed

`DocumentEndpoints.AllowedDocumentTypes` — the second copy ADR 0045 § "Option E" deferred to this
ticket — is **deleted**. All three sites in that file (the PATCH type edit and both upload paths) now
call `CanonicalDocumentTypes` directly. The set-equality test that pinned the duplication is retired
with it rather than left comparing the vocabulary to itself; the contract is asserted **over HTTP**
instead, which is strictly stronger than two C# sets being equal.

### 8. A non-nullable DTO property is still nullable on the wire (#389 review)

Added during the review pass, because the length guards themselves surfaced the sibling failure and one
of them introduced a new instance of it.

System.Text.Json binds a **missing or JSON-null** property to `null` even when the positional record
parameter is declared non-nullable. So a non-nullable string DTO property that lands in a NOT NULL
column is a **500 waiting to happen**, and `InputLength.FirstViolation` cannot catch it: it treats null
as "fits", correctly — *an absent value is not an over-length one*. The two are complementary guards,
not one.

Every such property now carries a blank guard beside its length check, the shape
`CreateTemplate` / `CreateVendor` already used:

- `UpdateFields` — `req.Fields` **and** each element's `FieldName`. `{}`, `{"fields": null}`,
  `{"fields":[null]}` and an element without a name each NRE'd on the new guard's own walk. An **EMPTY
  array stays legal**: the detail page enables Save with no edits precisely while the manual-review card
  is showing, and that no-op save is what resolves the review (ADR 0040).
- `UpsertRule` — `req.Operator`, over a NOT NULL column, on the very block this ticket hardened
  (`validation.operator`). A blank operator is meaningless anyway: `EvaluateRule`'s switch has no arm
  for it, so the rule would grade nothing while still printing on the auditor-facing export.
- `UpdateTemplate` — `req.Name` (found in the original pass, listed here for completeness).

The remaining non-nullable string columns written from request DTOs in the endpoints this ticket
touched were audited and are already safe: `Organization.Name`, `User.FullName`, `User.Email`,
`Vendor.Name`, `ComplianceTemplate.Name` and `WaitlistEntry.Email` by blank checks;
`ComplianceRule.DocumentType`, `Document.DocumentType` and `Organization.TimeZone` by null-tolerant
normalizers; `Reminder.EmailSubjectTemplate` is a nullable column.

## Consequences

### Positive

- No request string can 500 an insert on any of these routes; the public ones answer a 400 the caller
  can act on, or clamp and succeed.
- A vendor's upload can no longer be lost to a long filename or a stray `documentType`, and a
  `documentType` from either ingress path can no longer create a never-graded document.
- The dashboard upload leaks no blob on any failure it can confirm rolled back, closing the ticket's
  one data-loss-adjacent item — and, per §5 Amendment 2, it never deletes the file of a document that
  DID commit, which would have been a worse one.
- A public marketing form no longer 500s on a concurrent duplicate signup.
- All four idempotent routes agree on one client-key bound, and a long key still dedupes.
- One vocabulary literal instead of two, and one index-name literal per index instead of two.

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

- **No migration and no data migration.** Every width is numerically unchanged; the waitlist index name
  is EF's own default and the sample index name is the one it already carried (§6 Amendment 2), so
  naming both from a constant changes no model. `dotnet ef migrations has-pending-model-changes`
  reports no model changes.
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
- Code: `Services/InputLengths.cs` (the widths, plus the one collection-count bound — §4 Amendment 1),
  `Endpoints/InputLength.cs` (the `validation.too_long` envelope; it lives in `Endpoints/` per §1
  Amendment 1), `Services/WaitlistSignup.cs` (the `(Email)` index name + the duplicate predicate — §6),
  `Services/SampleData.cs` (the sample partial-unique index name + its predicate, moved there for the
  same reason — §6 Amendment 2), `Endpoints/OrphanBlobCleanup.cs` (the shared confirm-then-delete blob
  rollback — §5 Amendment 2), `Data/ModelConfiguration.cs`,
  `Endpoints/{VendorPortal,Auth,Waitlist,Document,Sample,Billing,Vendor,Compliance,Reminder}Endpoints.cs`,
  `BackgroundServices/ExtractionWorker.cs` (the aliased widths)
- Tests: `RequestInputLengthTests` (every guarded field at both boundaries — including the account
  email, which answers `validation.email` and not `validation.too_long`; the portal + dashboard clamps
  and coercions; the waitlist duplicate race; the blob-orphan assertion on the blob store; the
  client-abort cleanup on all three upload paths and the post-commit-fault cases that must NOT delete;
  the long-key replay on all three clamped routes; the field-update count cap; and the PATCH vocabulary
  behaviour that replaced the retired set-equality pin), `WaitlistEndpointsTests` +
  `SampleEndpointsTests` (each index name against `pg_indexes`, plus its predicate),
  `AuditClientInputClampTests` (the width binding, §1 Amendment 1)
