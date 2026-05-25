# 0002. Reminder dedupe is per-recipient, not per-(reminder, doc, day)

- **Status:** accepted
- **Date:** 2026-05-22
- **Deciders:** Ruben G.

## Context

The reminder background worker (`ReminderBackgroundService`) fires per-org at the org's local 08:00 and writes a `ReminderLog` row for every email it sends. A unique index on `ReminderLog` protects against the worker re-sending the same reminder twice — important because the worker ticks hourly, can race with itself across restarts, and emails are expensive (Resend cost + customer trust).

The original schema put the unique index on `(ReminderId, DocumentId, SendDate)`. That choice silently assumed "one log row per (reminder, doc, day)", which works as long as each `(reminder, doc, day)` send goes to exactly one recipient.

But the worker is configured to send to **multiple recipients per (reminder, doc, day)**: a reminder with both `NotifyInternalUser = true` and `NotifyVendor = true` produces one email per internal user plus one to the vendor — all sharing the same `(reminder, doc, day)` tuple. The intent — captured by `ReminderLog.RecipientEmail` being a separate column at all — was always "one row per recipient." The unique index didn't agree.

Ticket #7 surfaced this while writing the AC2 + AC3 tests:

- AC2: a reminder already logged for `(ReminderId, DocumentId, SendDate)` is not sent twice (dedupe across ticks).
- AC3: both internal and vendor recipients are included as configured.

With the original index these two ACs were **unsatisfiable together**. On a multi-recipient tick the worker added two `ReminderLog` rows in the same `SaveChangesAsync`. The second insert violated the unique constraint, the whole transaction rolled back, no rows persisted, and the next tick — finding no log rows — re-sent to both recipients. The "dedupe" only held when there was exactly one recipient.

## Decision

Widen the unique index to `(ReminderId, DocumentId, SendDate, RecipientEmail)` and move the worker's dedupe check from per-(reminder, doc) to per-recipient.

The worker now:

1. Pulls all already-sent `RecipientEmail` values for the `(reminder, doc, day)` tuple into a `HashSet<string>` (case-insensitive, `StringComparer.OrdinalIgnoreCase`).
2. Loops over each computed recipient and skips any already in the set.
3. Saves once per doc, with multiple recipient rows per save when applicable.

`recipients.Distinct(StringComparer.OrdinalIgnoreCase)` collapses case-variant duplicates upstream of the dedupe set so a user `owner@x.com` and a vendor `Owner@x.com` don't both get sent within the same tick.

Migration: `WidenReminderLogDedupeKeyWithRecipient` drops the old index and creates the new one. The `Down` migration reverses it. Run on a near-empty table at MVP, so no `CONCURRENTLY` lock concern.

## Consequences

### Positive
- AC2 and AC3 are now jointly satisfiable. Multi-recipient sends persist all log rows and dedupe correctly across subsequent ticks.
- The dedupe semantic now matches the data model's intent: `ReminderLog.RecipientEmail` exists for a reason; the unique key acknowledges it.
- The `HashSet` check is one query per `(reminder, doc, day)` — same round-trip count as the old `AnyAsync`, but bounded by the recipient count (typically 1–2 rows).

### Negative
- In a multi-instance deploy, two workers ticking at the same UTC hour can both pre-load an empty `HashSet`, both send to the same recipient, and only one's insert wins the unique-constraint race — the loser's email is still already at Resend. The DB index protects log integrity but cannot recall a sent email. At single-instance MVP this is a non-issue; if/when we scale out the worker, this needs an advisory lock or a coordinator row. Tracked separately.
- The per-recipient check pulls every `RecipientEmail` for the day's tuple instead of a boolean `EXISTS` — slightly more data over the wire (a couple of short strings instead of one boolean), but well under any threshold worth optimizing.

### Neutral
- `ReminderLog.Status = "failed"` rows are treated identically to `"sent"` for dedupe purposes — a transient Resend outage means no same-day retry. Pre-existing behavior; carried over unchanged. If we want intraday retries on failed sends, that's a future ADR.
- `ReminderLog.SendDate` continues to store the UTC date, not the org-local date. Dedupe works because each org's send window only opens for one UTC hour per local day, so all logs for one local day share one `SendDate`. Analytics queries that expect SendDate to align with the org's local day will be off by a day for non-US zones — also a future ADR if it matters. *(Superseded by the 2026-05-25 amendment below — SendDate now stores the org-local day.)*

## Alternatives considered

### Option A — keep the narrow index, save per recipient
Keep `(ReminderId, DocumentId, SendDate)` and call `SaveChangesAsync` after each `Add` so the first insert commits before the second is attempted. Rejected: it inverts the dedupe meaning ("one row wins, the rest fail silently") and obscures multi-recipient sends in the log. The data model already has `RecipientEmail` as a real column — the index should reflect that.

