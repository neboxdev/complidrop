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
- **Idempotency**: mutating POSTs accept `Idempotency-Key`; dedupe via `IdempotencyRecord` 24h TTL.
- **Extraction pipeline (§5 of tech doc, see revision history)**: two-stage. Always-on Document AI OCR → configurable LLM (`Extraction:Provider = "gemini" | "anthropic"`; Gemini Flash via Vertex AI is default). Structured output via JSON schema (Gemini) / tool-use (Anthropic). System prompt is provider-agnostic. Every document records `ExtractionPromptVersion`.
- **Background workers**: `ExtractionWorker` polls DB every 5s using `FOR UPDATE SKIP LOCKED` with 5-minute zombie reclaim; `ReminderBackgroundService` ticks hourly and fires per-org at the org's local 08:00, deduped by `(ReminderId, DocumentId, SendDate)` unique index.
- **File uploads**: magic-byte validated (PDF / JPEG / PNG), 10 MB cap at Kestrel.
- **Vendor portal**: PUBLIC `/api/portal/{token}` routes with `portal-token` (10/hr) + `portal-ip` (30/hr) rate limits, per-link `MaxUploads` quota, and per-org monthly cost ceiling.
- **Stripe webhook**: verify signature, dedupe via `ProcessedStripeEvent`.

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

## Secrets (user-secrets in Development, env vars in prod)

`ConnectionStrings:Database`, `Jwt:Secret`, `AzureStorage:ConnectionString`, `AzureStorage:ContainerName`, `Sentry:Dsn`, `DocumentAi:ProjectId`, `DocumentAi:Location`, `DocumentAi:ProcessorId`, `DocumentAi:CredentialsPath` (or `CredentialsJson`), `Gemini:ApiKey` (when Endpoint=aistudio), `Anthropic:ApiKey` (optional), `Stripe:SecretKey`, `Stripe:PublishableKey`, `Stripe:WebhookSecret`, `Stripe:MonthlyPriceId`, `Stripe:AnnualPriceId`, `Stripe:FoundingPriceId`, `Resend:ApiKey`, `Resend:FromEmail`, `Resend:WebhookSecret` (Svix `whsec_…` signing secret for the inbound delivery-status webhook; if unset the webhook is rejected in production and allowed-with-warning only in Development).

## Rules of engagement

- Never expose API keys in code — always config + user-secrets.
- Never commit the contents of `connection string.txt`.
- Never create migrations without reading spec §3 first.
- Vendor portal endpoints (`/api/portal/*`) are PUBLIC — treat inputs as untrusted.
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

Multi-agent code review runs on every ticket before PR (`/start <n>` Phase 4). **Every bug the reviewers find gets fixed regardless of severity.** Reviewer **suggestions** are triaged three ways before the PR opens:

1. **Implement in the same PR** — the default. Polish, missing test edges, small refactors, ADRs.
2. **Defer to a follow-up ticket** — only when the suggestion expands scope, changes data semantics, or contradicts the reviewer's own caveat. The new ticket gets the reviewer's reasoning copied in. The PR body lists the spawned ticket ids.
3. **Discard** — only when the suggestion contradicts a project rule (this file, an existing ADR, the ticket's Non-goals). The PR body lists discards with the rule cited.

"Listed in the PR body but not auto-fixed" is no longer a valid outcome.

Never silently diverge from a ticket's acceptance criteria — update the ticket first.

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
