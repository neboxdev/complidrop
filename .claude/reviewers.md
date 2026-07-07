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
- Bare `now()` / `DateTime.UtcNow` in raw SQL on `timestamptz` is correct; the bug is
  `AT TIME ZONE` whose result feeds back into a timestamptz comparison/assignment
  (ADR 0009 — output-only conversion for display stays legitimate).
- Reminder catch-up window (org-local 08:00 → midnight) and failed-send retry-in-place
  (ADR 0025); per-recipient dedupe key and suppression skips (ADR 0031).
- `Database:AutoMigrate` stays ON in Development — the dev Neon branch is throwaway
  and the startup environment banner is the guard (#271).

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