### Option B — drop `RecipientEmail` from `ReminderLog`, store the recipient list as a JSON column
Make the unique key narrow and pack recipients into one row. Rejected: breaks per-recipient delivery-status tracking (the Resend inbound webhook updates a single log row keyed by `ResendMessageId`, which is one per recipient).

### Option C — use a per-recipient idempotency key (string hash of the tuple) as a separate column
Add a `DedupeKey` column = hash of `(ReminderId, DocumentId, SendDate, RecipientEmail)` and put the unique index on that. Rejected: extra column buys nothing the natural composite key doesn't, and the natural key is already query-able for analytics.

## References

- Tickets: [#7](https://github.com/neboxdev/complidrop/issues/7) (surfaced during AC2 + AC3 test writing)
- Migration: `api/CompliDrop.Api/Migrations/20260522094405_WidenReminderLogDedupeKeyWithRecipient.cs`
- Worker: `api/CompliDrop.Api/BackgroundServices/ReminderBackgroundService.cs`
- Schema: `api/CompliDrop.Api/Data/ModelConfiguration.cs` (search for `ReminderLog`)

---

## Amendment 2026-05-25 — `SendDate` is the org-local calendar day ([#24](https://github.com/neboxdev/complidrop/issues/24))

The "Neutral" paragraph above accepted a known asymmetry: `SendDate` stored the UTC date at the moment of send, which silently drifted by a day for any org east of the date line (Tokyo, Sydney, etc.). Dedupe continued to work because each org's send window only opens for one UTC hour per local day, but analytics queries and the Resend webhook reader naturally expected SendDate to align with the org's wall clock. This amendment closes that drift.

### Decision

`ReminderLog.SendDate = DateOnly.FromDateTime(ToLocal(org.TimeZone, nowUtc))` — the org-local calendar day, not the UTC date at send time. The column type (`date`) and name are unchanged; only the value assignment moves from `nowUtc` to the already-computed `localDate` variable in the worker. `SentAt` (`timestamptz`) continues to store the precise UTC instant — there is no UTC↔local question for the instant column, only for the calendar-day column.

Migration `BackfillReminderLogSendDateToOrgLocal` rewrites existing rows via `(SentAt AT TIME ZONE org."TimeZone")::date`, joined through `Reminders → Organizations`. Safe wrt the unique index `(ReminderId, DocumentId, SendDate, RecipientEmail)`: the worker only fires once per org-local day, so any row's `SendDate` shifts by at most one calendar day and can never collide with another row sharing the rest of the tuple. The WHERE guard makes the SQL idempotent — re-running it after deploy is a no-op.

### Why this path over rename-to-`SendDateUtc`

The ticket offered two options: rename the column for clarity, or change the value. Both honor the dedupe invariant. The value-change path was chosen because:

1. Every consumer — analytics dashboards, the Resend webhook reader, the org's audit log — naturally reads "reminders sent on Jan 15" in the org's local calendar. `SendDateUtc` would have made the asymmetry explicit but kept the wrong default for every downstream query.
2. The project's broader datetime convention (per [CLAUDE.md](../../CLAUDE.md) §"Core patterns" and user direction in #24) is: store as UTC where there is a UTC representation (timestamptz columns), but the *semantic* should track the user's local wall clock. `SendDate` is a `DateOnly` so there is no UTC representation to preserve — only the semantic, which now matches.
3. Renaming the column would have required a no-op data change anyway (the migration would still need to backfill rows for orgs whose UTC date and local date diverge, or accept a permanent two-class schema).

### Consequences delta

#### Positive
- Analytics queries "show me reminders sent on Jan 15 for Tokyo orgs" now match the org's local calendar without per-row TZ math in the read path.
- The Resend webhook handler (`/api/reminders/resend-webhook`) is unaffected — it keys lookups by `ResendMessageId`, not by `SendDate`. Verified during #24.
- The dedupe invariant (one `(reminder, doc, day)` send per local day) is unchanged: the worker writes and queries with the same `localDate` variable, so write and lookup remain self-consistent.

#### Neutral
- The migration `Down` reverses the value to the UTC date of `SentAt` — but only as the formal inverse for `dotnet ef migrations script`. We don't expect to ever run it.

### Test coverage

- `Tokyo_org_records_send_date_as_local_calendar_day_not_utc` — the regression that motivated the change.
- `NewYork_org_records_send_date_as_local_calendar_day` — symmetry partner so a future "let's just use nowUtc" refactor fails both tests, not just the Tokyo one.
- `Tokyo_pre_existing_log_with_local_send_date_blocks_resend` — dedupe still holds when the pre-existing row was written with the new semantic.
- `Backfill_sql_rewrites_legacy_utc_send_date_to_org_local_for_tokyo_row` — the migration's SQL itself, verified against a Tokyo row in the pre-#24 shape and an NY row already in the new shape.

### References

- Ticket: [#24](https://github.com/neboxdev/complidrop/issues/24)
- Migration: `api/CompliDrop.Api/Migrations/20260525101534_BackfillReminderLogSendDateToOrgLocal.cs`
