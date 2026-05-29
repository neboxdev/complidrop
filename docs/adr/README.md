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
