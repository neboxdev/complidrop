# 0044. Clamp client-controlled audit input at one boundary; replace an unusable trace id

- **Status:** accepted
- **Date:** 2026-07-25
- **Deciders:** Ruben G. (founder), Claude (implementing #372)

## Context

Three values on every request are chosen by the CLIENT and land in bounded `AuditLog` columns: the
inbound `User-Agent` header (`varchar(500)`), the inbound `X-Trace-Id` header (`CorrelationId`,
`varchar(64)`), and the connection's remote address (`IpAddress`, `varchar(64)`). Nothing bounds any
of them at the source — a header can be any length a client cares to send.

Npgsql does not truncate. An over-length string written to a bounded column fails the **whole**
`SaveChanges` with Postgres `22001`, and the audit row is deliberately added to the SAME unit of work
as the business mutation (`AuditSaveChangesInterceptor`) or committed inside the endpoint's own
transaction (`IAuditLogger`). So a long header did not merely lose an audit row — it took the
mutation down with it as an unhandled `DbUpdateException` → 500. Four concrete instances — the
first three are routes where an over-length `User-Agent` did it, the fourth is the other header
reaching the same insert:

1. **The PUBLIC portal upload.** `POST /api/portal/{token}/upload` is unauthenticated, so a THIRD
   party controls the header. Its `audit.LogAsync("vendorPortalLink.upload_processed", …)` sits
   inside an explicit transaction, between the `ExecuteUpdateAsync` that burns a PAID upload permit
   and the `CommitAsync`. The 22001 rolled that transaction back after the blob had already been
   uploaded, and handed the vendor a 500 they could not retry out of.
2. **Audit suppression on failed login.** The lockout increment commits in its own earlier
   `SaveChanges`, so the follow-up `user.login_failed` insert threw on its own: the attempt still
   counted against the account, but vanished from the audit trail. An attacker could erase their own
   failed-login evidence by sending a long `User-Agent`.
3. **Any authenticated mutation**, e.g. a corporate proxy appending product tokens to the UA.
4. **The same 22001 through `X-Trace-Id`.** `AuditLog.CorrelationId` is `varchar(64)`, and the
   inbound header was stored verbatim, so a 65-character `X-Trace-Id` failed the very same audit
   insert — on any of the three routes above — with no over-length `User-Agent` involved at all.

A second, independent problem sat on the same value. The whole of `CorrelationIdMiddleware`'s
handling of the inbound header was:

```csharp
var correlationId = context.Request.Headers[HeaderName].FirstOrDefault()
    ?? Guid.NewGuid().ToString("N");
```

A present header was echoed and stored **verbatim, at any length and with any characters**; an id was
minted only when the header was absent. There was no length check (hence vector 4), no charset check
and no blank check — a blank header blanked the echoed response header, and a CR/LF in it was header
injection.

That one value then fans out: it is echoed back in the response header, becomes `error.correlationId`
in the error envelope and `ApiError.correlationId` in the frontend, and is shipped to Sentry as the
`correlation_id` tag by `tagCorrelationId`
([ADR 0037](0037-frontend-sentry-pii-scrubbing-and-gating.md)) — which by deliberate design runs
**after** `scrubEvent` and does **not** redact the tag, on the premise that a correlation id is an
opaque identifier rather than user content. So any client could put arbitrary text — an email
address, a customer name, a phone number — straight into that un-redacted tag and break ADR 0037's
invariant at its source. (ADR 0037's own wording called the id "server-minted", which this
header-honoring path had never made true.) The same arbitrary id is also the activity-feed collapse
key (`(CorrelationId, EntityType, EntityId)` in `DashboardEndpoints`), so a client repeating one id
could merge two distinct events into a single feed row.

## Decision

### 1. Clamp at the single `ICurrentUser` boundary, not per sink

`CurrentUserService.IpAddress` / `.UserAgent` / `.CorrelationId` pass through
`ColumnClamp.To(value, AuditColumnLengths.X)` — the ONE surrogate-safe truncation in the codebase.
Both audit writers (`AuditSaveChangesInterceptor` and `AuditLogger`) read `ICurrentUser`, so clamping
here covers **every audit writer by construction, present and future**, rather than each sink
remembering to. `ComplianceCheckService.ClampToColumn` is a one-line delegate to the same helper for
`ComplianceCheck.ActualValue` / `.Notes`.

**Truncate rather than drop**: a `User-Agent` prefix still names the browser/OS and a truncated
address is still forensic evidence. Truncation is surrogate-safe — a cut that would split a surrogate
pair backs off one code unit, because a lone high surrogate is an invalid string that Npgsql's strict
UTF-8 encoder rejects at `SaveChangesAsync`, which is the very write-path failure this removes.
`ColumnClamp.To` accepts width 0 (nothing fits → `string.Empty`) and **throws**
`ArgumentOutOfRangeException` on a negative width: there is no such column, so that is a caller bug,
and silently returning empty would erase exactly the evidence the clamp exists to preserve.

### 2. An unusable inbound `X-Trace-Id` is REPLACED, not truncated

`CorrelationIdMiddleware.Resolve` mints a fresh 32-hex id whenever the inbound value is not a usable
trace id. Truncating instead would be worse than useless:

- A truncated prefix is no longer the id the client sent, so it correlates nothing — it just looks
  like it does.
- Truncation **manufactures** collisions: two hostile requests sharing a 64-char prefix would collapse
  into one activity-feed row.

**The invariant this protects is four-way agreement.** One resolved value is written to
`HttpContext.Items["CorrelationId"]`, the echoed `X-Trace-Id` response header, the log scope, and —
via `ICurrentUser.CorrelationId` — the stored `AuditLog.CorrelationId` column. What we hand a customer
for a bug report must be exactly what we stored, or pasting the header finds nothing. A change that
clamps or rewrites any ONE of the four independently breaks the invariant and is a real defect, not a
style question. The `CurrentUserService` clamp on `CorrelationId` — new here, like the middleware
bound itself — is therefore a deliberate **no-op from the moment both land**: `Resolve` has already
bounded the value before `ICurrentUser` reads it. It is kept anyway so the §1 boundary rule stays
total and independent of middleware ordering, rather than because it is ever reached.

### 3. The trace-id charset is narrow, and the reason is the Sentry tag

`IsUsableTraceId` accepts only: non-blank, `≤ AuditColumnLengths.CorrelationId` (64), and every
character drawn from **ASCII letters, ASCII digits, `-`, `_`**. Nothing else — no space, no control
characters, no non-ASCII, no other punctuation.

The load-bearing reason is the un-redacted Sentry `correlation_id` tag described above (and,
secondarily, that this value goes straight into a response header, where a CR/LF is header injection
and its own self-inflicted 500).

The obvious lighter rule — accept any **visible ASCII**, i.e. reject only blanks, spaces and control
characters — is the one this section exists to rule out. It would fix the response-header and length
halves and leave the PII half wide open: `X-Trace-Id: pat@gardenhall.com` is 18 visible-ASCII
characters, so an email address would still reach the un-redacted tag, and so would a customer name
or a phone number. Narrowing to `[A-Za-z0-9_-]` instead makes an email-shaped or free-text id
**structurally impossible to inject**, so ADR 0037's "no email reaches Sentry" invariant holds by
construction rather than by trusting the client. It costs nothing real: 32-hex ids, W3C `traceparent`
hex-and-dash, UUIDs, ULIDs and `_`-prefixed vendor ids all pass verbatim.

Consequently the correlation id is **server-RESOLVED, not always server-minted**, and what makes the
tag safe to send un-redacted is its **shape**, not its origin. ADR 0037's wording and
`frontend/src/lib/sentry/scrub.ts`'s comment are corrected to say so.

### 4. The widths are bound to the EF model structurally, not mirrored

`ModelConfiguration` **consumes** the constants — `HasMaxLength(AuditColumnLengths.IpAddress)` etc.,
and `HasMaxLength(ComplianceCheckService.CheckColumnMaxLength)` for `ComplianceCheck.ActualValue` /
`.Notes` — exactly as it already does for `Vendor.ContactEmail` via `Services.ContactEmail.MaxLength`
([ADR 0038](0038-vendor-contact-email-mirrored-validation.md) / #369). The column and the clamp that
feeds it agree by construction; a hand-copied number cannot drift. `AuditColumnLengths` is the SOURCE
of those widths, not a mirror of them.

`Action` / `EntityType` are excluded on purpose: every value written to them is a compile-time
literal or a `nameof`, never client input.

### 5. Scope: audit-adjacent only

This change bounds the audit boundary. The **systemic** length-validation sweep over the other
client-fed bounded columns — the upload filename path, register, waitlist, the idempotency key — is
[#389](https://github.com/neboxdev/complidrop/issues/389), deliberately not done here.

## Consequences

### Positive
- A long or hostile header can no longer 500 an audited mutation on any route, including the public
  portal upload, and can no longer suppress an attacker's own `user.login_failed` row.
- The paid portal upload permit is no longer burned-then-rolled-back by a header the vendor sent.
- No client-supplied free text can reach the un-redacted Sentry `correlation_id` tag, so ADR 0037's
  PII invariant is enforced at the source rather than asserted downstream.
- The activity feed's collapse key can no longer be forced to merge unrelated events by a repeated
  client-chosen id (any id it accepts is still client-chosen, but a hostile one is no more collapsing
  than an honest one, and a non-conforming one is replaced outright).
- New audit writers inherit the clamp for free — there is one boundary, not N sinks.

### Negative
- Before this change ANY non-empty `X-Trace-Id` was honored, so a client sending one containing `.`,
  `:`, `/` or `+` silently stops having it honored: it gets a minted id back in the response header
  instead. That is the intended trade (the echoed header still tells the caller exactly which id was
  used), but it is a behavior change for any such caller. No known caller does this — the frontend
  never sets `X-Trace-Id`.
- Truncation is lossy by definition: a >500-char `User-Agent` is stored head-first and the tail is
  gone. Judged strictly better than storing nothing and losing the mutation.

### Neutral
- **No migration.** The widths are numerically unchanged (500 / 64 / 64 / 500 / 500); binding them to
  constants is a pure refactor, confirmed by `dotnet ef migrations has-pending-model-changes`
  reporting no model changes.
- The clamp on `CorrelationId` in `CurrentUserService` stays even though the middleware already bounds
  the value — see §2.

## Alternatives considered

### Option A — Clamp at each audit sink
Truncate inside `AuditSaveChangesInterceptor` and inside `AuditLogger`. **Rejected**: two copies that
must not drift, and every future audit writer starts out wrong. The clamp belongs where the value is
first read from the request.

### Option B — Truncate an over-length `X-Trace-Id` instead of replacing it
**Rejected**: see §2 — a truncated id correlates nothing and manufactures activity-feed collisions.
Replacement keeps the four-way agreement, and the echoed response header still tells the caller which
id actually got used.

### Option C — Accept any visible ASCII and redact the tag on the frontend instead
**Rejected**: ADR 0037's post-scrub, un-redacted tag is a deliberate design (a correlation id must
survive the redactor to be usable), and re-scrubbing it there would defeat its purpose while leaving
the same free text in the response header, the log scope and the audit column. Fix it at the source:
if the value cannot BE an email, nothing downstream has to detect one.

### Option D — Widen the columns (or make them `text`)
**Rejected**: unbounded audit columns let a client write megabytes per request into the audit sink,
and it would not touch the trace-id/PII half of the problem at all.

## References

- Tickets: [#372](https://github.com/neboxdev/complidrop/issues/372),
  [#389](https://github.com/neboxdev/complidrop/issues/389) (the deferred systemic sweep),
  [#48](https://github.com/neboxdev/complidrop/issues/48) (rolling bug-fix epic)
- ADRs: [0037](0037-frontend-sentry-pii-scrubbing-and-gating.md) (the un-redacted `correlation_id`
  tag this charset protects), [0038](0038-vendor-contact-email-mirrored-validation.md) (the
  constant-consumed-by-`ModelConfiguration` pattern), [0030](0030-compliance-verdict-combined-unit-of-work.md)
  (why a `ComplianceCheck` 22001 takes the verdict down with it),
  [0032](0032-portal-upload-idempotency.md) (the portal upload's transaction)
- Code: `Services/ColumnClamp.cs` (`ColumnClamp` + `AuditColumnLengths`),
  `Auth/CurrentUserService.cs`, `Middleware/CorrelationIdMiddleware.cs`, `Data/ModelConfiguration.cs`,
  `Services/ComplianceCheckService.cs` (`CheckColumnMaxLength` / `ClampToColumn`),
  `frontend/src/lib/sentry/scrub.ts` (`tagCorrelationId`)
- Tests: `AuditClientInputClampTests` (charset walk, PII-shaped ids, degenerate widths, and the
  structural width binding pinned twice — against the built EF model for a divergent literal, and
  against `ModelConfiguration.cs` source text for an equal-valued re-inline the model check cannot
  see), `AuditClientInputClampIntegrationTests` (public portal upload, failed login,
  echoed-header-equals-stored-column, and — through the real middleware rather than `Resolve` — the
  charset half via an email-shaped `X-Trace-Id` that FITS the column)
