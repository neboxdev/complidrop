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
  `TemplateCorrections` / `RuleEngine` (ADR 0042). It is COPY-ONLY: flag-OFF keeps the
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
