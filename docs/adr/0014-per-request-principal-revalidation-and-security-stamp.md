# 0014. Per-request principal re-validation + a rotating security stamp

- **Status:** accepted
- **Date:** 2026-06-04
- **Deciders:** Ruben G.

## Context

Auth is cookie-delivered **stateless JWT**: a `cd_session` (15 min) and a `cd_refresh` (30 days), both HS256, validated purely on signature + lifetime + issuer/audience. There was **no revocation primitive** — no `jti` denylist, no security stamp, no per-request DB check. `Refresh()` re-minted a session from any valid refresh token plus a live user lookup.

Two defects, surfaced by the #183 careful-review and the #180-batch re-review, both trace to this single gap:

1. **Password reset/change did not evict existing sessions** ([#202](https://github.com/neboxdev/complidrop/issues/202)). A stolen `cd_refresh` kept working for its full 30-day TTL even after the victim reset their password — defeating the primary purpose of the reset flow (recovering a compromised account).
2. **A soft-deleted account kept acting for the `cd_session` TTL.** `DeleteAccount` soft-deletes only the User + Org rows, while most authed endpoints authorize on the `org_id` JWT claim + the per-entity tenant filter — neither of which knows the org/user was deleted. So a deleted account's still-valid session could read/export/upload for up to 15 minutes (the endpoints that re-load the user — `/me`, `/account/export` — already 401'd via the soft-delete filter, but the `org_id`-claim-only endpoints did not).

Both require the same thing the stateless design lacked: **re-validating the principal against current DB state on each request.**

## Decision

Add a **`User.SecurityStamp` (GUID)** and **re-validate the principal on every authenticated request**.

- `SecurityStamp` is embedded as a `stamp` claim in both the session and refresh JWTs (`TokenService`). Migration backfills existing rows with `gen_random_uuid()`; `Register` sets it explicitly so the freshly-issued token matches the row.
- The JWT-bearer `OnTokenValidated` hook (fires for every `[Authorize]` endpoint, after signature/lifetime pass) does **one indexed PK lookup** via `SystemDbContext` and fails the token when:
  - the user is **missing or soft-deleted** (the soft-delete query filter returns null) → closes defect #2 on **every** endpoint, not just user-loading ones; or
  - the token's `stamp` is **present and ≠** the user's current `SecurityStamp` → closes defect #1.
- The `/api/auth/refresh` path validates its token manually (it bypasses the middleware hook), so it repeats the same liveness + stamp check.
- `SecurityStamp` is **rotated** on `ResetPassword` (anonymous → no session to keep; all sessions evicted) and `ChangePassword` (which then **re-issues the caller's cookies** with the new stamp, so changing your own password doesn't log out the tab you're using, while every *other* session is evicted). `DeleteAccount` needs no rotation — the liveness check already evicts a soft-deleted user.
- **Grandfathering**: a token with **no** `stamp` claim (minted before this change) is NOT rejected on the stamp check — only the liveness check applies — so deploying this does not mass-logout everyone holding a pre-#202 token. They re-stamp naturally on their next login/refresh.

### Why a per-request DB lookup (vs. refresh-only)

A refresh-only stamp check would bound an attacker to the residual ≤15-min session window after a reset, and would NOT close defect #2 at all (the deleted account would keep hitting `org_id`-claim endpoints for the session TTL). Re-validating per request closes both defects immediately and uniformly. The cost is a single indexed PK lookup per authenticated request — acceptable for an auth-critical SaaS at this scale.

## Consequences

- The auth model moves from "pure stateless JWT" to **"stateless JWT + per-request principal re-validation."** This is the deliberate trade: a small, bounded DB cost per authed request in exchange for real revocation (credential-change eviction + immediate deactivation of deleted accounts).
- **Performance**: a dashboard page that fires several authed queries now does one extra `Users` PK lookup per query. Negligible at current scale; if it ever matters, the stamp/liveness can be cached per `(userId)` with a short TTL (seconds) — at the cost of a correspondingly short revocation lag. Not done now (correctness over premature optimization).
- A DB outage now fails authed requests at the auth layer rather than the data layer — no behavioral regression (they'd fail at the data layer anyway).
- Change-email does **not** rotate the stamp (the password — the login credential — is unchanged; the existing sessions remain legitimately the user's). Account deletion relies on the liveness check, not a stamp bump.
- Supersedes the "no session eviction" caveat noted in ADR 0013.
