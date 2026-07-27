# CompliDrop — review addendum

Read at review time by every generic reviewer persona in the machine-level claude-kit
(`~/.claude/agents/`) and by the /start, /review, /plan and /epic-review skills. This
file owns the project's review-time facts: rosters, deliberate patterns, sensitive
areas, commit scopes, scale. The invariants themselves live in CLAUDE.md § Core
patterns and `docs/adr/` — this file points at them, it does not restate them.

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
  framing. `ComputeOutcome`'s zero-applicable-rules branch stores
  `expiringSoon ? ExpiringSoon : Pending`; `ComplianceStatusDeriver.Effective` promotes even a
  stored `Pending` to `ExpiringSoon` inside the 30-day window; `VendorEndpoints.ComputeCoverage`
  counts `Compliant or ExpiringSoon` as in-force and rolls the vendor up to `Covered`; and
  `ExportService` prints "Expiring soon" in the auditor package — with an EMPTY "What we checked"
  panel behind it. That is an affirmative-coverage overclaim. Fixing the read surfaces is
  [#443](https://github.com/neboxdev/complidrop/issues/443) (out of #373's scope, already ticketed —
  do not re-report).
  Three KNOWN GAPS, recorded in ADR 0045 — do not re-report them as new findings:
  - **Legacy rows are not laundered.** Coercion runs only on the next EXTRACTION and nothing
    re-extracts a processed row, so a pre-deploy non-canonical type persists (never graded, own
    supersession group, invisible to `?type=coi`) until a human re-types it. #389 does not touch
    stored rows either. Deliberately NOT fixed by a data migration — because laundering production
    rows is a destructive operation needing human sign-off (measure the population first), NOT
    because the residue is harmless. It is not; see #443 above.
  - **A clamped field can be narrowed by a manual edit.** `ExtractionFields` (jsonb) keeps the FULL
    value AS EXTRACTED, but the detail page binds its input to the clamped `DocumentField.FieldValue`
    and `UpdateFields` writes the submitted text back into `ExtractionFields` — so saving an
    untouched clipped `description_of_operations` (a verdict input via the additional-insured
    `contains` fallback) narrows the canonical value. Surfacing the clip in the UI is
    [#444](https://github.com/neboxdev/complidrop/issues/444).
  - **One unpinned mirror:** `frontend/src/lib/document-types.ts`. A .NET test cannot reach it
    (no shared fixture, unlike ADR 0038's contact-email corpus). The five in-repo mirrors that CAN
    be reached are pinned: both provider schemas, the extraction prompt's DOCUMENT TYPES block,
    `DocumentEndpoints.AllowedDocumentTypes`, and `DisplayLabels.DocumentTypes`.
    `RuleEngine/RuleSetLoader.DocumentTypes` is a SIXTH set and deliberately NOT a mirror — an RD-c
    SUBSET (`coi | license | certification | other`) that must NOT be pinned equal to `All`.
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
    real finding.
  - `Services/InputLengths.cs` is the SOURCE of those widths; `ModelConfiguration` calls
    `HasMaxLength(InputLengths.X)` (the `AuditColumnLengths` / `ContactEmail.MaxLength` pattern). A
    re-inlined literal there IS a real finding. Columns NOT listed there (`UploadedBy`,
    `BlobStoragePath`, `DocumentType`, `TimeZone`) are absent on purpose — each is length-safe by
    construction, and listing them would imply a guard that need not exist.
  - The `Idempotency-Key` clamp must sit at the SINGLE point the header is read, before BOTH the
    lookup and the storage. A version that clamps only one of the two makes a repeat of a long key
    miss its own record and duplicate the side effect — idempotency silently broken. The PUBLIC
    portal still IGNORES an oversize key rather than clamping (truncation manufactures collisions an
    untrusted third party could force on purpose, ADR 0044 §2's reasoning); the three authenticated
    routes clamp at the same 128. That asymmetry is deliberate.
  - The dashboard upload's blob cleanup is UNCONDITIONAL (`documentPersisted` + `finally`, the
    portal's shape). It does not violate ADR 0029/0032: `blobName` embeds the request's own Guid so
    a concurrent same-key loser deletes only ITS OWN blob, and a sequential replay returns at the
    fast path before any blob exists. A version that narrows it back to the `IsKeyConflict` catch
    re-opens the orphan.
  - The waitlist duplicate race is fixed by CATCHING the existing `(Email)` unique violation (matched
    on the index NAME, the `IsKeyConflict` shape) and replaying the friendly 200 — NOT by adding an
    index. Adding one over possibly-duplicated prod rows would fail the startup auto-migration and
    take prod down (ADR 0016). "Just add a unique index" is a refuted suggestion, not a finding.
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

## Sensitive areas (`careful-review` label ⇒ autonomous sessions stop before merge)

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

Any touched path matching one of these ⇒ pass `--careful` to the merge gate:

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
