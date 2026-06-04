# 0015. Reminder dedupe carries a trailing-window guard for editable-time-zone re-fires

- **Status:** accepted
- **Date:** 2026-06-04
- **Deciders:** Ruben G.

## Context

Reminder dedupe ([ADR 0002](0002-reminder-dedupe-is-per-recipient.md)) keys on the unique index
`(ReminderId, DocumentId, SendDate, RecipientEmail)`, where `SendDate` is the **org-local calendar
day** ([ADR 0007](0007-reminder-log-send-date-is-org-local.md)). Both ADRs lean on the same
load-bearing assumption, stated explicitly in ADR 0002's closing consequence and ADR 0007's
Decision: *each org's send window opens for exactly one UTC hour per local day*, because
`ReminderBackgroundService.IsLocalSendWindow` gates on `local.Hour == 8` and — for a **fixed** zone —
local 08:00 occurs once per 24h. Under that assumption all of a local day's sends share one
`SendDate`, so the per-recipient key fully dedupes.

[#185](https://github.com/neboxdev/complidrop/issues/185) made the org time zone **user-editable**
(previously write-once at signup). That breaks the one-window-per-day assumption. An owner who edits
the zone in the narrow band around their local 08:00 can make **two** hourly ticks both satisfy
`IsLocalSendWindow` on **different** org-local calendar days within a single ~24h span. Worked
example (the regression test):

- Org `America/New_York`. Tick at **13:00 UTC Jan 15** is NY-local 08:00 Jan 15 → `SendDate = Jan 15`.
  A document expiring at 20:00 UTC Jan 15 (NY-local Jan 15 afternoon) is inside the org-local target
  window → reminder sent.
- Owner switches the zone to `Asia/Tokyo`.
- Tick at **23:00 UTC Jan 15** is Tokyo-local 08:00 **Jan 16** → `SendDate = Jan 16`. The same
  document (20:00 UTC Jan 15 = Tokyo-local Jan 16 05:00) is inside *that* day's target window too →
  reminder sent **again**.

The two ticks record different `SendDate` values (`Jan 15` vs `Jan 16`), so the unique index does not
collide and the worker's same-day `alreadySent` set does not match. The same document's reminder
reaches the same recipient twice. The per-`(org, localDate)` advisory lock
([ADR 0008](0008-reminder-multi-instance-coordination-via-advisory-lock.md)) does not help either —
the two ticks derive **different** lock keys from their different localDates, so they never contend.

Impact is bounded (a duplicate reminder email — Resend cost plus a small trust ding, not data loss)
and the trigger is narrow (an admin editing the zone precisely around their 08:00). But it is a real
double-send newly enabled by #185, and the dedupe contract is exactly the place the project promises
it cannot happen. Surfaced by the #180 batch re-review (correctness reviewer); tracked as
[#205](https://github.com/neboxdev/complidrop/issues/205).

## Decision

Keep `SendDate` and the unique index exactly as ADR 0002 / 0007 define them. Widen **only** the
worker's pre-send dedupe lookup, from "same `SendDate`" to "same `SendDate` **OR** a prior send of
this `(ReminderId, DocumentId, RecipientEmail)` whose `SentAt` falls within a trailing **26-hour**
guard window":

```csharp
var tzEditGuardStart = nowUtc - TzEditDedupeWindow;   // TzEditDedupeWindow = 26h
var alreadySent = (await db.ReminderLogs
    .Where(l => l.ReminderId == reminder.Id
                && l.DocumentId == doc.Id
                && (l.SendDate == sendDate || l.SentAt >= tzEditGuardStart))
    .Select(l => l.RecipientEmail)
    .ToListAsync(ct))
    .ToHashSet(StringComparer.OrdinalIgnoreCase);
```

The `SentAt` instant (`timestamptz`) is the precise, **zone-independent** UTC moment of the prior
send — unlike `SendDate`, editing the zone cannot move it. So the guard recognises the earlier send
no matter which local calendar day the second tick believes it is on.

**Why 26h is the right window.** The two qualifying ticks are provably **<24h apart in UTC**. Each
tick fires at a fixed offset from its ~24h org-local expiration window (`localDate 08:00 local`,
which is `DaysBefore` days − 8h before that window's start, *in local terms* — and that local-to-UTC
offset cancels because the same zone converts both the tick and the window). For the **same**
document to be selected in both ticks, the document's single fixed-UTC expiration instant must lie
inside **both** ~24h windows, so both window-starts — and therefore both ticks — fall within one 24h
span. 26h covers that **<24h tick-gap** — the operative, load-bearing bound — with a safety margin.
(It coincidentally also equals the maximum IANA UTC-offset span, UTC+14 Kiritimati … UTC−12 Baker
Island; that span is the most a zone edit can shift the org-local calendar day, but it is a secondary
sanity check, not the bound the window is sized against — the tick-gap is.) A reminder's legitimate
cadence for one document is **once** (a fixed
expiration is `DaysBefore` away on exactly one local day; renewals create a new `Document` row with a
new id — `Document.ExpirationDate` is written only by the extraction pipeline, never edited in
place), so a 26h window cannot suppress a genuinely distinct future occurrence.

This is a deliberate **additive** revision of ADR 0002's dedupe procedure: the unique index and the
per-recipient semantics are unchanged; the worker simply consults a slightly wider set of prior log
rows before deciding a recipient is already served. ADR 0007's `SendDate` semantic is untouched —
the column still stores the org-local calendar day for analytics.

## Consequences

### Positive

- Editing the org time zone can no longer double-send a document's reminder to the same recipient
  within ~24h — the bug #205 was filed for, closed at the dedupe layer where the contract lives.
- The guard is a single OR-clause on an existing query: no schema change, no migration, no new index,
  no change to the write path, and the column semantics of ADR 0002 / 0007 are preserved verbatim.
- The fix keys on `SentAt`, the one reminder-log timestamp a zone edit provably cannot perturb, so it
  is robust to *any* offset change, not just the New York → Tokyo direction in the worked example.

### Negative

- The cross-day case is enforced by a **query-time** check, not the hard unique index (which cannot
  express "within 26h"). It deliberately does **not** rely on the ADR 0008 advisory lock — the two
  qualifying ticks derive *different* per-`(org, localDate)` lock keys (Jan 15 vs Jan 16 in the worked
  example), so the lock never serialises them. Safety comes instead from the inter-tick **time gap**:
  the two ticks fire at **different UTC hours** (a single UTC instant resolves to one local hour, so
  only one localDate qualifies per tick) and are therefore ≥1h apart (in practice hours apart, since
  both must bracket one fixed-UTC expiration instant). Under read-committed that gap guarantees the
  earlier tick's `ReminderLog` row is committed and visible to the later tick's lookup before it runs —
  on the same replica **or any other** — so the guard is multi-replica-safe without holding a lock
  across the two ticks. The same-day multi-replica race (two ticks, *same* localDate, same UTC hour)
  remains covered by the advisory lock + unique index exactly as before.
- A pathological re-extraction that mutated an existing `Document.ExpirationDate` by ~1 day *and*
  landed inside the 26h window could be suppressed. This is not a reachable product flow today
  (expiration is set once by the pipeline; there is no in-place edit endpoint), and the pre-#205
  same-day dedupe already had the identical, narrower exposure — so the change widens an
  already-accepted edge from "same day" to "~26h" rather than introducing a new class of suppression.

### Neutral

- `ReminderLog.Status = "failed"` rows continue to count as "already served" for dedupe (carried over
  from ADR 0002) — now within the 26h window as well as same-day. Intraday/cross-day retry of failed
  sends remains a separate future decision.

## Alternatives considered

### Option A — dedupe on the target UTC window / expiration instant instead of `SendDate`

Re-key dedupe off something stable under zone edits (the document's expiration instant or the target
UTC window) rather than the editable org-local `SendDate`. Rejected for MVP: it reopens the ADR 0002
key decision and the ADR 0007 analytics semantic for a narrow edge, and the natural "dedupe identity"
is still `(reminder, doc, recipient)` — which the trailing-window lookup already keys on without
disturbing the stored column or the index.

### Option B — adjacency on `SendDate` (`SendDate BETWEEN sendDate − N AND sendDate + N`)

Stay entirely in calendar-day space and treat adjacent days as duplicates. Rejected: it requires
reasoning about how many calendar days an offset change can span (up to **2** days for the extreme
UTC+14 ↔ UTC−12 edit, not 1), so the safe bound is murkier than the physical "<24h apart" bound that
`SentAt` gives directly; and it leans on `SendDate`, the very value the zone edit corrupts.

### Option C — accept and document the edge in ADR 0007

The third option #205 floated. Rejected: the acceptance criterion is *"editing the org time zone
cannot cause the same document reminder to be sent twice to the same recipient within ~24h."* A
documented-but-unfixed double-send does not satisfy it.

## Test coverage

All in [ReminderBackgroundServiceTests](../../api/CompliDrop.Api.Tests/ReminderBackgroundServiceTests.cs):

- `Editing_time_zone_around_local_08_does_not_double_send_the_same_reminder` — the marquee
  regression: NY tick sends once (`SendDate = Jan 15`), the org switches to Tokyo, the next-day Tokyo
  tick (`SendDate = Jan 16`) for the same document + recipient is suppressed. Verified to fail (two
  sends, two rows) without the guard and pass (one send, one row) with it.
- `Tz_edit_guard_suppresses_a_prior_send_only_within_the_26h_window` (theory, 25h/27h/48h) — pins the
  window **width**: a prior send on an earlier `SendDate` (so only the new `SentAt` arm can match) is
  suppressed iff its `SentAt` is inside the 26h window. Catches a shrink or grow of the constant, and
  pins the lower bound (a stale send never wedges a `(reminder, doc, recipient)` forever).
- `Tz_edit_guard_is_per_recipient_a_recent_send_to_one_does_not_suppress_another` and
  `Tz_edit_guard_is_per_document_a_recent_send_for_one_doc_does_not_suppress_another` — the widened
  clause stays scoped: a recent send to one recipient (or for one document) does not suppress another.
- `Editing_time_zone_suppresses_the_re_fire_for_both_internal_and_vendor_recipients` — the
  multi-recipient end-to-end (the vendor case is the costly one).
- `Editing_time_zone_across_an_extreme_offset_jump_still_dedupes_a_two_day_send_date_shift` —
  Pago_Pago → Kiritimati pushes `SendDate` **two** calendar days while the ticks stay 23h apart in
  UTC; the `SentAt` key catches it where a ±1-day `SendDate`-adjacency guard (Option B) would miss it.
- `Editing_time_zone_within_the_same_local_day_still_dedupes_via_the_same_day_key` — the widening is
  additive: a same-local-day zone edit (NY → Chicago) is still deduped by the unchanged `SendDate` arm.

## References

- Ticket: [#205](https://github.com/neboxdev/complidrop/issues/205) (latent edge enabled by
  [#185](https://github.com/neboxdev/complidrop/issues/185); surfaced in the #180 batch re-review)
- Extends: [ADR 0002](0002-reminder-dedupe-is-per-recipient.md) (dedupe key) and
  [ADR 0007](0007-reminder-log-send-date-is-org-local.md) (org-local `SendDate`); interacts with
  [ADR 0008](0008-reminder-multi-instance-coordination-via-advisory-lock.md) (advisory lock is keyed
  per org-local day and so does not cover this cross-day case)
- Worker: `api/CompliDrop.Api/BackgroundServices/ReminderBackgroundService.cs`
  (`TzEditDedupeWindow`, the `alreadySent` lookup)
