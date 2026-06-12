# 0025. Reminder sends catch up within the org-local day; failed sends retry in place

- **Status:** accepted
- **Date:** 2026-06-12
- **Deciders:** Ruben G.

## Context

Reminder delivery rests on three layers decided in earlier ADRs: per-recipient dedupe via the
`(ReminderId, DocumentId, SendDate, RecipientEmail)` unique index
([ADR 0002](0002-reminder-dedupe-is-per-recipient.md)), the org-local `SendDate` semantic
([ADR 0007](0007-reminder-log-send-date-is-org-local.md)), and the trailing 26h `SentAt` guard for
editable-time-zone re-fires ([ADR 0015](0015-reminder-dedupe-time-zone-edit-guard.md)). Two
robustness gaps were deliberately deferred by those ADRs and are now resolved here
([#270](https://github.com/neboxdev/complidrop/issues/270), surfaced by the #236 promise-vs-reality
audit):

**1. A failed send is permanently deduped.** When Resend returns non-2xx, `EmailService.SendAsync`
returns null and the worker still writes the `ReminderLog` row with `Status = "failed"`. The
dedupe lookup selects on row *existence*, not status — ADR 0002's Neutral clause ("failed rows are
treated identically to sent for dedupe purposes; if we want intraday retries, that's a future
ADR") and ADR 0015's carry-over of the same clause both deferred the retry decision. The practical
consequence: a 30-second Resend outage at an org's 08:00 silently drops that day's expiry warnings
forever, visible only if someone browses Recent deliveries and spots "Couldn't send". That
contradicts the product's core promise ("we warn you before anything expires").

**2. A missed 08:00 window is skipped forever.** Sends fire only when an hourly tick lands inside
`local.Hour == 8`. A deploy, crash, or stuck instance during an org's 8 o'clock hour means that
day's reminders are never sent and never caught up — the next day computes different target dates.
The same one-tick-per-day assumption also means defect 1 has no natural retry vehicle: even if the
dedupe admitted failed rows, no later tick would qualify to retry them.

The two halves are interlocking — retry needs a later qualifying tick to run on, and a wider
window without retry still drops the day on a transient Resend failure (the failed row dedupes).
One decision covers both.

## Decision

### Half A — catch-up window: `local.Hour == 8` → `local.Hour >= 8`

`IsLocalSendWindow` now returns true for every org-local hour from 08:00 through 23:59. Each
hourly tick in that range processes the org for the **current org-local calendar day**
(`localDate`, hence `targetDate = localDate + DaysBefore` — unchanged derivation). Idempotency
comes from the existing dedupe layers: recipients already holding a non-failed log row for
`(reminder, doc, sendDate)` are skipped, so the extra qualifying ticks re-send nothing.

Catch-up is bounded at the org-local midnight **by design**: the next local day derives different
target dates, and a late send for *yesterday's* target would carry body copy ("expires in N days
— that's N days from today") that is no longer true. Cross-day catch-up would be a send-semantics
and template question, not a robustness patch; it stays out of scope.

This revises the **one-qualifying-tick-per-(org, local day)** premise stated as load-bearing in
ADR 0007's and ADR 0008's Context sections. Both *decisions* survive unchanged: every qualifying
tick of one org-local day derives the same `localDate`, so all of a day's sends still share one
`SendDate` (ADR 0007's invariant) and one advisory-lock key (ADR 0008's serialization unit). The
premise becomes "all qualifying ticks of a local day agree on `localDate`", which is what those
decisions actually needed.

### Half B — failed sends retry in place

The worker's pre-send dedupe lookup now counts only rows with `Status != "failed"` as "already
served" — in **both** arms of the ADR 0015 lookup (same `SendDate` and trailing 26h `SentAt`
guard). A `failed` row records an attempt that Resend never accepted; the recipient was not
served, so nothing exists to dedupe against.

On retry, a same-day `failed` row is **updated in place** (`SentAt` = attempt instant,
`ResendMessageId`, `Status`) rather than inserted anew — the unique index admits only one row per
`(reminder, doc, sendDate, recipient)`, and the row's identity *is* the natural idempotency key. A
failed row from an earlier `SendDate` (reachable only through the 26h guard arm) does not collide
with today's tuple, so that path inserts a fresh row for today and leaves the historical failure
untouched.

Only `Status == "failed"` retries. Every webhook-derived status (`delivered`, `bounced`,
`complained`, `opened`, `clicked`) and `sent` describe mail Resend **accepted**; in particular a
hard bounce or complaint must never be automatically re-sent (sender-reputation damage, exactly
the wrong response to a complaint).

Retry cadence is the catch-up window itself: one attempt per hourly tick while the org-local hour
is ≥ 8, ceasing at local midnight — at most ~16 attempts for a send that keeps failing, each one
HTTP call to Resend (failed attempts are not billed sends).

