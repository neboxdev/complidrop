# 0007. `ReminderLog.SendDate` stores the org-local calendar day

- **Status:** accepted
- **Date:** 2026-05-25
- **Deciders:** Ruben G.

## Context

[ADR 0002](0002-reminder-dedupe-is-per-recipient.md) ("Reminder dedupe is per-recipient") concluded with a Neutral consequence noting that `ReminderLog.SendDate` continued to store the UTC date at send time — not the org-local calendar day — and flagged a future ADR if analytics ever cared. It does now: ticket [#24](https://github.com/neboxdev/complidrop/issues/24) revisits the column's semantic.

Today, `SendDate = DateOnly.FromDateTime(nowUtc)` in `ReminderBackgroundService`. For an `America/New_York` org firing at local 08:00 on Jan 15, that's 13:00 UTC Jan 15 — same calendar day in both zones, no asymmetry. For an `Asia/Tokyo` org firing at local 08:00 on Jan 15, the instant is 23:00 UTC Jan 14, and `SendDate` reads Jan 14 — the day before the local calendar says. Dedupe still works because each org's send window only opens for one UTC hour per local day (`IsLocalSendWindow` gates on `local.Hour == 8`), so all logs for one local day share one `SendDate`. The semantic, though, is off by a day for every org east of the date line — Tokyo, Sydney, Hong Kong, Singapore — and any "reminders sent on Jan 15 for this org" query (analytics dashboard, audit log, Resend webhook reader) silently returns the wrong row set.

The broader project convention, articulated in the #24 thread and consistent with [CLAUDE.md](../../CLAUDE.md) §"Core patterns", is that datetimes are stored as UTC where there is a UTC representation (every `timestamptz` column) but the *semantic* tracks the org's wall clock. `SendDate` is `DateOnly` — there is no UTC representation to preserve, only the semantic, which the original code got wrong.

## Decision

`ReminderLog.SendDate = DateOnly.FromDateTime(ToLocal(org.TimeZone, nowUtc))` — the org-local calendar day, not the UTC date at send time. The column type (`date`) and name are unchanged; only the value assignment moves from `nowUtc` to the already-computed `localDate` variable in the worker. `SentAt` (`timestamptz`) continues to store the precise UTC instant — there is no UTC↔local question for the instant column, only for the calendar-day column.

Migration `BackfillReminderLogSendDateToOrgLocal` rewrites existing rows via `(SentAt AT TIME ZONE org."TimeZone")::date`, joined through `Reminders → Organizations`. Safe wrt the unique index `(ReminderId, DocumentId, SendDate, RecipientEmail)`: the worker only fires once per org-local day, so any row's `SendDate` shifts by at most one calendar day and can never collide with another row sharing the rest of the tuple. The WHERE guard makes the SQL idempotent and bounds blast radius — re-running it after deploy is a no-op, and a row whose UTC date already equals its org-local date (every NY/UK/EU org pre-Asia-tick) is skipped without a write.

This is the formal revision of ADR 0002's Neutral consequence on `SendDate`. ADR 0002's dedupe-key decision stands unchanged; only the "SendDate continues to store the UTC date" line is superseded.

## Consequences

### Positive

- Analytics queries "show me reminders sent on Jan 15 for Tokyo orgs" match the org's local calendar without per-row TZ math in the read path.
- The dedupe invariant from ADR 0002 (one `(reminder, doc, day, recipient)` send per local day) is unchanged: the worker writes and queries with the same `localDate` variable, so write and lookup remain self-consistent.
- The Resend inbound webhook handler is unaffected — it keys lookups by `ResendMessageId`, not by `SendDate`. (Verified by code-reading `ResendWebhook` during the #24 review.)

### Negative

- The migration is one-shot raw SQL via `migrationBuilder.Sql` — the first data-migration in the repo. The pattern is unavoidable (EF Core's builder API has no data-mutation surface), but it introduces a coupling between the CLR's `TimeZoneInfo.ConvertTimeFromUtc` (used by the worker going forward) and Postgres' `AT TIME ZONE` operator (used by the one-shot backfill). Both engines consult the same IANA tzdata on the deploy target, so for any well-formed IANA zone they agree.
- Rows whose `Organizations.TimeZone` is a string Postgres doesn't recognise are silently skipped by the backfill's `AND o."TimeZone" IN (SELECT name FROM pg_timezone_names)` guard. The skipped row keeps its pre-#24 SendDate value and resurfaces naturally the next time the worker writes for that org (which uses `TryFindTimeZone` and falls back to UTC for unknown zones). This is the trade-off chosen over an unguarded UPDATE, which would have failed the entire deploy on a single bad row. The guard does mean a malformed-TZ row stays stale until the next worker write — surfaced via deploy logs is the planned mitigation if this ever becomes load-bearing.

### Neutral

- The migration `Down` reverses the value to the UTC date of `SentAt` — formal inverse only; we don't expect to run it.
- The column name stays `SendDate`. The alternative path (rename to `SendDateLocal`) was not chosen — see Alternatives below.

## Alternatives considered

### Option A — rename the column to `SendDateUtc`, keep the UTC value

Make the asymmetry explicit at the schema level: the column says what it stores. Rejected because the natural query for every consumer — analytics dashboards, the Resend webhook handler, the org's audit log — is the org-local day. Renaming to `SendDateUtc` would have made the wrong default explicit while still requiring read-path TZ math on every query. The asymmetry is worth fixing, not labelling.

### Option B — add a second column `SendDateLocal` alongside `SendDateUtc`

Carry both, let consumers pick. Rejected because every shipped consumer wants the local date; carrying the UTC date as a separate column buys nothing the existing `SentAt` (timestamptz) doesn't already give for free.

### Option C — backfill via a C# job rather than SQL

Move the backfill out of the migration into a one-off `IHostedService` so the CLR's `TimeZoneInfo.ConvertTimeFromUtc` is the single source of truth for the conversion. Rejected because the migration runs on startup before the app accepts traffic anyway, and the SQL form executes in a single round-trip on a near-empty MVP table. A C# job would add an orchestration concern (gate on completion before workers start, idempotency, observability) for no semantic gain — the two conversions agree on every well-formed IANA zone.

## Test coverage

- [`Tokyo_org_records_send_date_as_local_calendar_day_not_utc`](../../api/CompliDrop.Api.Tests/ReminderBackgroundServiceTests.cs) — the regression that motivated this ADR.
- `NewYork_org_records_send_date_as_local_calendar_day` — symmetry partner so a future "let's just use nowUtc" refactor fails both tests at once, not just the Tokyo one.
- `Tokyo_pre_existing_log_with_local_send_date_blocks_resend` — dedupe still holds when the pre-existing row was written with the new semantic.
- `Backfill_sql_rewrites_legacy_utc_send_date_to_org_local_for_tokyo_row` — the migration's SQL itself, verified against a Tokyo row in the pre-#24 shape and an NY row already in the new shape. SQL is sourced from a `const` on the migration class so the test cannot drift from prod.

## References

- Ticket: [#24](https://github.com/neboxdev/complidrop/issues/24) (supersedes the SendDate Neutral consequence in [ADR 0002](0002-reminder-dedupe-is-per-recipient.md))
- Origin: ticket [#7](https://github.com/neboxdev/complidrop/issues/7) (correctness-reviewer first surfaced the semantic during ADR 0002's review)
- Migration: `api/CompliDrop.Api/Migrations/20260525101534_BackfillReminderLogSendDateToOrgLocal.cs`
- Worker: `api/CompliDrop.Api/BackgroundServices/ReminderBackgroundService.cs`
