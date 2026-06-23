# 0032. Public portal upload idempotency — token-namespaced client key, co-committed with the permit

- **Status:** accepted
- **Date:** 2026-06-23
- **Deciders:** Ruben G. (founder), Claude (implementing #333)

## Context

The public vendor-portal upload route `POST /api/portal/{token}/upload` accepted no `Idempotency-Key` and didn't dedupe. A double-submit (double-tap, a retried request, a flaky network re-send) could create a **duplicate `Document`** AND **burn one of the link's `MaxUploads` permits**. Batch G (#321) shipped a client-side mitigation (the dropzone is `disabled` while `atQuota || uploading`), which closes the realistic double-tap window in the UI, but that's a UX guard, not a server guarantee — two rapid requests that bypass the dropzone (a retried POST, a duplicated request) are still each processed end to end. Filed as #333.

The existing `IIdempotencyService` keys every record on `(OrganizationId, Key)`. The public portal route runs **unauthenticated** on `SystemDbContext` with **no `OrganizationId` principal**, so a client-sent `Idempotency-Key` would be silently ignored — which is why honoring it needs a deliberate scoping decision (the acceptance asks: token-scoped, content-hash, or both?). #336 had just generalized `IIdempotencyService` to a **co-commit** model (the dedupe record commits in the same transaction as the side effect; the `(OrganizationId, Key)` unique index makes a concurrent same-key commit conflict, and the loser replays the winner — ADR 0029); #333 builds on it.

## Decision

**Honor a client `Idempotency-Key`, scoped per `(link-org, "portal:{token}:{clientKey}")`, co-committed inside the route's existing permit-reservation transaction.**

- **Org**: the portal route has no *authenticated* principal, but the token resolves to a `VendorPortalLink → Vendor → Organization`, so it has a perfectly good `OrganizationId` (the link's org). The record keys on that.
- **Key namespacing**: the stored key is `"portal:{token}:{clientKey}"` — the token-namespaced client key. The token scopes the dedupe to *this* link (two different links never collide), and the `"portal:"` prefix guarantees a portal key can never collide with a dashboard upload's raw-UUID key in the same org.
- **Untrusted-input guard**: the client key is honored only when `0 < length ≤ 128` (the route is public; the namespaced key must fit the `IdempotencyRecord.Key` `varchar(200)`). An oversize/empty key is simply ignored (the upload proceeds without dedupe), never a 500.
- **Co-commit in the permit transaction (the load-bearing choice)**: the route already wraps the **atomic permit reservation** (`UPDATE … WHERE UploadCount < MaxUploads`) + the `Document` insert + the audit row in **one explicit transaction**. We add the `IdempotencyRecord` to that same `SaveChanges`. So a concurrent same-key request's commit fails the `(org, key)` unique index, and because the permit increment is in the *same* transaction, that conflict **rolls the permit increment back** — the loser duplicates neither the `Document` *nor* the burned permit. It then replays the winner (same `uploadId`). A repeat after commit is caught earlier by the fast-path `TryGetAsync` replay (before any permit/blob work).
- **Client**: the portal dropzone sends `Idempotency-Key` on the upload POST, **stable across a retry** (the failed attempt's key is captured and reused by the Retry button) — so a *succeeded-but-response-lost* upload replays the winner instead of creating a second `Document`.

Content-hashing was **considered and not adopted** (see alternatives): the client-sent key is the contract the acceptance asks for and covers the double-submit/retry cases; a content hash would only add value for clients that send *no* key, which our own client always does.

## Consequences

### Positive
- A double-submit / retried POST / flaky re-send dedupes server-side: exactly one `Document`, exactly one permit — even concurrently, and even when the response to the original was lost (the retry reuses the key and replays).
- Reuses #336/ADR 0029's proven co-commit pattern — no new idempotency mechanism. The portal's *existing* permit-reservation transaction is exactly the seam that makes the permit roll back on conflict, so the "burned permit" half is fixed for free.
- Public-route-safe: org derived from the token (not the client), key length-bounded, no injection (bound params), the key only ever dedupes the caller's own org.

### Negative
- A losing concurrent racer still does wasted work (blob upload + permit `UPDATE`) before its commit conflicts and rolls back; the blob is best-effort cleaned by the existing `finally`. Acceptable — the concurrent-same-key case is rare, and it's the same tradeoff #336/#238 already accept.
- The client uses **per-call** keys (fresh per `uploadFile`), reused only across an explicit Retry of the same file — not a single key for the file's whole lifetime. So two *separate* drops of the same file (distinct user actions) are distinct uploads (by design; the dropzone-disabled guard covers accidental re-drops).

### Neutral
- No schema change (reuses the `IdempotencyRecord` table + `(OrganizationId, Key)` index from #336). No new endpoint surface; the route's response shape is unchanged.

## Alternatives considered

### Option A — Content-hash key (dedupe even without a client key)
Hash the uploaded bytes and key on `(org, token + content-hash)`, so two identical uploads dedupe with no client cooperation. **Rejected as the primary mechanism**: it changes the semantics (two *intentional* re-uploads of the identical file would silently collapse to one — wrong for a vendor re-sending a corrected-but-identical doc), it can't dedupe two *different* files from one double-submit, and our client always sends a key so the gap it closes doesn't exist here. The client-key contract is what the acceptance asks for.

### Option B — Un-namespaced `(org, clientKey)` (reuse the dashboard scope verbatim)
**Rejected**: a portal upload and a dashboard upload in the same org could (astronomically) collide on a shared raw key, and two different links would share a key space. The `"portal:{token}:"` namespace removes both with no downside.

### Option C — Separate idempotency transaction (reserve before the side effect)
A leading reservation insert in its own transaction (the #336 "placeholder" alternative). **Rejected** for the portal specifically because the permit increment lives in the route's existing transaction; co-committing the record *in that transaction* is what makes the permit roll back on conflict. A separate reservation would leave the permit-burn half unsolved (or require threading the rollback manually).

## References

- Tickets: [#333](https://github.com/neboxdev/complidrop/issues/333), [#336](https://github.com/neboxdev/complidrop/issues/336) (the co-commit substrate), [#321](https://github.com/neboxdev/complidrop/issues/321) (Batch G client mitigation), [#242](https://github.com/neboxdev/complidrop/issues/242) (portal quota atomicity), [#48](https://github.com/neboxdev/complidrop/issues/48)
- ADRs: [0029](0029-idempotency-co-commit-reservation.md) (the co-commit pattern generalized here)
- Code: `Endpoints/VendorPortalEndpoints.cs` (`UploadViaPortal`), `frontend/src/app/portal/[token]/page.tsx`
- FP-123 in `docs/ux/final-pass-2026-06-10.md`
