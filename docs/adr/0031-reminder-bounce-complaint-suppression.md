# 0031. Reminder bounce/complaint suppression — per-(org, email), complaint permanent, hard bounce only

- **Status:** accepted
- **Date:** 2026-06-23
- **Deciders:** Ruben G. (founder), Claude (implementing #340)

## Context

A bounce or complaint was recorded **only on the specific `ReminderLog` row** the Resend webhook matched (`ResendWebhook` set that row's `Status` to `bounced`/`complained`). There was **no address-level suppression** anywhere. So:

- The reminder worker (`ReminderBackgroundService`) kept firing **future** reminders (other documents, later expiry cycles) into an address that had hard-bounced or filed a spam complaint — there was no check against prior bounces/complaints when building each send's recipient list.
- The real product harm was **no operator visibility**: the #184 dead-letter warning fires only for *unverified internal* recipients at 08:00, so a dead **vendor** address produced no signal. The product's core loop — remind the vendor → they upload the renewal — failed **silently**: the venue never learned the vendor's email was dead, and the document expired uncollected. (The class epic #235 targets: "a reminder that fires twice or never.")
- Continuing to send to a spam-complainer is a CAN-SPAM / sender-reputation hazard, amplified by the shared sending domain.

Found by the #235 hunt — external-integration audit (#245), filed as #340. The owner refinement split the two negatives: a **bounce** is a *deliverability* signal (Resend's account-level list backstops actual delivery; the app harm is missing visibility); a **complaint** is an affirmative *consent opt-out* and the higher-priority half.

## Decision

**Persist a per-`(OrganizationId, Email)` `EmailSuppression`, written by the webhook, checked by the worker, and surfaced on the vendor.**

**Policy:**
- **Complaint (`email.complained`)** → suppress, reason `Complained`. An affirmative opt-out — always suppress, permanent.
- **Hard bounce (`email.bounced` with `data.bounce.type == "Permanent"`)** → suppress, reason `Bounced`. The address is durably undeliverable.
- **Transient / Undetermined / unclassified bounce** → **no** address-level suppression. The `ReminderLog` still records the `bounced` status, but a soft bounce self-recovers and is left to Resend's own retry; we never over-suppress on an ambiguous signal.
- **Never downgrade:** a later bounce for an already-`Complained` address leaves it `Complained` (the reason enum is ordered `Bounced < Complained`).

**Scope: per-org**, not global. The org is the sender relationship, this matches CompliDrop's tenant-scoped data model, and Resend's own **account-level** suppression list is the global delivery backstop. So the app-level suppression layers visibility + "don't even try" on top of Resend's guarantee, without a cross-tenant data model.

**Permanence / recovery: no un-suppress UI in v1.** The natural recovery is the operator giving the vendor a **new** contact email — a different address has no suppression, so reminders resume to it automatically. A complaint should never auto-clear (permanent opt-out); a recovered same-address mailbox is a rare edge an explicit "resume" action can address later if needed.

**Worker** (`ReminderBackgroundService.ProcessHourlyTickAsync`): loads the org's suppressed addresses once per org-tick and **skips** a suppressed recipient (no send, no `ReminderLog` row, no cost) — the dead address is already surfaced elsewhere.

**Operator visibility:**
- The webhook writes a system `reminder.recipient_suppressed` **audit/feed event** on first suppression (the webhook has no current user, so the interceptor can't, mirroring `PersistSuccess`'s explicit `document.processed` row).
- The vendor endpoints expose `contactEmailStatus` (`null` | `"bounced"` | `"complained"`), and the UI badges it: a clear "reminders are paused — update the contact email to resume" alert on the vendor **detail**, and a scannable "Bounced" / "Spam report" badge in the vendor **list**.

## Consequences

### Positive
- The engine stops firing into known-dead / opted-out mailboxes — the silent-failure class #340 targets.
- A dead **vendor** address is now visible to the operator (detail alert + list badge + feed event), not buried on a `ReminderLog` row — so the venue can fix the email and actually collect the renewal.
- Complaints are honored as permanent opt-outs (CAN-SPAM / reputation posture), the owner's higher-priority half.

### Negative
- A persistently-Undetermined-bouncing address keeps being emailed (no escalation), because we suppress only `Permanent` bounces. Accepted: Resend usually classifies, over-suppression is worse for legit reminders, and Resend's account list still backstops delivery.
- No un-suppress UI: a recovered same-address mailbox stays suppressed until the operator changes the contact email. Accepted v1 simplification (see Decision).
- One extra small query per org-tick (the suppression set) and per vendor-list/detail read (the badge) — negligible (the suppression set is tiny; the list loads it once and matches in memory).

### Neutral
- New additive `EmailSuppression` table + `(OrganizationId, Email)` unique index (migration `AddEmailSuppression`). `Email` stored lowercased for case-insensitive matching; the entity is in `AuditSaveChangesInterceptor`'s non-audited set (the *event* is audited explicitly).
- Bounce classification depends on Resend's `data.bounce.type` payload; parsing fails safe (absent/unknown → not permanent → no suppression), so a payload-shape change degrades to complaint-only suppression, never over-suppression.

## Alternatives considered

### Option A — Global (cross-tenant) suppression
Suppress an address across **all** orgs on a complaint/bounce. Strongest for shared-domain reputation. **Rejected** as the data model: it breaks tenant isolation (org A's vendor complaint would silence org B's reminders to the same address — a different sender relationship), and Resend's account-level list already provides the global delivery backstop. Per-org + Resend's global list covers both concerns.

### Option B — Suppress on every bounce (no hard/soft distinction)
Simpler webhook (no `bounce.type` parsing). **Rejected**: a transient bounce (mailbox full, greylisting) self-recovers; suppressing it would permanently stop legit reminders to a momentarily-unavailable address. Suppress only `Permanent`.

### Option C — A `Vendor.EmailStatus` column instead of a suppression table
Store the status on the vendor. **Rejected**: reminder recipients include **internal org users**, not just vendors, so suppression must key on `(org, email)` to cover both; a vendor column can't. The table also keeps provenance (`SourceReminderLogId`) and an upgrade path (bounce → complaint).

### Option D — Block the send but keep writing a `ReminderLog` row / "visibly flag" in place
The acceptance allowed "skip **or** visibly flag." **Chose skip** (no send, no row): a row per suppressed-tick would accrete noise, and the visibility is better served by the durable vendor badge + the one-time feed event than by repeated skipped-log rows.

## References

- Tickets: [#340](https://github.com/neboxdev/complidrop/issues/340), [#245](https://github.com/neboxdev/complidrop/issues/245) (audit), [#235](https://github.com/neboxdev/complidrop/issues/235), [#184](https://github.com/neboxdev/complidrop/issues/184) (dead-letter visibility), [#48](https://github.com/neboxdev/complidrop/issues/48)
- ADRs: [0025](0025-reminder-catch-up-window-and-failed-send-retry.md) (reminder retry / `ReminderLogStatus`)
- Code: `Entities/EmailSuppression.cs`, `Endpoints/ReminderEndpoints.cs` (`ResendWebhook`, `RecordSuppressionAsync`), `BackgroundServices/ReminderBackgroundService.cs`, `Endpoints/VendorEndpoints.cs`, `frontend/src/app/(dashboard)/vendors/*`
- Audit: `docs/audits/integrations-2026-06-22.md` §Resend
