# CompliDrop

SMB compliance-document tracking SaaS — drop a COI / license / permit, extract the fields, get warned before anything expires.

- Landing + dashboard: Next.js 16, React 19, Tailwind 4, shadcn/ui.
- API: ASP.NET Core (.NET 10) Minimal API, EF Core 10, Npgsql → Neon.
- Extraction: Document AI OCR → Gemini Flash via Vertex AI (Anthropic Haiku 4.5 as a switchable alternate).
- Storage: Azure Blob Storage.
- Reminders: Resend.
- Billing: Stripe Checkout + Customer Portal.

See [`CLAUDE.md`](./CLAUDE.md) for architectural context, and
`C:\NewStart\Company documents\complidrop-technical-architecture.md` for the full spec.

## Local development

```bash
# 1) Backend (port 5292)
cd api/CompliDrop.Api
dotnet user-secrets set "ConnectionStrings:Database" "<neon connection string>"
dotnet user-secrets set "AzureStorage:ConnectionString" "<blob connection string>"
dotnet user-secrets set "AzureStorage:ContainerName" "documents"
dotnet user-secrets set "Jwt:Secret" "<64-byte base64>"
# optional: Sentry:Dsn, DocumentAi:*, Gemini:ApiKey, Anthropic:ApiKey,
#           Stripe:SecretKey + WebhookSecret + *PriceId, Resend:ApiKey
dotnet watch run

# 2) Frontend (port 3000)
cd frontend
npm install
# .env.local should have NEXT_PUBLIC_API_URL=http://localhost:5292
npm run dev
```

Open <http://localhost:3000>, register, upload a COI. Without extraction
credentials set, the document will remain in `Pending` — configure Document
AI + Vertex (or swap to Anthropic via `Extraction:Provider=anthropic`) to
run the pipeline end-to-end.

## Tests

```bash
cd api
dotnet test CompliDrop.Api.Tests/CompliDrop.Api.Tests.csproj
```

`MultiTenancyTests` exercises the tenant query filter and soft-delete
interceptor against the configured Postgres database. CI runs this on every
push.

## Deploy

- API → Railway (see `api/CompliDrop.Api/Dockerfile`). Env vars match the
  user-secret keys above.
- Frontend → Vercel. `NEXT_PUBLIC_API_URL` points at the Railway-hosted API.
- DB → Neon (free tier to start; upgrade to Scale after $2K MRR to remove
  cold-start auto-suspend).
- Blob → Azure Blob Storage account.

## Monorepo layout

```
complidrop/
├── api/
│   ├── CompliDrop.slnx
│   ├── CompliDrop.Api/                 # ASP.NET Core API
│   │   ├── Auth/                       # Cookie + JWT wiring, PasswordHasher
│   │   ├── BackgroundServices/         # ExtractionWorker, ReminderBackgroundService
│   │   ├── Configuration/              # Strongly-typed settings (ValidateOnStart)
│   │   ├── Data/                       # AppDbContext, SystemDbContext, interceptor, seed, migrations
│   │   ├── DTOs/
│   │   ├── Endpoints/                  # Auth, Documents, Vendors, Portal, Compliance, Reminders, Dashboard, Billing, Export, Waitlist
│   │   ├── Entities/
│   │   ├── Middleware/                 # CorrelationId, ExceptionHandling
│   │   └── Services/                   # Blob, FileValidation, Extraction (OCR + LLM), Compliance, Email, Stripe, Export, CostTracking, AuditLogger, Idempotency
│   └── CompliDrop.Api.Tests/           # xUnit — MultiTenancyTests, extraction fixtures
├── frontend/
│   └── src/
│       ├── app/
│       │   ├── (auth)/                 # login, register
│       │   ├── (dashboard)/            # dashboard, documents, vendors, rules, reminders, export, settings
│       │   ├── portal/[token]/         # PUBLIC vendor portal
│       │   └── page.tsx                # landing
│       ├── hooks/
│       └── lib/
├── CLAUDE.md
└── README.md
```

## Security checklist (§14)

- [x] Every tenant-scoped query flows through `AppDbContext` global filter by `OrganizationId`.
- [x] `MultiTenancyTests` in CI.
- [x] Auth tokens in httpOnly cookies only; never in JS/localStorage.
- [x] Cookies: `Secure` (prod), `SameSite=Lax` session / `Strict` refresh, refresh scoped to `/api/auth`.
- [x] BCrypt work factor 12, password policy min 12 chars / letter + digit.
- [x] Failed-login lockout with exponential backoff.
- [x] Rate limits: auth-strict, waitlist, portal-token, portal-ip, default-authed.
- [x] File uploads: magic-byte validation, size limit 10 MB.
- [x] Stripe webhook: signature verified AND deduped via `ProcessedStripeEvent`.
- [x] Resend webhook wired (signature verification can be enabled when Resend exposes an HMAC secret for this org).
- [x] Every mutating endpoint writes an `AuditLog` entry (interceptor + explicit calls).
- [x] Vendor portal has per-token and per-IP rate limits + per-link `MaxUploads` + per-org extraction cost ceiling.
- [x] Idempotency-Key accepted on upload + checkout; dedupe via `IdempotencyRecord`.
- [x] Configuration classes registered with `ValidateOnStart()`.
- [x] CORS restricted to configured origins only.
- [x] HTTPS forced in production (Railway TLS termination + `UseHttpsRedirection` when not in Development).
- [x] EF parameterized queries — no string concatenation.
- [x] React default escaping — no `dangerouslySetInnerHTML`.
- [x] DTO validation via Zod (frontend) + FluentValidation (API) — expand in Phase 2.
- [x] Error responses expose `{code, message, correlationId}`, never stack traces.
- [x] Sentry wired when DSN present (API + frontend).
- [x] Serilog JSON sink + correlation-id middleware tag every log line.

## Licensing

Closed source. © 2026 nebox.dev. Do not redistribute.
