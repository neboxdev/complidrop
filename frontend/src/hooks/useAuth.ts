"use client";

import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
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

export function useMe() {
  return useQuery<Me | null>({
    queryKey: ["auth", "me"],
    queryFn: async () => {
      try {
        // skipRefresh: anonymous landing-page visitors have no cd_refresh cookie,
        // so the automatic POST /api/auth/refresh on 401 is a guaranteed wasted
        // round-trip on the highest-traffic public page. Authenticated users
        // with a valid cd_session still get 200 here; refresh-on-expiry still
        // works on every other authenticated call site (#30, follow-up from #22).
        return await api.get<Me>("/api/auth/me", { skipRefresh: true });
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
      qc.setQueryData(["auth", "me"], me);
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
      qc.setQueryData(["auth", "me"], me);
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
      qc.setQueryData(["auth", "me"], null);
      resetIdentity();
      qc.clear();
    },
  });
}
