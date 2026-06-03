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
