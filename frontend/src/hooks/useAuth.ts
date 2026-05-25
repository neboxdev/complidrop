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
const ME_KEY = ["auth", "me"] as const;
const ME_PROBE_KEY = ["auth", "me", "probe"] as const;

function setMeCache(qc: QueryClient, me: Me | null) {
  qc.setQueryData(ME_KEY, me);
  qc.setQueryData(ME_PROBE_KEY, me);
}

export function useMe(opts: UseMeOptions = {}) {
  const queryKey = opts.skipRefresh ? ME_PROBE_KEY : ME_KEY;
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
