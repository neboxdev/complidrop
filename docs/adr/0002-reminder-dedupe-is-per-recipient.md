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
- `ReminderLog.SendDate` continues to store the UTC date, not the org-local date. Dedupe works because each org's send window only opens for one UTC hour per local day, so all logs for one local day share one `SendDate`. Analytics queries that expect SendDate to align with the org's local day will be off by a day for non-US zones — also a future ADR if it matters.

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
