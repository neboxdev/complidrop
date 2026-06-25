# CompliDrop — Claude Code project guide

CompliDrop is a $49/mo SMB compliance-document SaaS: upload a COI / license / permit → automatic extraction → rules evaluation → vendor portal → reminders → audit-ready export.

Canonical tech reference: `C:\NewStart\Company documents\complidrop-technical-architecture.md`. Read it before making architectural changes. Phase-2 scope lives in `complidrop-phase2-architecture.md` — do not build Phase 2 features until the product hits $5K MRR.

## Stack

- **Backend**: ASP.NET Core (.NET 10) Minimal APIs at `api/CompliDrop.Api`. EF Core 10 + Npgsql against Neon Postgres. DB doubles as durable extraction queue and audit sink.
- **Frontend**: Next.js 16 + React 19 + Tailwind 4 + shadcn/ui at `frontend/`. App Router, TanStack Query, React Hook Form + Zod, sonner toasts.
- **Solution**: `api/CompliDrop.slnx` wraps `CompliDrop.Api` + `CompliDrop.Api.Tests`.

## Core patterns

- **Tenant isolation**: `AppDbContext` instance member `CurrentOrgId` drives global query filters on every tenant entity. Background services use `SystemDbContext` which skips the tenant filter. Never bypass the tenant filter in request-path code — use `IgnoreQueryFilters()` only inside background workers or system contexts.
- **Auth**: httpOnly cookie-based JWT (`cd_session` 15 min, `cd_refresh` 30 days path `/api/auth`). BCrypt work factor 12. Lockout after 10 fail attempts with exponential backoff. Tokens never leave the server; frontend just sends `credentials: "include"`.
- **Audit log**: `AuditSaveChangesInterceptor` auto-sets `UpdatedAt`, translates `Delete → soft delete + DeletedAt`, and emits an `AuditLog` row with `Before/After` JSON on every entity mutation. Non-entity events (login, webhook) call `IAuditLogger` explicitly.
- **Idempotency**: mutating POSTs accept `Idempotency-Key`; dedupe via `IdempotencyRecord`. The dedupe record **co-commits in the same transaction as the request's side effect** (the new `Document` / sample / checkout record), so the `(OrganizationId, Key)` unique index is an atomic concurrent-duplicate backstop: of two in-flight same-key requests exactly one `SaveChanges` wins, and the loser catches the unique violation (`IIdempotencyService.IsKeyConflict`, matched on the index name) and **replays the winner's exact response** (not a 409). A committed record is a permanent claim that replays for as long as the row exists (`ExpiresAt` is a future-GC hint, not a replay filter). Generalizes the sample endpoint's partial-unique-index race backstop to the shared key — see [ADR 0029](docs/adr/0029-idempotency-co-commit-reservation.md) and [#336](https://github.com/neboxdev/complidrop/issues/336).
- **Extraction pipeline (§5 of tech doc, see revision history)**: two-stage. Always-on Document AI OCR → configurable LLM (`Extraction:Provider = "gemini" | "anthropic"`; Gemini Flash via Vertex AI is default). Structured output via JSON schema (Gemini) / tool-use (Anthropic). System prompt is provider-agnostic. Every document records `ExtractionPromptVersion`.
- **Background workers**: `ExtractionWorker` polls DB every 5s using `FOR UPDATE SKIP LOCKED` with 5-minute zombie reclaim; `ReminderBackgroundService` ticks hourly and fires per-org from the org's local 08:00 onward (catch-up until local midnight; failed sends retry in place — see [ADR 0025](docs/adr/0025-reminder-catch-up-window-and-failed-send-retry.md)), deduped per recipient by the `(ReminderId, DocumentId, SendDate, RecipientEmail)` unique index, and **skips per-`(org, email)` suppressed addresses**: the Resend webhook records an `EmailSuppression` on a spam complaint (permanent) or a hard `Permanent` bounce (transient bounces don't suppress), the worker skips those recipients, and a dead vendor address surfaces on the vendor (detail alert + list badge) and the activity feed — see [ADR 0031](docs/adr/0031-reminder-bounce-complaint-suppression.md) and [#340](https://github.com/neboxdev/complidrop/issues/340).
- **Document supersession (#327)**: a document is *superseded* when a newer one (later `CreatedAt`) **that also extends coverage** (non-null `ExpirationDate >= ` this doc's) exists for the same `(VendorId, DocumentType)`. The coverage-extension clause (ADR 0033 Amendment 1, from the #327 re-review) is a compliance-safety guard: a still-processing (null-expiry), no-expiry, or earlier-expiry "renewal" does **not** supersede, so it can never make a genuinely-unmet expired liability silently vanish; a future-/equal-expiry renewal still supersedes (no double-count). The shared `DocumentSupersession.IsCurrent`/`.IsSuperseded` predicate (a set-based correlated `EXISTS`) de-counts superseded old certs from the **Expired liability** — the dashboard `expired` count, the expiry-pipeline expired bucket, the documents list `?status=Expired` (so dashboard count == deep-linked list), and the reminder windows (a renewed vendor isn't pestered). The audit export keeps but **annotates** superseded docs (its in-memory `ExportService.SupersededIds` mirror is pinned equal to the predicate by a test). Deliberately NOT applied to the compliant/nonCompliant/expiringSoon counts or the future pipeline buckets — see [ADR 0033](docs/adr/0033-document-supersession-expired-liability.md).
- **Compliance verdict atomicity (#337)**: the persisted `Document.ComplianceStatus` must never contradict the canonical inputs (`ExtractionFields` + the typed `GeneralLiabilityLimit/EffectiveDate/ExpirationDate` columns) it was computed from. Each input-writer folds the verdict into its OWN unit of work via `IComplianceCheckService.ApplyEvaluationAsync(context, doc, ct)` — which applies `ComplianceStatus` + the `ComplianceCheck` rows to the caller's tracked context WITHOUT saving — so `UpdateFields`, `UpdateDocument` (vendor/type assign) and `ExtractionWorker.PersistSuccess` each commit (inputs, verdict) in ONE `SaveChanges`. This makes a manual-edit-vs-(re)extraction race last-writer-wins on the WHOLE tuple (ADR 0017 aligned), never a torn pair. A recompute failure degrades the verdict to `Pending` (a safe "not yet graded" state), never a confident verdict from stale inputs. Pure re-grades that don't change inputs (Check-again, the template/vendor fan-outs) use `EvaluateAsync` (load → apply → save). See [ADR 0030](docs/adr/0030-compliance-verdict-combined-unit-of-work.md).
- **File uploads**: magic-byte validated (PDF / JPEG / PNG), 10 MB cap at Kestrel.
- **Sample demo (#238)**: `POST` / `DELETE /api/sample` seeds / clears the one-click "Try a sample certificate" demo — a generated sample COI (`SampleCertificateGenerator`, QuestPDF) run through the REAL extraction pipeline, plus a sample vendor on the system "Caterer" checklist, so a cold org reaches a verdict with no file on hand. Tagged by the `IsSample` flag on `Document` + `Vendor` (labelled in the UI, excluded from the plan document-limit). One live sample per org (partial unique index `IX_Documents_OrganizationId_SampleUnique`); clear deletes the blob and soft-deletes the rows (contrast: a normal document delete RETAINS its blob, ADR 0013). See [ADR 0028](docs/adr/0028-sample-demo-reuses-real-pipeline-and-shared-system-templates.md).
- **Vendor portal**: PUBLIC `/api/portal/{token}` routes. The UPLOAD route is rate-limited `portal-token` (10/hr) + `portal-ip` (30/hr); the read routes (info + upload-status GETs) are uncapped per-token — so a vendor polling status isn't throttled — but carry a generous `portal-ip` 240/hr backstop so a single IP can't flood the public read routes (#242, classified by `PortalRateLimit`). Plus a per-link `MaxUploads` quota and a per-org monthly cost ceiling. The upload route is **idempotent**: it honors a client `Idempotency-Key` scoped per `(link-org, "portal:{token}:{key}")` and co-commits the dedupe record in the SAME transaction as the permit reservation + `Document` insert, so a double-submit can't duplicate the document OR burn a second permit (the loser's conflict rolls the permit back) — see [ADR 0032](docs/adr/0032-portal-upload-idempotency.md) and [#333](https://github.com/neboxdev/complidrop/issues/333).
- **Stripe webhook**: verify signature, dedupe via `ProcessedStripeEvent`.
- **Frontend error monitoring (#356)**: `@sentry/nextjs` wired for the App Router — client/server/edge init from one shared option builder (`frontend/src/lib/sentry/options.ts`), `app/global-error.tsx` boundary, `next.config.ts` wrapped with `withSentryConfig`. **No-op unless `NEXT_PUBLIC_SENTRY_DSN` is set AND `NODE_ENV=production`** (mirrors the #271 dev-silent posture). `sendDefaultPii: false` + a unit-tested `beforeSend`/`beforeSendTransaction` scrubber (`frontend/src/lib/sentry/scrub.ts`) strips cookies (`cd_session`/`cd_refresh`), auth/portal tokens, emails, and request/response bodies from events + breadcrumbs before transmit; `/portal/{token}` URL paths are deterministically redacted. **Application code must never hand raw document field values to Sentry** (`captureException`/`setExtra`/`setContext`) — the scrubber catches the SDK's automatic capture, not arbitrary prose we attach (treat a new such call of user/document content as a review finding). Errors tie to the backend via the `correlation_id` tag (from `ApiError.correlationId`). Session Replay is OFF; `tracesSampleRate` defaults 0 (env-tunable). Source-map upload is gated on `SENTRY_AUTH_TOKEN` (build succeeds without it). See [ADR 0036](docs/adr/0036-frontend-sentry-pii-scrubbing-and-gating.md).

## Commands

```bash
# API (port 5292 by default)
cd api/CompliDrop.Api && dotnet watch run

# Frontend (port 3000)
cd frontend && npm run dev

# Tests
cd api && dotnet test CompliDrop.Api.Tests/CompliDrop.Api.Tests.csproj

# Migrations
cd api/CompliDrop.Api && dotnet ef migrations add <Name> --context AppDbContext
cd api/CompliDrop.Api && dotnet ef database update --context AppDbContext
```

**Migrations apply automatically on deploy** — the API runs pending migrations at
startup before serving traffic (`Database:AutoMigrate`, default on; fail-fast on a
bad migration; boot-time drift guard refuses to start if migrations are pending and
auto-migrate is off). So a migration-adding merge updates the prod schema on the
next Railway deploy with no manual `dotnet ef database update`. The CLI commands
above are still how you create migrations locally and apply them to your dev DB.
See [ADR 0016](docs/adr/0016-apply-ef-migrations-on-startup.md) and [#226](https://github.com/neboxdev/complidrop/issues/226).

## Secrets (user-secrets in Development, env vars in prod)

`ConnectionStrings:Database`, `Jwt:Secret`, `Frontend:BaseUrl` (public site origin, `https://www.complidrop.com` — REQUIRED in prod: portal links, email-borne URLs (verify/reset), and Stripe checkout + billing-portal return URLs are minted from it and silently fall back to `http://localhost:3000` when unset, see #250), `AzureStorage:ConnectionString`, `AzureStorage:ContainerName`, `Sentry:Dsn`, `DocumentAi:ProjectId`, `DocumentAi:Location`, `DocumentAi:ProcessorId`, `DocumentAi:CredentialsPath` (or `CredentialsJson`), `Gemini:ApiKey` (when Endpoint=aistudio), `Anthropic:ApiKey` (optional), `Stripe:SecretKey`, `Stripe:PublishableKey`, `Stripe:WebhookSecret`, `Stripe:MonthlyPriceId`, `Stripe:AnnualPriceId`, `Stripe:FoundingPriceId`, `Resend:ApiKey`, `Resend:FromEmail`, `Resend:WebhookSecret` (Svix `whsec_…` signing secret for the inbound delivery-status webhook; if unset the webhook is rejected in production and allowed-with-warning only in Development).

**Dev environment must be isolated from prod** ([#271](https://github.com/neboxdev/complidrop/issues/271)): local user-secrets point `ConnectionStrings:Database` at a separate **Neon `dev` branch**, `AzureStorage:ConnectionString` at **Azurite** (`UseDevelopmentStorage=true`), leave `Resend:ApiKey` **unset** (email-silent — the dev DB is a clone of prod data with real addresses), and use **`sk_test_`** Stripe keys. Every boot logs a redacted `StartupEnvironmentBanner` line naming the resolved DB host / blob target / email mode / Stripe mode (never a secret), and in Development warns loudly if any target looks live. `Database:AutoMigrate` deliberately **stays on** in Development (the isolated dev branch is throwaway; the banner is the guard). Full setup + rationale: [docs/dev-environment.md](docs/dev-environment.md).

**Frontend env vars** (set in the build/host environment, not user-secrets; `NEXT_PUBLIC_*` are baked into the client bundle at build time): `NEXT_PUBLIC_API_URL`, `NEXT_PUBLIC_SITE_URL`, `NEXT_PUBLIC_POSTHOG_KEY` / `NEXT_PUBLIC_POSTHOG_HOST`, and — frontend Sentry ([ADR 0036](docs/adr/0036-frontend-sentry-pii-scrubbing-and-gating.md)) — `NEXT_PUBLIC_SENTRY_DSN` (public DSN, **distinct from the backend `Sentry:Dsn`**; absence ⇒ Sentry is a no-op, and it's left **unset in dev** to mirror the email-silent posture, since Sentry is also gated to `NODE_ENV=production`), the optional `NEXT_PUBLIC_SENTRY_ENVIRONMENT` (event tag; defaults to `NODE_ENV`) and `NEXT_PUBLIC_SENTRY_TRACES_SAMPLE_RATE` (default `0`), plus the **build-time source-map upload** trio `SENTRY_AUTH_TOKEN` / `SENTRY_ORG` / `SENTRY_PROJECT` (server-only, never shipped; absent ⇒ upload skipped and the build still succeeds).

## Rules of engagement

- Never expose API keys in code — always config + user-secrets.
- Never commit the contents of `connection string.txt`.
- Never create migrations without reading spec §3 first.
- Raw SQL touching a `timestamptz` column uses bare `now()` / `DateTime.UtcNow`, never `AT TIME ZONE` — see [ADR 0009](docs/adr/0009-no-at-time-zone-on-timestamptz-in-raw-sql.md). When reviewing a diff that adds `ExecuteSqlRaw` / `ExecuteSqlInterpolated` / `migrationBuilder.Sql` / `cmd.CommandText`, check: any `AT TIME ZONE` on a timestamptz expression whose result feeds back into a timestamptz comparison or assignment is a bug. (Output-only conversion to `date` / wall-clock display — clause 3 — stays legitimate.)
- Vendor portal endpoints (`/api/portal/*`) are PUBLIC — treat inputs as untrusted.
- Never declare a React component inside another component's render body — hoist helper components to module scope (or above the parent component in the same file). The `react-hooks/static-components` lint rule enforces this and blocks CI on violation; inline components reset their state on every parent render and break React DevTools. See [#73](https://github.com/neboxdev/complidrop/issues/73) and the `SkeletonRow` pattern in [register-form.tsx](frontend/src/app/(auth)/register/register-form.tsx) for the canonical fix.
- Every form `<label>` must associate with its control — either `htmlFor="<input-id>"` pointing at an input with the same `id`, or by nesting the control inside the label. The `jsx-a11y/label-has-associated-control` lint rule (configured in [frontend/eslint.config.mjs](frontend/eslint.config.mjs)) enforces this at file-save time and blocks CI on a missing wire-up; the lint rule covers the static shape of every label (existence + non-empty `htmlFor`), while [`forms.test.tsx`](frontend/src/test/forms.test.tsx) pins the runtime wire-up (`getByLabelText` actually resolves an input) on each enumerated form — add an entry there when introducing a new form. If the project ever adopts a shadcn-style `<Label>` wrapper, extend the rule's `labelComponents` option so the lint coverage continues to match the codebase. See [#76](https://github.com/neboxdev/complidrop/issues/76) for the label-wiring contract and [#131](https://github.com/neboxdev/complidrop/issues/131) for the rule rollout.
- **Frontend error-message policy**: user-facing error toasts and error-card copy come from the server's `error.message` field when present, otherwise the `GENERIC_FALLBACK_MESSAGE` exported from [`frontend/src/lib/api.ts`](frontend/src/lib/api.ts) ("Something went wrong. Try again."). NEVER surface raw HTTP `res.statusText` ("Bad Gateway"), interpolated status codes (`Export failed (502)`), or browser TypeErrors ("Failed to fetch") into toast / error-card copy — these are HTTP-jargon hostile to SMB users. The `api.*` client enforces this for every envelope-returning request via [`fetchOrFriendlyThrow`](frontend/src/lib/api.ts) ([#77](https://github.com/neboxdev/codedrop/issues/77)). **Binary endpoints (file streams, blob downloads) go through `api.getBlob`** ([#254](https://github.com/neboxdev/complidrop/issues/254)) — same cookie transport, coalesced silent 401-refresh, and friendly-error mapping as the envelope client; do NOT hand-roll a bare `fetch` for downloads (the export page's old bare-fetch pattern is migrated). Any residual bare-fetch site must IMPORT `GENERIC_FALLBACK_MESSAGE` and emit it on every error path. Tests assert `not.toHaveTextContent(/bad gateway/i)` / `not.toMatch(/typeerror/i)` style invariants to catch leaks (see `frontend/src/lib/api.test.ts` and `frontend/src/app/(dashboard)/documents/page.test.tsx`).
- **Frontend testid policy**: prefer accessible-text selectors (`getByText` / `getByRole` / `getByLabelText`) in component and E2E tests. Reach for `data-testid` only when text selectors are ambiguous-by-design — e.g. a status badge whose label collides with section copy or sibling badges. Three placement rules:
  1. **Leaf, not wrapper.** Place the testid on the element whose state is asserted (the badge / input / row), NOT on a wrapper container. Wrapper testids force tests to traverse children via `within()` and re-introduce the brittleness the testid was meant to remove.
  2. **Compound elements: tag the asserted substring.** When a badge renders status PLUS incidental data (e.g. the list-page extraction badge that shows `Pending · 87%` confidence), the testid wraps a stable nested `<span>` around the asserted substring rather than the whole badge — `toHaveText('Pending')` against the outer badge would fail on the ` · 87%` suffix.
  3. **One-of-its-kind per page.** The flat `{noun}-status` naming applies to detail-page badges where one of each kind exists. For LIST or REPEATED rows where every row carries the same badge, prefer either (a) `getByRole('row', { name: /.../ })` + `within(row).getByText(...)` for the accessible-text path, or (b) row-scoped ids (`extraction-status-{rowId}`) when text selectors don't disambiguate. Never let `getByTestId('extraction-status')` resolve to N elements — it defeats the purpose.

  See `extraction-status` / `compliance-status` on [documents/[id]/page.tsx](frontend/src/app/(dashboard)/documents/[id]/page.tsx) ([#92](https://github.com/neboxdev/complidrop/issues/92)) for the canonical detail-page shape. The documents LIST page deliberately stays on accessible-text selectors per rule 3 — see [#92](https://github.com/neboxdev/complidrop/issues/92) review for the rationale.
- File validation must use magic bytes, not `Content-Type`.
- Webhook handlers must verify signatures and dedupe event ids.

## Workflow & record-keeping

Substantive changes flow through four record layers:

1. **Tickets** — GitHub Issues (or `docs/tickets/` when `gh` is unavailable). Every non-trivial change starts with a ticket. Typos and trivial fixes excepted.
2. **Conventional Commits** — durable shipped record. Always reference the issue: `feat(extraction): retry on Gemini 5xx (#42)`.
3. **ADRs** (`docs/adr/`) — architectural decisions worth preserving. Append-only; supersede via new ADR.
4. **CHANGELOG.md** — auto-generated from commits via `git-cliff`.

Features begin with `/plan`, run through PM review (6 reviewers including a CompliDrop-specific legal-compliance reviewer), then `/breakdown` into tickets. No jumping straight from idea to code.

Tests are mandatory after implementation — unit tests for pure logic (xUnit), integration tests using `WebApplicationFactory` for boundaries (Postgres, Azure Blob, Document AI, Gemini, Stripe webhook, Resend). Frontend tests use Jest/Vitest.

Multi-agent code review runs on every ticket before PR (`/start <n>` Phase 4). **Every bug the reviewers find gets fixed regardless of severity.** Reviewer **suggestions** are triaged three ways before the PR opens — but the first option is the **overwhelming default, target ≥99%**:

1. **Implement in the same PR** — the strong default. Polish, missing test edges, small refactors, ADRs, mid-sized cleanups all land here. Even non-trivial changes are absorbed unless an exception in (2) applies.
2. **Defer to a follow-up ticket** — **rare**. Permitted only when **(a)** the suggestion strictly requires its own ADR / spec conversation (data-semantics or public-API contract change, or the reviewer explicitly flagged it as deferred-to-later), or **(b)** the 5-hour Claude session budget is approaching its cap with bugs and core suggestions still pending — in which case never defer a `bug`-kind finding. "Bigger refactor" alone is NOT sufficient. The new ticket gets the reviewer's reasoning copied in; the PR body lists the spawned ticket id(s) AND the deferral reason (necessity vs. budget).
3. **Discard** — only when the suggestion contradicts a project rule (this file, an existing ADR, the ticket's Non-goals). The PR body lists discards with the rule cited.

"Listed in the PR body but not auto-fixed" is no longer a valid outcome. When unsure between implement and defer, **implement**.

Never silently diverge from a ticket's acceptance criteria — update the ticket first.

### Rolling bug-fix epic (#48)

Bug-fix and latent-issue tickets are indexed in one rolling epic — **[#48 Bug fixes & latent issues](https://github.com/neboxdev/complidrop/issues/48)** — so open defects are visible in one place and historical fixes stay browseable. See [ADR 0006](docs/adr/0006-rolling-bug-fix-epic.md) for the rationale.

- **How a ticket joins**: apply the `bug` label. Any GitHub issue with `bug` (open or closed) is auto-listed in the epic body by `.github/workflows/bugfix-epic-sync.yml` (event-triggered + daily cron).
- **What counts as `bug`**: a defect, a latent fragility (race/TZ/multi-instance assumption), a correctness/semantic decision needed to resolve wrong behavior, a contract ambiguity producing wrong client behavior. Not a feature, refactor, or test scaffolding.
- **When deferring a review finding to its own ticket** (per the three-way triage above), apply the `bug` label so the workflow picks it up.
- **Crossing off**: closing the ticket (a merged PR with `Closes #N` does this) flips the checkbox to `[x]` on the next workflow run. Re-opening unticks it.
- **Dual epic membership is fine**: a `bug` ticket can also be listed in another epic (e.g. a launch-blocker tracked in #1). The rolling epic is a discovery index, not an ownership claim.

Other epics today:
- #1 Backend hardening before launch (closed 2026-05-27)
- #33 Frontend test hardening (closed 2026-05-27)
- #41 Codebase simplification pass (gated on launch; one-time)
- #48 Bug fixes & latent issues (rolling; never closes)
- #150 Post-launch follow-ups (umbrella for deferred-past-launch work; starts after launch — #40 lives here now)

## Slash commands

| Command | Purpose |
|---|---|
| `/plan <feature>` | Interview → spec → 6-reviewer PM review → approval |
| `/pm-review` | Re-run PM reviewers on an existing spec |
| `/breakdown` | Approved spec → epic + 5–12 ordered child tickets |
| `/start <n>` | Pick up ticket → implement → tests → 5-agent review → fix bugs → PR |
| `/review` | Diagnostic 5-agent review on the current branch (no auto-fix) |
| `/epic-review <n>` | Consolidated review across an epic after merge |
| `/ticket <description>` | Single ticket creator outside `/breakdown` |
| `/tickets` | List/triage open work |
| `/adr <title>` | Scaffold a new ADR |
| `/changelog` | Regenerate `CHANGELOG.md` via git-cliff |
| `/worklog` | Free-form narrative entry |
