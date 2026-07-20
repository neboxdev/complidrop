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
  nonCompliant / expiringSoon or future pipeline buckets (ADR 0033 + Amendment 1).
- A normal document delete RETAINS its blob (ADR 0013); the sample-demo clear DELETES
  its blob (ADR 0028). Both directions are deliberate.
- Two email validators coexist ON PURPOSE (#369): `Services/ContactEmail.IsWellFormed`
  (vendor contact email — strict: dotted domain, no blank/invisible chars, ≤256) and
  `AuthEndpoints.IsValidEmail` (account email — lax: `Contains('@')`). An account email
  is PROVEN by the verification mail, so a typo self-corrects and over-strict signup
  validation locks out a real customer; a vendor contact email is never proven and a
  typo fails silently forever (reminders retry in place, ADR 0025). Different evidence,
  different strictness — do not "unify" them. The pair that MUST agree is
  `Services/ContactEmail.cs` ↔ `frontend/src/lib/contact-email.ts`; drift between
  THOSE two is a real finding — and it is now pinned mechanically, since both test
  suites are driven by the shared corpus
  `api/CompliDrop.Api.Tests/SharedFixtures/contact-email-cases.json`. Add a case there,
  not to one suite. It sits in the api test tree so `api-ci`'s `api/**` filter covers a
  corpus-only edit; `frontend-ci` names it explicitly (the #272 precedent). Moving it
  back under `docs/` would silently un-enforce the guarantee above.
- Those two mirrors spell their blank-character class out as explicit `\uXXXX` ranges
  instead of `\s`, and strip edges with that same set instead of `Trim()`/`.trim()`. That
  verbosity is DELIBERATE and load-bearing, not a style lapse: .NET's `\s` includes
  U+0085 and excludes U+FEFF while JS's is the reverse, and the two native trims diverge
  on the same pair — which made the mirrors genuinely disagree (a pasted BOM was rejected
  client-side and ACCEPTED server-side, storing an unsendable address). `\s`, `\p{C}`, or
  any general-category class re-introduces engine-dependence; do not "simplify" to one.
- Edge-stripping is a LINEAR SCAN (`ContactEmail.IsBlank` / `isBlank`), not a regex, on
  both sides. The regex form `^[BLANK]+|[BLANK]+\z` is unanchored in its second
  alternative, so when that alternative can't match, the engine retries at every offset —
  quadratic in a request-controlled body, with the 256 cap applied only AFTER
  normalization. Do not "simplify" the scan back into a regex.
  Note the hostile shape if you ever re-measure this: blanks in the MIDDLE with a
  non-blank at BOTH ends. Leading/trailing padding is LINEAR (the `^`-anchored alternative
  consumes it in one match) — a repro built from leading spaces shows no blowup and will
  wrongly clear the pattern. Measured on the generated-regex path: 100k → 225 ms,
  200k → 1.0 s, 400k → 4.3 s.
  The predicate and the character class are kept in agreement by a test that walks the
  whole BMP, so adding a range to one without the other fails.
- Vendor update is BLOCK-UNTIL-FIXED on a malformed contact email (#369): `UpdateVendor`
  validates the submitted address whether or not that request changed it, so a vendor
  whose STORED address is already malformed (written by the pre-#369 unguarded edit path)
  must be corrected or cleared before unrelated edits land. Deliberate: the address is
  actively failing, the detail form shows the reason inline on load with Save disabled,
  and both correcting and clearing are accepted. Finding these rows without opening each
  vendor is [#430](https://github.com/neboxdev/complidrop/issues/430), not a defect here.
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
