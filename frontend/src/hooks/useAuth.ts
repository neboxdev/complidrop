"use client";

import { useMutation, useQuery, useQueryClient, type QueryClient } from "@tanstack/react-query";
import { api, ApiError } from "@/lib/api";
import { identify, resetIdentity, track } from "@/lib/analytics";

export type Me = {
  userId: string;
  organizationId: string;
  email: string;
  fullName: string;
  role: string;
  plan: string;
  organizationName: string;
  timeZone: string;
};

export type UseMeOptions = {
  /**
   * Suppress the automatic POST /api/auth/refresh that lib/api.ts fires on a 401.
   * Use this ONLY on the public landing page (#30): anonymous visitors have no
   * cd_refresh cookie, so the retry is a guaranteed wasted round-trip on the
   * highest-traffic page in the product.
   *
   * Do NOT pass this from authenticated routes (dashboard layout, settings, etc.).
   * They rely on the auto-refresh to keep a user with an expired cd_session but
   * valid cd_refresh on their page instead of bouncing them to /login.
   *
   * Trade-off accepted in #30: an authenticated user with an *expired* cd_session
   * who lands directly on / will see the logged-out CTAs (the probe returns
   * null because refresh was skipped). The moment they navigate to an
   * authenticated route, refresh fires and they're back in. Acceptable cost
   * vs. paying two auth round-trips for every anonymous visitor.
   */
  skipRefresh?: boolean;
};

// Distinct cache key for the landing-page probe so a 401-mapped-to-null from a
// skipRefresh call cannot poison the authoritative useMe() cache that dashboard
// routes depend on. Login/register/logout mirror writes across both keys below.
//
// Exported so the test harness (src/test/render.tsx) can seed both keys
// without duplicating the literal — a rename of either key here is otherwise
// silently survived by tests that prime the OLD key into a cache nothing
// reads, falling through to MSW and either hanging in waitFor or quietly
// rendering the anonymous branch.
export const ME_KEY = ["auth", "me"] as const;
export const ME_PROBE_KEY = ["auth", "me", "probe"] as const;

// Name MUST match `CookieAuthSetup.HintCookie` on the backend (#69).
// Exported so tests can drive the hint state via document.cookie without
// re-declaring the literal — a rename on either side would otherwise
// silently make the gate either always-on or always-off in tests.
export const SESSION_HINT_COOKIE = "cd_session_hint";

/**
 * Returns true iff the non-httpOnly `cd_session_hint` cookie is present
 * — the only signal we have on the client that this browser has been
 * authenticated at some point. The cookie carries NO credential; it
 * exists solely to gate the landing-page `useMe({ skipRefresh })`
 * probe behind `useQuery({ enabled })` so anonymous visitors pay ZERO
 * auth round-trips (#69).
 *
 * SSR / first paint: `document` is undefined on the server, so we
 * return `false`. The landing page renders the logged-out CTAs on the
 * server, the SPA hydrates, the gate flips to true on hydration if a
 * hint cookie is present, and `useQuery` fires the probe THEN. The
 * worst case is a single-frame logged-out flash for a user who lands
 * via SSR — acceptable because the alternative (firing the probe on
 * the server) costs every anonymous SSR pass an auth round-trip too.
 *
 * Matching is anchored on `; <name>=` (with a leading-`;` sentinel) so
 * the literal `cd_session_hint=` is matched as a full token rather
 * than as a substring — a hypothetical sibling cookie named
 * `bad_cd_session_hint` cannot trigger a false positive.
 *
 * Exported for use in tests; the production caller is `useMe()` below.
 */
export function hasSessionHint(): boolean {
  if (typeof document === "undefined") return false;
  // `; ` sentinel + leading `; ` on cookies makes startsWith-style
  // matching unambiguous without parsing every key=value pair.
  const cookies = `; ${document.cookie}`;
  return cookies.includes(`; ${SESSION_HINT_COOKIE}=`);
}

function setMeCache(qc: QueryClient, me: Me | null) {
  qc.setQueryData(ME_KEY, me);
  qc.setQueryData(ME_PROBE_KEY, me);
}

export function useMe(opts: UseMeOptions = {}) {
  const queryKey = opts.skipRefresh ? ME_PROBE_KEY : ME_KEY;
  // Gate the landing-page probe (skipRefresh) on the hint cookie (#69):
  // anonymous visitors with no hint pay ZERO auth round-trips. The
  // authoritative `useMe()` (no opts — dashboard layout, settings) is
  // deliberately NOT gated: those routes rely on the canonical
  // 401→refresh→retry chain in `lib/api.ts` to keep a user with an
  // expired cd_session but valid cd_refresh on their page instead of
  // bouncing them to /login. Reading `document.cookie` once at the
  // hook-call site is fine — the gate only matters for the initial
  // mount; subsequent transitions to "authenticated" go through
  // useLogin/useRegister, which write the Me cache directly and skip
  // the network entirely.
  const enabled = opts.skipRefresh ? hasSessionHint() : true;
  return useQuery<Me | null>({
    queryKey: [...queryKey],
    queryFn: async () => {
      try {
        return await api.get<Me>("/api/auth/me", { skipRefresh: opts.skipRefresh });
      } catch (err) {
        if (err instanceof ApiError && (err.status === 401 || err.code === "auth.unauthorized")) {
          return null;
        }
        throw err;
      }
    },
    enabled,
    staleTime: 60_000,
  });
}

export type RegisterPayload = {
  email: string;
  password: string;
  fullName: string;
  companyName: string;
  industry?: string;
  companySize?: string;
  timeZone?: string;
};

export function useRegister() {
  const qc = useQueryClient();
  return useMutation<Me, ApiError, RegisterPayload>({
    mutationFn: (payload) => api.post<Me>("/api/auth/register", payload),
    onSuccess: (me) => {
      setMeCache(qc, me);
      identify(me.userId, { email: me.email, organizationId: me.organizationId, plan: me.plan });
      track("user.registered");
    },
  });
}

export function useLogin() {
  const qc = useQueryClient();
  return useMutation<Me, ApiError, { email: string; password: string }>({
    mutationFn: (payload) => api.post<Me>("/api/auth/login", payload),
    onSuccess: (me) => {
      setMeCache(qc, me);
      identify(me.userId, { email: me.email, organizationId: me.organizationId, plan: me.plan });
      track("user.logged_in");
    },
  });
}

export function useLogout() {
  const qc = useQueryClient();
  return useMutation<void, ApiError, void>({
    mutationFn: () => api.post<void>("/api/auth/logout"),
    onSuccess: () => {
      // qc.clear() blows away every cache entry — both ME_KEY and ME_PROBE_KEY
      // included — so we don't need to setMeCache(null) here. The pre-clear
      // write keeps the immediate read consistent for any subscriber that
      // re-renders before clear() commits.
      setMeCache(qc, null);
      resetIdentity();
      qc.clear();
    },
  });
}
