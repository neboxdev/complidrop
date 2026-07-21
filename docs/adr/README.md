# Architecture Decision Records

Each ADR captures a decision that's worth preserving for future engineers (or future you) reading the codebase.

ADRs are immutable once accepted — supersede via a new ADR rather than rewriting.

Use `/adr <title>` to scaffold a new one.

## Index

- [0001](0001-record-architecture-decisions.md) — Record architecture decisions
- [0002](0002-reminder-dedupe-is-per-recipient.md) — Reminder dedupe is per-recipient, not per-(reminder, doc, day)
- [0003](0003-frontend-testing-with-vitest.md) — Frontend testing with Vitest + React Testing Library
- [0004](0004-portal-rate-limits-via-chained-global-limiter.md) — Portal upload rate limits via a chained global limiter
- [0005](0005-testcontainers-for-integration-tests.md) — Testcontainers + Respawn + WebApplicationFactory for API integration tests
- [0006](0006-rolling-bug-fix-epic.md) — Rolling bug-fix epic synced from the `bug` label
- [0007](0007-reminder-log-send-date-is-org-local.md) — `ReminderLog.SendDate` stores the org-local calendar day (supersedes 0002 Neutral)
- [0008](0008-reminder-multi-instance-coordination-via-advisory-lock.md) — Reminder worker uses a per-(org, sendDate) Postgres advisory lock to coordinate across replicas (supersedes 0002 Negative)
- [0009](0009-no-at-time-zone-on-timestamptz-in-raw-sql.md) — Raw SQL against `timestamptz` columns uses bare `now()` / `DateTime.UtcNow`, never `AT TIME ZONE`
- [0010](0010-frontend-e2e-with-playwright.md) — Frontend E2E with Playwright (network-mocked, scrubbed artifacts, conservative flake policy)
- [0011](0011-plan-vocab-unified-with-founding-as-authenticated-only-promo.md) — Plan vocab unified as `free | pro | annual | founding`; `founding` is an authenticated-only promo tier
- [0012](0012-seo-geo-marketing-surface.md) — Public marketing/SEO surface: server-rendered content pages, structured data, and an AI-crawler-allow robots policy
- [0013](0013-account-deletion-is-soft-delete-plus-pii-scrub.md) — Account deletion is soft-delete + PII scrub, not hard delete
- [0014](0014-per-request-principal-revalidation-and-security-stamp.md) — Per-request principal re-validation + a rotating security stamp
- [0015](0015-reminder-dedupe-time-zone-edit-guard.md) — Reminder dedupe carries a trailing-window guard for editable-time-zone re-fires (extends 0002 / 0007)
- [0016](0016-apply-ef-migrations-on-startup.md) — Apply EF migrations on API startup (auto-migrate by default) with a boot-time drift guard
- [0017](0017-manual-field-edits-sync-compliance-inputs.md) — Manual field edits sync the canonical compliance inputs (`ExtractionFields` + typed columns); re-extraction overwrites manual edits
- [0018](0018-heic-heif-transcode-to-jpeg-on-ingest.md) — HEIC/HEIF uploads are transcoded to JPEG on ingest (Magick.NET) so OCR, any LLM provider, and the browser preview all see a supported format
- [0019](0019-test-harness-bridges-abortsignal.md) — The frontend test harness bridges `AbortSignal` across the jsdom ↔ undici realm boundary so `queryFn({ signal })` cancellation is testable
- [0020](0020-stripe-webhook-at-least-once-idempotent-handlers.md) — Stripe webhook dedupe is at-least-once with idempotent handlers
- [0021](0021-extraction-budget-lazy-utc-month-reset.md) — Extraction budget resets lazily on a UTC-month anchor (deliberate UTC divergence from 0007's org-local convention)
- [0022](0022-document-bytes-only-via-authenticated-proxy.md) — Document bytes are served only via the authenticated API proxy — no SAS, no public container
- [0023](0023-stripe-webhook-order-resilience-event-fence.md) — Stripe webhook order-resilience via last-applied-event fence
- [0024](0024-paid-entitlements-gate-on-subscription-flags.md) — Paid entitlements gate on Subscription flags; portal lapse is neutral and reversible
- [0025](0025-reminder-catch-up-window-and-failed-send-retry.md) — Reminder sends catch up within the org-local day; failed sends retry in place (supersedes 0002 / 0015 failed-row Neutral clauses)
- [0026](0026-environment-aware-required-config-validation.md) — Environment-aware required-config validation via a custom `IValidateOptions` + `ValidateOnStart`
- [0027](0027-compliance-date-window-boundaries.md) — Compliance date-window SQL uses an exclusive instant upper bound; the date-only deriver stays the source of truth
- [0028](0028-sample-demo-reuses-real-pipeline-and-shared-system-templates.md) — The sample-certificate demo reuses the real pipeline; assigned system templates stay shared
- [0029](0029-idempotency-co-commit-reservation.md) — Idempotency dedupe record co-commits with the side effect; the concurrent loser replays the winner
- [0030](0030-compliance-verdict-combined-unit-of-work.md) — Compliance verdict commits in the same transaction as its inputs (combined unit of work)
- [0031](0031-reminder-bounce-complaint-suppression.md) — Reminder bounce/complaint suppression — per-(org, email); complaint permanent, hard bounce only
- [0032](0032-portal-upload-idempotency.md) — Public portal upload idempotency — token-namespaced client key, co-committed with the permit
- [0033](0033-document-supersession-expired-liability.md) — Document supersession — latest cert per (vendor, type) for the Expired liability
- [0034](0034-dev-environment-isolation-and-boot-banner.md) — Dev/prod environment isolation, surfaced by a redacted startup banner
- [0035](0035-standing-cleanup-tooling-gates.md) — Standing cleanup-tooling gates (`dotnet format` + knip)
- [0036](0036-system-template-seed-convergence.md) — System templates converge to their seed (add/update/delete), tenant clones never; re-grade on change
- [0037](0037-frontend-sentry-pii-scrubbing-and-gating.md) — Frontend Sentry: PII scrubbing, dev/no-DSN no-op gating, source-map degradation (renumbered from a colliding 0036)
- [0039](0039-documents-url-source-of-truth-overlay.md) — Documents filters read the URL through a pending-write overlay; `useSearchParams()` is the base, never `window.location` unconditionally (0038 reserved by the open #369 branch)