Retries make delivery **at-least-once**: a transport throw after Resend already accepted the
mail, or a crash between the accept and the per-doc save, leaves no usable record, and the next
tick re-attempts a send the recipient may already have received. Each send therefore carries a
deterministic Resend `Idempotency-Key` (24h server-side TTL) hashed from
`(ReminderId, DocumentId, SendDate, recipient)`: the re-attempt after an accepted-but-unrecorded
send recomputes the same key and is deduped at Resend instead of double-delivering. A retry after
a **recorded** failure salts the key with that attempt's `SentAt` — Resend's docs don't specify
whether error responses are cached against their key, and a genuine retry must never be served a
cached error — so the scheme is correct under either caching semantic.

This formally supersedes ADR 0002's Neutral clause ("failed rows are treated identically to sent
for dedupe") and ADR 0015's carry-over of it. The unique index, the per-recipient semantics
(ADR 0002), the org-local `SendDate` (ADR 0007), and the 26h guard's width and purpose (ADR 0015)
are all unchanged.

### Interaction with the same-PR window fix (#270 half 3)

The same PR fixes the document-match window to the **UTC calendar day of the stored face date**
(`ExpirationDate` is the document's face date at UTC midnight, per
`CanonicalDocumentFields.ParseUtcDate`); the previous org-zone conversion fired a day early for
every UTC-negative org. That is a bug fix against an unambiguous storage semantic, not a
decision — no ADR. One consequence worth recording: with UTC-day brackets, two qualifying ticks
on *different* org-local days now target *disjoint* UTC-day windows, so the original ADR 0015
re-fire trigger (same doc matching two local days' windows) is structurally unreachable; the 26h
guard is retained as defense-in-depth and still covers prior sends recorded on a different
`SendDate` within the window.

## Consequences

### Positive

- A transient Resend outage at 08:00 no longer costs an org its day's expiry warnings — the next
  hourly tick retries, and the history row heals from "Couldn't send" to "sent" in place.
- A deploy, crash, or stuck instance during the 08:00 hour no longer drops the day — any later
  tick that local day catches up. This also covers the latent DST edge where a zone transition
  could skip over the 08:00 wall-clock hour entirely.
- A document uploaded mid-day whose reminder target is that same local day now gets its reminder
  at the next tick (previously silently skipped — the 08:00 tick had already passed).
- The throw-path gap closes for free: when `SendAsync` *throws* (network fault), the worker writes
  no row at all; previously the single daily tick had already passed, now a later tick retries.
- No schema change, no migration, no new index.

### Negative

- Every hourly tick with org-local hour ≥ 8 now processes the org (~16 of 24 ticks instead of 1):
  one reminders query, one docs query per active reminder, one log lookup per matched doc, one
  advisory lock acquire/release. At MVP scale (tens of orgs, near-zero matched docs on most
  ticks) this is noise; if org count grows, add a cheap "unsent work exists" pre-filter before
  the per-org loop. Deliberately not built now.
- A send that keeps failing retries up to ~16 times that local day. Bounded, cheap (one HTTP call
  per attempt, no Resend billing for failures), and self-terminating at local midnight. An
  attempt-count cap was considered and rejected (Alternatives, Option A).
- `SentAt` on a `failed` row now records the **latest** attempt instant, not the first. The first
  failure's timestamp is not preserved (no attempt-history table at MVP); the Recent-deliveries
  page semantics shift from "when it failed" to "when it last failed", which is the more
  actionable reading anyway.
- An outage spanning an org's entire 08:00–24:00 local window still drops that day's reminders —
  the residual accepted risk. Mitigation remains the worker-heartbeat monitor (tech doc §15,
  Monitoring & Observability), not cross-day catch-up.
- Duplicate-send protection on the at-least-once paths leans on Resend honoring the
  `Idempotency-Key` header (24h TTL). If Resend ever dropped or shortened that guarantee, the
  worst case reverts to one duplicate email per accepted-but-unrecorded incident — bounded, and
  strictly better than the pre-#270 alternative of dropping the day.
- The #184 unverified-recipient warning stays gated to the org's 08:00 hour so it keeps its
  once-per-local-day cadence under the widened window; a day whose 08:00 tick was missed skips
  the warning entirely (the catch-up send still goes out — only the operator signal is skipped).

### Neutral

- A successful retry overwrites the `failed` status — the failure becomes invisible in Recent
  deliveries. That is the correct customer-facing reading (the reminder *was* delivered); operator
  visibility of the transient failure lives in structured logs. The #241 history work can add
  attempt counts if it proves to matter.
- The failed→sent transition is **not** recorded in the audit log: `AuditSaveChangesInterceptor`
  requires an authenticated principal's `OrganizationId` (it returns early without one), and the
  worker's service scope has no HttpContext — so no worker mutation, insert or heal, produces an
  `AuditLog` row (pre-existing behavior, unchanged here). The transition is visible in structured
  logs (the Resend non-2xx error log and the send-failure log). If system-actor audit rows are
  ever wanted, that is its own ticket, not a side effect of this one.
- Evening catch-up sends can disagree with the dashboard's day count for a few hours: the email
  computes "N days from today" in org-local terms while `daysUntilExpiry` on the documents API
  uses UTC dates (#263 convention). A NY send at 20:00 local (01:00 UTC next day) says "14 days"
  while the dashboard shows 13 until the UTC day catches up. The email is correct in wall-clock
  terms; aligning the display side is #263-family follow-up work, not a reminder bug.
- The pre-existing lost-webhook window (a Resend status event arriving between `SendAsync`
  acceptance and the per-doc save matches zero rows and is dropped with a 200) is unchanged by
  this ADR: the window count equals the send count, exactly as before the catch-up window.
  Returning non-2xx on zero-row matches to force Svix retries was considered and rejected — the
  same Resend account sends auth emails whose message ids legitimately have no `ReminderLog` row,
  and those would retry pointlessly forever.

## Alternatives considered

### Option A — attempt-count column with a retry cap

Add `AttemptCount` to `ReminderLog`, cap retries at N (e.g. 3). Rejected for MVP: a schema change
plus migration to bound something the local-day window already bounds at ~16 cheap HTTP calls.
Revisit if Resend rate limits or cost ever make unbounded-within-day retries a problem.

### Option B — separate retry queue / outbox table

Model sends as queue work items with explicit state transitions (the extraction-pipeline
pattern). Rejected: heavyweight for a once-a-day fan-out whose natural idempotency key — the
unique log row — already exists; it would duplicate the dedupe semantics ADR 0002/0007/0015
define on `ReminderLog`.

### Option C — delete the failed row and insert fresh on retry

Rejected: a delete+insert races the unique index under concurrent ticks (the advisory lock makes
this unlikely but ADR 0008 treats the index as defense-in-depth, which a delete would disarm
mid-flight), and it discards the failure from the audit trail. In-place update keeps the index
invariant continuously true.

### Option D — widen the window to a fixed band (e.g. hours 8–10) instead of open-ended

Rejected: still drops the day on any outage longer than the band, for zero added safety — the
dedupe layers make the open-ended window equally idempotent. The band only re-introduces a
smaller version of the same cliff.

## Test coverage

All in [ReminderBackgroundServiceTests](../../api/CompliDrop.Api.Tests/ReminderBackgroundServiceTests.cs):

- `Failed_send_is_retried_on_a_later_tick_and_heals_the_same_log_row` — the marquee Half B
  regression (replaces the pre-#270 `Failed_send_writes_a_failed_log_row_and_blocks_intraday_retry`,
  which codified the old behavior and named itself the canonical place to flip).
- `Failed_retry_that_fails_again_keeps_status_failed_and_updates_the_attempt_instant` — the
  retry loop's failure path stays a single row.
- `Non_failed_statuses_block_retry` (theory: bounced / complained / delivered) — webhook statuses
  count as served; hard bounces and complaints never re-send.
- `Only_the_failed_recipient_is_retried_not_the_already_served_one` — partial failure inside one
  (reminder, doc): the retry stays per-recipient.
- `Failed_row_within_the_26h_guard_window_does_not_suppress_a_send` — the failed-row exclusion
  applies to the guard arm too; the historical failed row (earlier `SendDate`) survives untouched
  while today's send inserts a fresh row.
- `Missed_08_window_is_caught_up_by_a_later_tick_the_same_local_day` — the marquee Half A
  regression.
- `Catch_up_tick_does_not_resend_what_08_already_sent` — idempotency of the widened window.
- `Before_local_08_nothing_is_sent` — the window still opens at 08:00, not earlier.
- `Catch_up_still_fires_at_the_last_hour_of_the_local_day` and
  `Yesterdays_missed_target_is_not_caught_up_the_next_local_day` — both edges of the window's
  close (open through 23:59; never across local midnight).
- `Window_fallback_for_unresolvable_time_zone_uses_utc_hours` — the `TryFindTimeZone == null`
  defensive branch carries the same catch-up semantic.
- `Send_throw_leaves_no_row_and_a_later_tick_retries` — the throw-path gap.
- `Unverified_recipient_warning_fires_only_during_the_08_hour` — the #184 operator signal keeps
  its once-per-day cadence under the widened window.
- `BuildSendIdempotencyKey_is_deterministic_and_varies_by_tuple_and_failure_salt`,
  `Reminder_sends_carry_a_deterministic_resend_idempotency_key`,
  `Retry_after_a_throw_reuses_the_predecessors_idempotency_key`,
  `Retry_after_recorded_failure_uses_a_fresh_idempotency_key` — the at-least-once dedupe scheme.

## References

- Ticket: [#270](https://github.com/neboxdev/complidrop/issues/270) (halves 1 and 2; half 3 — the
  UTC-day window fix — ships in the same PR without an ADR)
- Supersedes the failed-row Neutral clauses of [ADR 0002](0002-reminder-dedupe-is-per-recipient.md)
  and [ADR 0015](0015-reminder-dedupe-time-zone-edit-guard.md); leaves their decisions intact
- Companion: [ADR 0007](0007-reminder-log-send-date-is-org-local.md) (`SendDate` semantic,
  unchanged), [ADR 0008](0008-reminder-multi-instance-coordination-via-advisory-lock.md) (advisory
  lock, unchanged)
- Worker: `api/CompliDrop.Api/BackgroundServices/ReminderBackgroundService.cs`
