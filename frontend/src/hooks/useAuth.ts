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
  /** True once the user has confirmed their signup email via the #184 link.
   * Gates the persistent "confirm your email" dashboard banner. */
  emailVerified: boolean;
  /** True once the user has finished (or skipped) the first-run onboarding (#191).
   * Server-persisted so the welcome modal fires exactly once, across devices. */
  hasCompletedOnboarding: boolean;
  /** Server-evaluated feature flags the UI gates on (#416, ADR 0036 Amendment 3).
   * `correctedChecklists` mirrors the backend `TemplateCorrections:Enabled` flag — while false
   * (the prod default, pending the G1 legal/insurance sign-off), the rules page hides the liquor
   * "+ Add a requirement" menu option and the additional-insured nudge.
   * `correctedAdditionalInsuredWording` mirrors the backend
   * `ComplianceClaims:CorrectedAdditionalInsuredWording` flag (#396 / CLM-1, ADR 0042) — while false
   * (the prod default, pending the G1 attorney sign-off), the additional-insured requirement sentence
   * and failure copy use the legacy categorical "Names …" wording; when true they use the honest
   * "certificate indicates …" wording. Both ride every me-shaped payload (me / register / login /
   * complete-onboarding / organization), so the session cache always holds them. */
  features: {
    correctedChecklists: boolean;
    correctedAdditionalInsuredWording: boolean;
  };
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
 * Gate re-evaluation is RENDER-DRIVEN — the returned value only flips
 * when a consumer re-renders. In practice this is fine because
 * login/register/logout mutations write/clear the Me cache (via
 * `setMeCache` / `qc.clear()`) which triggers consumer re-renders,
 * and the cache write also feeds `useQuery` its data directly so the
 * gate-flip-then-fetch race is moot. An EXTERNAL cookie write
 * (DevTools tampering, a future non-mutation route that sets the
 * hint server-side) would not re-evaluate the gate until something
 * else triggered a render; that's deliberate — we don't subscribe to
 * cookie events.
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
    queryFn: async ({ signal }) => {
      try {
        return await api.get<Me>("/api/auth/me", { skipRefresh: opts.skipRefresh, signal });
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

/**
 * Redeems an email-verification token (#184). Public — the link may be opened
 * in a logged-out browser. On success we invalidate the Me cache so an
 * authenticated tab drops the "confirm your email" banner without a reload.
 */
export function useVerifyEmail() {
  const qc = useQueryClient();
  return useMutation<{ message: string }, ApiError, { token: string }>({
    mutationFn: (payload) => api.post<{ message: string }>("/api/auth/verify-email", payload),
    onSuccess: () => {
      qc.invalidateQueries({ queryKey: ME_KEY });
      qc.invalidateQueries({ queryKey: ME_PROBE_KEY });
    },
  });
}

/**
 * Marks first-run onboarding complete (#191). The server flips the persisted flag
 * and returns the refreshed Me, which we write straight into the session cache so
 * the welcome modal closes and never re-fires (on this or any other device).
 * Idempotent server-side, so a double-call (two tabs) is harmless.
 */
export function useCompleteOnboarding() {
  const qc = useQueryClient();
  return useMutation<Me, ApiError, void>({
    mutationFn: () => api.post<Me>("/api/auth/complete-onboarding"),
    onSuccess: (me) => setMeCache(qc, me),
  });
}

/**
 * Asks the server to re-send the verification link to the logged-in user's own
 * address (#184). Surfaced by the dashboard banner's "Resend" action.
 */
export function useResendVerification() {
  return useMutation<{ message: string }, ApiError, void>({
    mutationFn: () => api.post<{ message: string }>("/api/auth/resend-verification"),
  });
}

/**
 * Updates the org name + IANA time zone (#185). Returns the refreshed Me, which
 * we write straight into the cache so the sidebar org name + settings reflect
 * the change without a refetch.
 */
export function useUpdateOrganization() {
  const qc = useQueryClient();
  return useMutation<Me, ApiError, { name: string; timeZone: string }>({
    mutationFn: (payload) => api.put<Me>("/api/auth/organization", payload),
    onSuccess: (me) => setMeCache(qc, me),
  });
}

/** Requests a password-reset link (#183). Always resolves (server returns 200
 * whether or not the email exists — no enumeration). */
export function useForgotPassword() {
  return useMutation<{ message: string }, ApiError, { email: string }>({
    mutationFn: (payload) => api.post<{ message: string }>("/api/auth/forgot-password", payload),
  });
}

/** Redeems a reset token + sets a new password (#183). Public. */
export function useResetPassword() {
  return useMutation<{ message: string }, ApiError, { token: string; newPassword: string }>({
    mutationFn: (payload) => api.post<{ message: string }>("/api/auth/reset-password", payload),
  });
}

/** Changes the password after re-checking the current one (#183). */
export function useChangePassword() {
  return useMutation<{ message: string }, ApiError, { currentPassword: string; newPassword: string }>({
    mutationFn: (payload) => api.post<{ message: string }>("/api/auth/change-password", payload),
  });
}

/** Starts a change-email flow — emails a confirmation link to the new address (#183). */
export function useChangeEmail() {
  return useMutation<{ message: string }, ApiError, { password: string; newEmail: string }>({
    mutationFn: (payload) => api.post<{ message: string }>("/api/auth/change-email", payload),
  });
}

/** Deletes the account after a password re-check (#183). Clears all cached
 * session state on success, mirroring logout. */
export function useDeleteAccount() {
  const qc = useQueryClient();
  return useMutation<{ message: string }, ApiError, { password: string }>({
    mutationFn: (payload) => api.post<{ message: string }>("/api/auth/account/delete", payload),
    onSuccess: () => {
      setMeCache(qc, null);
      resetIdentity();
      qc.clear();
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
