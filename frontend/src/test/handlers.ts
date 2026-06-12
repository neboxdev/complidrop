/**
 * Default MSW request handlers — the "do-nothing-surprising" baseline every
 * test starts from. Override per-test with `server.use(http.get(...))`.
 *
 * Defaults are intentionally minimal:
 *   - `/api/auth/me`               → 401 anonymous envelope (the most common state).
 *   - `/api/auth/refresh`          → 401 (no cookie, refresh fails).
 *   - `/api/billing/subscription`  → 401 (entitlement unknown → no plan gating,
 *     plan-safe copy; #261). Tests asserting plan-dependent UI must override.
 *
 * Any other endpoint hit without an explicit override raises an unhandled
 * request in tests — that's a feature, not a gap: it forces each test to
 * declare exactly which network surface it intends to exercise.
 *
 * Internal to the harness — `server.ts` is the only consumer. Keeping this
 * list server-private prevents a contributor from accidentally building a
 * SECOND MSW server in some test file, which would split state and break
 * the "one server for the whole run" invariant `server.ts` relies on.
 */
import { http } from "msw";
import { jsonError, url } from "./helpers";

export const defaultHandlers = [
  // Anonymous baseline. Tests that need an authed Me override this OR prime
  // the QueryClient cache via `renderWithProviders({ auth: authedMe })`.
  http.get(url("/api/auth/me"), () =>
    jsonError("auth.unauthorized", "Not authenticated", { status: 401 }),
  ),
  // Refresh fails with the same envelope so the 401-retry path in
  // `lib/api.ts` short-circuits instead of looping.
  http.post(url("/api/auth/refresh"), () =>
    jsonError("auth.unauthorized", "Not authenticated", { status: 401 }),
  ),
  // Same anonymous baseline for the subscription snapshot (#261): the shared
  // useSubscription hook now fires from the dashboard checklist, the vendor
  // detail page, AND settings, so without a default every test rendering those
  // pages would have to redeclare it. 401 keeps the entitlement UNKNOWN
  // (`data` undefined → no gating, plan-safe copy) — the do-nothing-surprising
  // state. Tests that assert plan-dependent UI override with jsonOk({...}).
  http.get(url("/api/billing/subscription"), () =>
    jsonError("auth.unauthorized", "Not authenticated", { status: 401 }),
  ),
];
