# 0013. Account deletion is soft-delete + PII scrub, not hard delete

- **Status:** accepted
- **Date:** 2026-06-03
- **Deciders:** Ruben G.

## Context

[#183](https://github.com/neboxdev/complidrop/issues/183) added self-serve account deletion (`POST /api/auth/account/delete`) as a GDPR/CCPA "right to erasure" affordance. The ticket is `careful-review` and flagged "recommend `/plan`" precisely because *how* an account is deleted is a data-semantics decision with compliance, billing, and audit-trail consequences — not a mechanical choice.

The codebase already has an established soft-delete convention: the `AuditSaveChangesInterceptor` translates `Remove()` into a `DeletedAt` timestamp for soft-deletable entities (`Organization`, `User`, `Vendor`, `Document`, `ComplianceTemplate`), and both `AppDbContext` and `SystemDbContext` apply a `DeletedAt == null` query filter. The reminder + extraction workers gate on `Organization.DeletedAt == null`. A genuine hard delete (`ExecuteDeleteAsync`) would bypass the interceptor (no audit of the deletion) and cascade-remove all child rows including the audit trail.

Two questions had to be resolved:

1. **Soft-delete vs hard-delete** — does "erasure" mean removing the rows, or tombstoning them?
2. **What is the scope of "erasure"** — which PII is actually removed vs retained?

## Decision

Account deletion is **a password-confirmed soft-delete of the user + organization, plus a scrub of the account holder's PII**, performed in a single `SaveChanges`:

- The **user's PII is scrubbed**: `Email → deleted+{userId:N}@deleted.invalid`, `FullName → "Deleted account"`. The scrub is set as ordinary property updates alongside a manual `DeletedAt` (not via `Remove()`) so all three land in one `UPDATE`.
- The **organization is soft-deleted** (`DeletedAt` set). Its query filter then hides the org and, because every authenticated path resolves data through `CurrentOrgId`, makes the tenant's data inaccessible.
- **Access is revoked**: the soft-deleted user is invisible to the login lookup and `/me`, so the account can no longer authenticate. Outstanding reset/verification tokens become unusable (their `User` navigation filters to null). The caller's auth cookies are cleared.
- The scrubbed-unique email **frees the original address for re-registration**.
- An explicit `user.account_deleted` audit event is written **before** the scrub, capturing the deletion with the intact identity (the `PasswordHash` is redacted from all audit snapshots — see the interceptor's `RedactedProperties`).

### Why soft-delete + scrub over hard-delete

- **Audit-trail retention.** A merged-PR audit log of "who deleted what, when" survives. Hard delete via `ExecuteDeleteAsync` bypasses the interceptor and would erase that record.
- **Billing / reconciliation.** Stripe webhooks and subscription reconciliation may still reference the org after deletion; a tombstone is safer than a dangling FK cascade.
- **Reversibility by support** during the MVP window (an accidental self-deletion can be undone by clearing `DeletedAt`), while the PII scrub means the actual personal data is already gone.
- **Simplicity + consistency** with the existing soft-delete convention used everywhere else.

## Consequences

- **Scope of erasure is bounded (MVP).** Only the **account holder's** PII (email, name) is scrubbed. Explicitly **retained**: child rows (`Vendor.ContactEmail` / `ContactPhone`, `Document.OriginalFileName`, `Reminder`, `ReminderLog`, `Subscription`) and the uploaded **blob files in Azure**. These are hidden (org soft-deleted) but not purged or scrubbed. For a stricter "full erasure" obligation (e.g. a formal DPA, or third-party-vendor PII removal) a follow-up hard-purge job would be required. This is a deliberate MVP boundary, documented here so a future privacy-policy/DPA and the next person writing deletion code inherit the rationale.
- A password reset/change does **not** currently evict existing JWT sessions (stateless JWT, no security stamp). That is a separate, tracked gap (see the #183 follow-up `bug` ticket) — orthogonal to deletion, which *does* revoke access by soft-deleting the user.
- The single verification-token entity (`EmailVerificationToken`) now serves two flows — signup verification (#184) and change-email confirmation (#183, via a nullable `NewEmail`) — keyed on `NewEmail`. Recorded here so the dual purpose is discoverable beyond the property comment.
- Re-registration with a previously-deleted email works (the scrub frees it), which is the desired UX.

Superseded only by a future ADR if the erasure scope is widened to a hard purge.

---

## Amendment 1 (2026-08-07) — the user-facing copy now matches this decision, and the retention question goes to counsel

[#398](https://github.com/neboxdev/complidrop/issues/398), a deep privacy/legal-accuracy review. **No
behaviour changes here.** Every clause of the Decision above still holds; what changed is that the
product stopped telling customers the opposite of it.

### What was wrong

The Settings danger zone read *"Permanently delete your account and organization data. This can't be
undone."* with a **Permanently delete** button, and `/privacy` promised that after closure *"we delete
or de-identify your data within a reasonable period"*. Measured against this ADR:

- **"Permanently … delete … your organization data"** — false. § Consequences above lists the retained
  set: `Vendor.ContactEmail` / `ContactPhone`, `Document.OriginalFileName`, `Reminder`, `ReminderLog`,
  `Subscription`, **and every uploaded blob in Azure**. Only the account holder's email and name are
  scrubbed.
- **"This can't be undone"** — false, and this ADR is where it says so: *"reversibility by support …
  an accidental self-deletion can be undone by clearing `DeletedAt`"* is listed as a **benefit** of
  the decision. The copy asserted the opposite of a designed-in property.
- **The policy's retention promise had no implementing mechanism.** There is no purge job anywhere in
  the codebase. Nothing runs after closure; the data is hidden by query filters, not deleted.

"We delete your data" against actual retention is a well-worn FTC Act §5 deception pattern, and it
poisons any CCPA §1798.105 delete response. That — not the retention itself — is the harm, which is
why the fix is copy.

### The decision this amendment records

**Option (a): make every deletion claim true; route the retention posture to the counsel gate.** The
alternative on the table was (b) build a post-closure hard purge (blobs + vendor PII after 60–90
days). Rejected here, for this ticket: (b) **reverses this ADR** rather than amending it, needs a
retention schedule nobody has set, and needs the same attorney sign-off the wording needs — while the
deception it would address is removed completely and immediately by (a). The precedent is
[ADR 0047](0047-exports-carry-a-non-advice-disclaimer.md) (CLM-3): ship the truthful wording now,
default-on and behind no flag because a claim that overstates deletion is a defect whichever way a
flag sits, and mark it provisional pending the attorney pass.

The retention question is **open, not answered** — `G1-COUNSEL-BRIEF.md` §0 + §C **CLM-7**: *build a
post-closure purge and publish a retention schedule, or is retention-with-deletion-on-request the
posture?* Deliberately **no period is stated anywhere**: publishing a number no job enforces would
recreate this defect in a new sentence.

### What the copy now says, and what makes each clause true

| Surface | Traceable to |
|---|---|
| "Closing signs you out for good and clears your name and email from your account record." | `AuthEndpoints.DeleteAccount` — the `Email` / `FullName` overwrite + `DeletedAt`; the login lookup and `/me` filter the soft-deleted user out. Scoped to *your account record* on purpose: the `user.account_deleted` audit row keeps the intact email by design (see the Decision above), so "removes it from our records" would have been the same class of overstatement. |
| "If you have a paid plan, it will be canceled — no new charges will start." | Unchanged (#255). `CancelSubscriptionAsync` runs **before** any local change and a failure aborts the whole request with a 502/503 — the one clause of the old copy that was already true. |
| "Your vendors, documents, the files uploaded for them, and our record of account activity are kept…" | § Consequences above, verbatim. The trailing "…as described in our **Privacy Policy**" is a link (round 2): the dashboard shell renders no footer and no legal links, so the deferral pointed somewhere a signed-in customer could not reach — the § Alternatives rejection of "leave it to `/privacy`" applies one step along. |
| `/privacy`: "…we clear your name and email from your account record straight away, cancel any paid plan, and stop sending reminders." | The scrub, the Stripe cancel, and `ReminderBackgroundService`'s `o.DeletedAt == null` org filter. |
| `/privacy`: "We have not set a fixed disposal period for those records, so they stay with us until you ask us to delete them." | True by absence — no purge job exists. The request channel is the one § "Your choices and rights" already offers. |
| "This removes the document from your records. You won't be able to undo it." | `DeleteDocument` soft-deletes and **retains the blob** so the document *"remains recoverable"* (its own comment). The claim is scoped to the customer, who has no restore affordance anywhere in `frontend/`. |
| Audit label "Account closed" (round 2) | The `user.account_deleted` audit ACTION key is a stored value and keeps its name; its LABEL prints in the dashboard activity feed and the exported audit PDF (`ExportService` → `DisplayLabels.Action`), and it becomes readable again on exactly the support path § Consequences names as a benefit — so it would have reported a deletion for an account nothing deleted. Every sibling soft-delete already read "removed". |

Two further surfaces carried the same family and moved with it: the vendor removal dialog (same
soft-delete, same scoping), and the Settings data-export card, which offered *"a JSON copy of your …
documents"* for an export (`AuthEndpoints.ExportAccount`) that serializes document **rows** —
`OriginalFileName`, `DocumentType`, `ExpirationDate`, `ComplianceStatus`, `CreatedAt` — and no files
at all. `ExportAccount` itself is unchanged; only its description is.

The export card took a **second** pass in round 2 of the review, and it is worth recording why: the
first replacement said the dump held *"the details we hold for each document"*, which is a different
over-claim in the same family. The product's own word for what it holds per document is
**"Extracted fields"** (the detail page's heading), and `DocumentField` / `Document.ExtractionFields`
— along with the `ComplianceCheck` results and the `AuditLog` — appear nowhere in `ExportAccount`.
So the rule this ticket ends on is sharper than "don't say delete": **a vaguer word is not a fix.**
The card now enumerates the five columns and states the omissions outright.

### Consequences

- The document and vendor removal notices are **single-sourced** in
  `frontend/src/lib/removal-copy.ts`, for ADR 0047 §1's reason: the document sentence shipped as two
  hand-copied literals (list row + detail header), so a CLM-7 reword would have landed on one of them.
- The retention disclosure is deliberately **not** repeated in every destructive dialog. It belongs to
  `/privacy` § "How long we keep it" and to the account-closure card, where the customer is deciding
  about all of it at once; a paragraph on every per-item confirm is noise that gets skimmed.
- The deletion claims are now guarded by a **census**, not only per-page assertions — the same
  sentence sat on four surfaces, and the fifth is a dialog on a page nobody has written yet. Its
  reach is stated exactly, because round 2 of the #398 review found the claim still live on
  surfaces the first records implied were covered: **one rule table, two walks, plus direct
  assertions where no walk can read.** The table is
  `api/CompliDrop.Api.Tests/SharedFixtures/deletion-claim-rules.json` (ADR 0038's ContactEmail
  arrangement — the two hand-maintained copies were already unequal, 7 rules against 5, while the
  records called them "the same census"); `frontend/src/test/marketing-claims.test.ts` runs it over
  `frontend/src/**` + the repo README, `api/CompliDrop.Api.Tests/DeletionClaimCensusTests.cs` runs
  it over `api/CompliDrop.Api/**/*.cs`, which is the only walk that can see a SERVER message — and
  a server message is where both of round 2's confirmed majors were. Both are SOURCE scans catching
  a KNOWN list: backstops, blind to copy assembled at runtime, to a rendered page, and to a display
  label, which is a map value rather than prose. Those last are asserted directly instead
  (`user.account_deleted` → "Account closed" in both mirrors, the closure endpoint's three
  messages, the closure success toast).
- **The retention itself is unchanged and still unbounded.** Nothing here reduces what CompliDrop
  holds after a closure; it only stops the product from claiming otherwise. If counsel answers CLM-7
  with "purge", that work supersedes this ADR's § Decision and needs its own.

### Alternatives considered

- **Build the purge now (the ticket's option (b))** — rejected above: reverses a standing decision,
  invents a schedule, and still needs the sign-off.
- **Keep "Delete account" as the label and fix only the surrounding prose.** Rejected: *delete* is the
  representation the finding turns on, and a **Delete** button beside "your records are kept" reads as
  the fine print contradicting the button — the shape regulators treat as the deception, not the cure.
- **State a schedule now and build the job later.** Rejected: it is this exact defect, re-shipped in a
  sentence with a number in it.
- **Say nothing about retention in Settings and leave it to `/privacy`.** Rejected: the closure screen
  is where the decision is made, and a policy nobody opens is where the original claim already hid.
