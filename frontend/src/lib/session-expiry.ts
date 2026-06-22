/**
 * Session-expiry handoff + open-redirect guard (#318 FP-045).
 *
 * When a session is involuntarily evicted — a 401 that survived the silent
 * refresh in `lib/api.ts` — the dashboard layout redirects to `/login`. Before
 * this, that redirect was indistinguishable from a deliberate log-out: Pat lost
 * her place with no explanation, which reads as "the app logged me out at
 * random" on a tool she's trusting with compliance records.
 *
 * Two pieces:
 *   1. A per-tab flag, set at the SINGLE involuntary chokepoint
 *      (`query-client.ts` `handleAuthError`) and consumed once by the layout,
 *      so a deliberate log-out (which nulls the cache directly, never through
 *      that chokepoint) does NOT get the "you were signed out" treatment.
 *   2. `safeReturnTo` — validates a `returnTo` candidate as a same-origin,
 *      relative path before we navigate to it after sign-in. The candidate
 *      rides in the URL (`/login?returnTo=…`), so it is attacker-controllable;
 *      navigating to an unvalidated value is a classic open-redirect phishing
 *      vector. Used by BOTH the layout (minting the param) and the login form
 *      (consuming it).
 */

const EXPIRED_FLAG_KEY = "cd_session_expired";

/**
 * Mark that the current session was involuntarily evicted (expired / refresh
 * failed). sessionStorage so it's per-tab and survives the in-tab client
 * redirect to `/login`. Best-effort: private-mode storage denial is swallowed
 * (we simply fall back to a generic `/login` with no notice).
 */
export function markSessionExpired(): void {
  if (typeof window === "undefined") return;
  try {
    window.sessionStorage.setItem(EXPIRED_FLAG_KEY, "1");
  } catch {
    /* storage unavailable (private mode / quota) — degrade to no notice */
  }
}

/**
 * Read-and-clear the expiry flag. Returns true exactly once per eviction, so a
 * later manual visit to `/login` doesn't re-show the notice.
 */
export function consumeSessionExpired(): boolean {
  if (typeof window === "undefined") return false;
  try {
    const v = window.sessionStorage.getItem(EXPIRED_FLAG_KEY);
    if (v !== null) window.sessionStorage.removeItem(EXPIRED_FLAG_KEY);
    return v === "1";
  } catch {
    return false;
  }
}

/**
 * Validate a `returnTo` candidate as a SAME-ORIGIN, RELATIVE in-app path.
 * Open-redirect guard (#318 FP-045): returns the path only if it is safe to
 * `router.push()` after login, else `null` (caller falls back to `/dashboard`).
 *
 * Allowed: a path rooted at a single `/` ("/documents", "/vendors/123?x=1").
 * Rejected:
 *   - anything not starting with `/` ("https://evil.com", "evil.com", "")
 *   - protocol-relative "//evil.com" (the browser treats it as another origin)
 *   - "/\\evil.com" and "/\t…" — some browsers normalise the backslash to "/",
 *     turning it into a protocol-relative URL; control chars can do likewise
 *   - anything with an embedded scheme or backslash, defensively
 */
export function safeReturnTo(candidate: string | null | undefined): string | null {
  if (typeof candidate !== "string" || candidate.length === 0) return null;
  // Must be rooted-relative.
  if (candidate[0] !== "/") return null;
  // "//x" (protocol-relative) and "/\x" (backslash → some UAs treat as "//")
  // both resolve to a foreign origin.
  if (candidate[1] === "/" || candidate[1] === "\\") return null;
  // No control/whitespace chars (a leading tab/newline can defeat the checks
  // above once the browser strips it) and no backslashes anywhere.
  if (/[\x00-\x1f\x7f\\]/.test(candidate)) return null;
  return candidate;
}
