# Development environment

The local dev environment **must not share the production database or storage account.** This page
is the setup guide and the rationale. Background: [#271](https://github.com/neboxdev/complidrop/issues/271).

## Why this matters

Before #271, the local `user-secrets` pointed `ConnectionStrings:Database` at the **same Neon
database Railway prod serves**, and `AzureStorage` at the **same `complidropstorage` account**. That
made every local session a live prod actor:

- A local `dotnet run` on a branch with a pending migration **auto-migrates prod** at boot
  (`Database:AutoMigrate` defaults on — [ADR 0016](adr/0016-apply-ef-migrations-on-startup.md)).
- The Railway `ExtractionWorker` is a permanent **second queue consumer**, so local pipeline
  experiments are unreproducible (prod claims `Pending` docs within seconds, even when the local API
  is dead).
- Local test orgs/documents/audit rows land in **prod**; fixture uploads burn the prod org's
  extraction budget.
- Reminder-eligible local test data gets **real emails** from prod's Resend — and the dev DB is a
  clone of prod data, so those are real vendor/user addresses.

## The isolated dev setup

Configure these in **user-secrets** (Development) — never in `appsettings*.json`, which is committed.

```bash
cd api/CompliDrop.Api
dotnet user-secrets set "ConnectionStrings:Database" "<your Neon DEV-branch connection string>"
dotnet user-secrets set "AzureStorage:ConnectionString" "UseDevelopmentStorage=true"
# Resend:ApiKey is intentionally NOT set in dev — see "Email is silent" below.
# Stripe keys are sk_test_… / rk_test_… (test mode), never sk_live_.
```

| Resource | Production | Development |
|---|---|---|
| **Database** | Neon prod branch | A separate **Neon `dev` branch** (a copy-on-write clone — branching is near-free). Migrations applied to it never touch prod. |
| **Blob storage** | `complidropstorage` Azure account | **Azurite** local emulator (`UseDevelopmentStorage=true`). |
| **Email (Resend)** | Live API key → real sends | **No `Resend:ApiKey`** → `IEmailService.IsEnabled` is false → sends are skipped (email-silent). |
| **Stripe** | `sk_live_…` | `sk_test_…` (test mode). |

### Azurite (local blob storage)

Azurite is the official Azure Storage emulator. Install once and run it before testing uploads:

```bash
npm install -g azurite        # one-time (this repo verified v3.35.0)
azurite                        # or start it via the VS Code "Azurite" extension
```

With `AzureStorage:ConnectionString = UseDevelopmentStorage=true`, the API talks to the local
emulator; the container is created on first upload.

### Email is silent in dev

The dev DB is a **clone of prod data** containing real vendor and user email addresses, and the
hourly `ReminderBackgroundService` would mail them the moment a cloned document enters a reminder
window. Leaving `Resend:ApiKey` unset makes `ResendEmailService.IsEnabled` false, so every send is
skipped with a warning instead of delivered. **Do not add a Resend key to dev secrets.**

### Telemetry is silent in dev too — both ends

The frontend's Sentry error monitoring ([ADR 0037](adr/0037-frontend-sentry-pii-scrubbing-and-gating.md))
follows the same isolated-by-default posture: it is a no-op unless `NEXT_PUBLIC_SENTRY_DSN` is set
**and** `NODE_ENV=production`. Leave `NEXT_PUBLIC_SENTRY_DSN` unset in dev (the `next dev` server is
`development` anyway, so even a stray DSN captures nothing). PostHog is likewise gated on
`NEXT_PUBLIC_POSTHOG_KEY` being present. **Do not add a Sentry DSN or PostHog key to your dev env.**

The backend now matches it ([ADR 0053](adr/0053-backend-error-events-via-a-serilog-sentry-sink.md) /
[#386](https://github.com/neboxdev/complidrop/issues/386)): since backend `LogError` lines became real
Sentry events, `Sentry:Dsn` alone is no longer enough to switch reporting on — the environment must
also not be `Development`. That gate exists because a DSN *was* found in the local user-secrets store,
which the integration-test host reads too (it shares the API's `UserSecretsId` and boots Development).
Since **the dev database is a clone of prod data**, an exception naming a real vendor would otherwise
have been uploaded to the production Sentry project by nothing more than running the test suite.
`Sentry:Dsn` is a **production-only** secret; a leftover one in dev user-secrets is now inert, but
`dotnet user-secrets remove "Sentry:Dsn"` is still the tidy state. To exercise Sentry locally on
purpose, run with `ASPNETCORE_ENVIRONMENT=Staging` and a **separate** DSN — never the prod project's.

## The boot banner (how you know which environment you're in)

On startup, `StartupEnvironmentBanner` logs one INFO line naming the resolved targets — the durable
guard against silently pointing dev at prod again:

```
Startup environment [Development] — Database: dev-host.neon.tech (db: cd_dev) | Blob: Azurite (local emulator) | Email: silent (no Resend API key — sends are skipped) | Stripe: test mode
```

The line is printed **immediately above the migration line**, so the DB host is visible before any
DDL runs. It is **redacted** — it shows hostnames, account names, and key *modes* (test/live), never
the DB password, storage account key, or any API key.

In **Development only**, the banner additionally logs a loud `WARNING` for any target that looks like
a **live/production** resource:

- `Stripe:SecretKey` is a live key,
- `Resend:ApiKey` is set (dev would send real email),
- `AzureStorage` points at a real Azure account instead of Azurite.

These warnings are Development-only — those same values are *correct* in prod. The guard **warns, it
does not abort the boot**: deliberately pointing local at a prod resource for a one-off is a
legitimate (founder-sanctioned) mode, and a hard fail would be hostile to it. The banner just makes
sure the choice is never accidental.

## Design decisions

- **`Database:AutoMigrate` stays ON in Development.** The original #271 hazard was a local boot
  auto-migrating *prod*. Now that dev is an isolated, throwaway Neon branch, auto-migrating *it* on
  boot is desirable (it keeps the dev schema current with no manual step). Setting
  `AutoMigrate=false` in Development would instead make the boot-time drift guard
  ([ADR 0016](adr/0016-apply-ef-migrations-on-startup.md)) *refuse to start* whenever a migration is
  pending — friction with no remaining safety benefit. The real residual need — "am I about to
  migrate the right database?" — is met by the boot banner naming the host first.
- **Banner warns, never blocks.** Mirrors `RateLimitingGate`'s force-on-but-don't-crash posture: a
  loud, unmissable signal that respects a deliberate override.
- **No secret ever reaches a log line.** Same invariant as the
  [ADR 0026](adr/0026-environment-aware-required-config-validation.md) config validators; pinned by
  `StartupEnvironmentBannerTests`.

## Configuration reference

### Backend secrets (user-secrets in Development, env vars in prod)

`ConnectionStrings:Database`, `Jwt:Secret`, `Frontend:BaseUrl` (public site origin, `https://www.complidrop.com` — REQUIRED in prod: portal links, email-borne URLs (verify/reset), and Stripe checkout + billing-portal return URLs are minted from it and silently fall back to `http://localhost:3000` when unset, see [#250](https://github.com/neboxdev/complidrop/issues/250)), `AzureStorage:ConnectionString`, `AzureStorage:ContainerName`, `Sentry:Dsn` (**production-only** — a DSN in Development is ignored outright, see the telemetry section above), `Sentry:TracesSampleRate` (default `0.1`), `DocumentAi:ProjectId`, `DocumentAi:Location`, `DocumentAi:ProcessorId`, `DocumentAi:CredentialsPath` (or `CredentialsJson`), `Gemini:ApiKey` (when Endpoint=aistudio), `Anthropic:ApiKey` (optional), `Stripe:SecretKey`, `Stripe:PublishableKey`, `Stripe:WebhookSecret`, `Stripe:MonthlyPriceId`, `Stripe:AnnualPriceId`, `Stripe:FoundingPriceId`, `Resend:ApiKey`, `Resend:FromEmail`, `Resend:WebhookSecret` (Svix `whsec_…` signing secret for the inbound delivery-status webhook; if unset the webhook is rejected in production and allowed-with-warning only in Development).

### Frontend env vars (set in the build/host environment; `NEXT_PUBLIC_*` are baked into the client bundle at build time)

`NEXT_PUBLIC_API_URL`, `NEXT_PUBLIC_SITE_URL`, `NEXT_PUBLIC_POSTHOG_KEY` / `NEXT_PUBLIC_POSTHOG_HOST`, and — frontend Sentry ([ADR 0037](adr/0037-frontend-sentry-pii-scrubbing-and-gating.md)) — `NEXT_PUBLIC_SENTRY_DSN` (public DSN, **distinct from the backend `Sentry:Dsn`**; absence ⇒ Sentry is a no-op, and it stays unset in dev per the posture above), the optional `NEXT_PUBLIC_SENTRY_ENVIRONMENT` (event tag; defaults to `NODE_ENV`) and `NEXT_PUBLIC_SENTRY_TRACES_SAMPLE_RATE` (default `0`), plus the build-time source-map upload trio `SENTRY_AUTH_TOKEN` / `SENTRY_ORG` / `SENTRY_PROJECT` (server-only, never shipped; absent ⇒ upload skipped and the build still succeeds).

## Related

- [#271](https://github.com/neboxdev/complidrop/issues/271) — the dev-isolation bug.
- [#228](https://github.com/neboxdev/complidrop/issues/228) — sweeping the prod debris left by the
  shared-environment era (separate task; protect "The Garden Hall" demo org).
- [ADR 0016](adr/0016-apply-ef-migrations-on-startup.md) — apply EF migrations on startup.
- [ADR 0026](adr/0026-environment-aware-required-config-validation.md) — environment-aware config
  validation (the never-echo-a-secret invariant).
