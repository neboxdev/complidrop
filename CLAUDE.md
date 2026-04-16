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

`ConnectionStrings:Database`, `Jwt:Secret`, `AzureStorage:ConnectionString`, `AzureStorage:ContainerName`, `Sentry:Dsn`, `DocumentAi:ProjectId`, `DocumentAi:Location`, `DocumentAi:ProcessorId`, `DocumentAi:CredentialsPath` (or `CredentialsJson`), `Gemini:ApiKey` (when Endpoint=aistudio), `Anthropic:ApiKey` (optional), `Stripe:SecretKey`, `Stripe:PublishableKey`, `Stripe:WebhookSecret`, `Stripe:MonthlyPriceId`, `Stripe:AnnualPriceId`, `Stripe:FoundingPriceId`, `Resend:ApiKey`, `Resend:FromEmail`.

## Rules of engagement

- Never expose API keys in code — always config + user-secrets.
- Never commit the contents of `connection string.txt`.
- Never create migrations without reading spec §3 first.
- Vendor portal endpoints (`/api/portal/*`) are PUBLIC — treat inputs as untrusted.
- File validation must use magic bytes, not `Content-Type`.
- Webhook handlers must verify signatures and dedupe event ids.
