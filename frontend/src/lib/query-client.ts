"use client";

import {
  QueryClient,
  QueryCache,
  MutationCache,
  type DefaultOptions,
} from "@tanstack/react-query";
import { toast } from "sonner";
import { ApiError, GENERIC_FALLBACK_MESSAGE } from "./api";
import { markSessionExpired } from "./session-expiry";
import { ME_KEY, ME_PROBE_KEY } from "@/hooks/useAuth";

// Opt-in flag for the global mutation-error toast. Mutations that do NOT
// handle their own errors (no local onError / try-catch) set
// `meta: { errorToast: true }` so a failure surfaces a jargon-free toast
// instead of failing silently — the "I clicked create and nothing happened"
// class of bug. Mutations that already toast locally (login, upload, settings,
// document-detail, vendor create/update, …) deliberately omit it to avoid a
// double toast.
declare module "@tanstack/react-query" {
  // v5 derives `MutationMeta` from `Register["mutationMeta"]` and only honors
  // it when it extends `Record<string, unknown>` — hence the intersection.
  interface Register {
    mutationMeta: { errorToast?: boolean } & Record<string, unknown>;
  }
}

/**
 * True when an error means "the session is gone" (a 401 that survived the
 * silent refresh in lib/api.ts), as opposed to a bad-login 401
 * (`auth.invalid_credentials`) or a real server error. Keyed on the stable
 * server error CODE — set by the backend OnChallenge handler
 * (`auth.unauthorized`) and the refresh endpoint (`auth.token_expired`) —
 * NOT the raw 401 status, so a failed login attempt is never mistaken for an
 * expired session.
 *
 * Returns a plain `boolean` (not a type predicate) on purpose: callers use it
 * to GATE an error card (`!isAuthError(query.error)`), and a predicate would
 * narrow the error to `never` in that branch and break the card's own
 * `error?.message` read.
 */
export function isAuthError(err: unknown): boolean {
  return (
    err instanceof ApiError &&
    (err.code === "auth.unauthorized" || err.code === "auth.token_expired")
  );
}

/**
 * Builds a QueryClient wired with the app's global error handling:
 *
 *   - Any query/mutation that fails with a definitive auth error nulls the
 *     useMe() cache, which makes the dashboard layout's existing redirect
 *     effect bounce the user to /login — instead of every page rendering a
 *     scary "Something went wrong" card for what is really an expired session.
 *   - Any mutation marked `meta: { errorToast: true }` that fails surfaces a
 *     jargon-free toast (server message or GENERIC_FALLBACK_MESSAGE).
 *
 * `defaultOptions` is injected so production and the test harness share ONE
 * source of truth for the error handling while keeping their own retry /
 * staleTime tuning.
 */
export function createQueryClient(defaultOptions: DefaultOptions): QueryClient {
  // `let` (not const) is required: the QueryCache / MutationCache onError
  // handlers below close over `client` to null the me-cache, but `client` is
  // the value we're still constructing — a forward reference the handlers only
  // invoke later (on an actual error), never during construction.
  // eslint-disable-next-line prefer-const
  let client: QueryClient;

  const handleAuthError = () => {
    // This is the SINGLE involuntary-eviction chokepoint — a 401 that survived
    // the silent refresh. Flag it so the layout shows the "you were signed out"
    // notice + a returnTo, distinct from a deliberate log-out (which nulls the
    // cache directly, never here) which just lands on a plain /login (#318 FP-045).
    markSessionExpired();
    // Null both keys (authoritative + landing-probe) so the layout's
    // `useMe()` effect sees `me.data === null` and redirects. Reuses the
    // tested redirect path rather than navigating from here (no router
    // dependency, no redirect-loop risk on the /login page).
    client.setQueryData(ME_KEY, null);
    client.setQueryData(ME_PROBE_KEY, null);
  };

  client = new QueryClient({
    queryCache: new QueryCache({
      onError: (err) => {
        if (isAuthError(err)) handleAuthError();
      },
    }),
    mutationCache: new MutationCache({
      onError: (err, _vars, _ctx, mutation) => {
        if (isAuthError(err)) {
          handleAuthError();
          return;
        }
        if (mutation.meta?.errorToast) {
          toast.error(err instanceof ApiError ? err.message : GENERIC_FALLBACK_MESSAGE);
        }
      },
    }),
    defaultOptions,
  });

  return client;
}
