/**
 * useLogin / useRegister state-transition contract (#35).
 *
 * Pairs with `useAuth.test.tsx` (#30, useMe + cache split). That file
 * pins the `useMe()` 401-vs-refresh sequencing; this one pins the
 * mutation-side state machine that the login/register PAGES depend on:
 *
 *   - idle → isPending → isSuccess (Me cache + analytics exactly-once)
 *   - idle → isPending → isError   (ApiError forwarded with message)
 *
 * Driven through MSW + the real api client — a regression in lib/api.ts
 * envelope mapping, the mutation's onSuccess callback, or the cache write
 * fails here BEFORE the per-page test even runs.
 *
 * The `isPending` flip is pinned via a held-promise pattern (MSW awaits
 * `settled` until the test calls `release()`) so the intermediate state
 * is observable. A regression where `useMutation` got replaced by a
 * synchronous wrapper that never set isPending would be caught here.
 */
import { describe, it, expect, vi, beforeEach } from "vitest";
import { http } from "msw";
import { renderHook, waitFor } from "@testing-library/react";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import type { ReactNode } from "react";
import { ME_KEY, ME_PROBE_KEY, useLogin, useRegister } from "./useAuth";
import { server, url, jsonOk, jsonError, authedMe } from "@/test";
import { ApiError } from "@/lib/api";

// useLogin / useRegister fire analytics in their onSuccess. Mock the
// boundary so the tests don't depend on PostHog being initialized.
const { identify, resetIdentity, track } = vi.hoisted(() => ({
  identify: vi.fn(),
  resetIdentity: vi.fn(),
  track: vi.fn(),
}));
vi.mock("@/lib/analytics", () => ({ identify, resetIdentity, track }));

function makeWrapper() {
  // `gcTime: Infinity` keeps the cache populated for the assertions below
  // (no component subscribes to ME_KEY in these mutation-only tests, so
  // with the default gcTime the entry would be reaped before we read it).
  // Per-hook isolation comes from making a fresh client per `makeWrapper`
  // call, not from per-query GC.
  const qc = new QueryClient({
    defaultOptions: {
      queries: { retry: false, gcTime: Infinity },
      mutations: { retry: false },
    },
  });
  function Wrapper({ children }: { children: ReactNode }) {
    return <QueryClientProvider client={qc}>{children}</QueryClientProvider>;
  }
  return { qc, Wrapper };
}

/**
 * Build a MSW handler that holds the response until the returned `release`
 * is invoked. Lets the test observe the intermediate `isPending` state
 * before forcing settlement.
 */
function heldHandler(
  path: string,
  responder: () => Response,
): { handler: ReturnType<typeof http.post>; release: () => void } {
  let release: () => void = () => {};
  const settled = new Promise<void>((r) => (release = r));
  const handler = http.post(url(path), async () => {
    await settled;
    return responder();
  });
  return { handler, release };
}

describe("useLogin — state transitions (#35)", () => {
  beforeEach(() => {
    identify.mockClear();
    resetIdentity.mockClear();
    track.mockClear();
  });

  it("idle → isPending → isSuccess on 200, writes Me to both keys, fires analytics ONCE", async () => {
    const { handler, release } = heldHandler("/api/auth/login", () =>
      jsonOk(authedMe),
    );
    server.use(handler);

    const { qc, Wrapper } = makeWrapper();
    const { result } = renderHook(() => useLogin(), { wrapper: Wrapper });

    // idle: no pending state, no data, no error.
    expect(result.current.isPending).toBe(false);
    expect(result.current.data).toBeUndefined();
    expect(result.current.error).toBeNull();

    // Fire the mutation; the MSW handler is awaiting `settled` so the
    // hook observably sits in isPending until we release.
    result.current.mutate({
      email: "owner@acme.test",
      password: "verystrongpass1",
    });
    try {
      await waitFor(() => expect(result.current.isPending).toBe(true));
      expect(result.current.data).toBeUndefined();
      expect(result.current.error).toBeNull();
    } finally {
      release();
    }

    // Settlement: isSuccess true, data populated, cache written.
    await waitFor(() => expect(result.current.isSuccess).toBe(true));
    expect(result.current.data).toEqual(authedMe);
    expect(result.current.error).toBeNull();

    // Cache contract: useLogin's onSuccess (useAuth.ts) mirrors the Me
    // into BOTH the authoritative key and the landing-page probe key —
    // so an authed-in-this-tab user landing on / sees the dashboard CTA.
    // Use the EXPORTED constants so a rename in useAuth.ts breaks the
    // test loudly instead of silently passing `undefined === undefined`.
    expect(qc.getQueryData([...ME_KEY])).toEqual(authedMe);
    expect(qc.getQueryData([...ME_PROBE_KEY])).toEqual(authedMe);

    // Analytics contract: identify + track fire EXACTLY ONCE each. A
    // regression that duplicated the call (e.g. via an extra useEffect)
    // would skew funnel numbers — this is the layer to catch it.
    expect(identify).toHaveBeenCalledTimes(1);
    expect(identify).toHaveBeenCalledWith(
      authedMe.userId,
      expect.objectContaining({
        email: authedMe.email,
        organizationId: authedMe.organizationId,
        plan: authedMe.plan,
      }),
    );
    expect(track).toHaveBeenCalledTimes(1);
    expect(track).toHaveBeenCalledWith("user.logged_in");
  });

  it("idle → isError on 401, error is an ApiError with the server message, cache untouched", async () => {
    server.use(
      http.post(url("/api/auth/login"), () =>
        jsonError("auth.invalid_credentials", "Invalid email or password.", {
          status: 401,
        }),
      ),
    );

    const { qc, Wrapper } = makeWrapper();
    const { result } = renderHook(() => useLogin(), { wrapper: Wrapper });

    result.current.mutate({ email: "owner@acme.test", password: "wrong" });

    await waitFor(() => expect(result.current.isError).toBe(true));
    expect(result.current.error).toBeInstanceOf(ApiError);
    expect((result.current.error as ApiError).message).toBe(
      "Invalid email or password.",
    );
    expect((result.current.error as ApiError).code).toBe(
      "auth.invalid_credentials",
    );
    expect((result.current.error as ApiError).status).toBe(401);

    // Failure path must NOT seed the Me cache or fire analytics.
    expect(qc.getQueryData([...ME_KEY])).toBeUndefined();
    expect(qc.getQueryData([...ME_PROBE_KEY])).toBeUndefined();
    expect(identify).not.toHaveBeenCalled();
    expect(track).not.toHaveBeenCalled();
  });
});

describe("useRegister — state transitions (#35)", () => {
  beforeEach(() => {
    identify.mockClear();
    resetIdentity.mockClear();
    track.mockClear();
  });

  it("idle → isPending → isSuccess on 200, writes Me cache, fires user.registered ONCE", async () => {
    const { handler, release } = heldHandler("/api/auth/register", () =>
      jsonOk(authedMe),
    );
    server.use(handler);

    const { qc, Wrapper } = makeWrapper();
    const { result } = renderHook(() => useRegister(), { wrapper: Wrapper });

    result.current.mutate({
      email: "owner@acme.test",
      password: "verystrongpass1",
      fullName: "Owner Name",
      companyName: "Acme Inc",
    });
    try {
      await waitFor(() => expect(result.current.isPending).toBe(true));
    } finally {
      release();
    }

    await waitFor(() => expect(result.current.isSuccess).toBe(true));
    expect(result.current.data).toEqual(authedMe);
    expect(qc.getQueryData([...ME_KEY])).toEqual(authedMe);
    expect(qc.getQueryData([...ME_PROBE_KEY])).toEqual(authedMe);
    expect(identify).toHaveBeenCalledTimes(1);
    expect(identify).toHaveBeenCalledWith(
      authedMe.userId,
      expect.objectContaining({ email: authedMe.email }),
    );
    expect(track).toHaveBeenCalledTimes(1);
    expect(track).toHaveBeenCalledWith("user.registered");
  });

  it("idle → isError on 409 dup email, error message is the server's human copy", async () => {
    server.use(
      http.post(url("/api/auth/register"), () =>
        jsonError(
          "auth.email_taken",
          "An account with that email already exists.",
          { status: 409 },
        ),
      ),
    );

    const { qc, Wrapper } = makeWrapper();
    const { result } = renderHook(() => useRegister(), { wrapper: Wrapper });

    result.current.mutate({
      email: "taken@acme.test",
      password: "verystrongpass1",
      fullName: "Owner Name",
      companyName: "Acme Inc",
    });

    await waitFor(() => expect(result.current.isError).toBe(true));
    expect(result.current.error).toBeInstanceOf(ApiError);
    expect((result.current.error as ApiError).message).toBe(
      "An account with that email already exists.",
    );
    expect((result.current.error as ApiError).code).toBe("auth.email_taken");
    expect((result.current.error as ApiError).status).toBe(409);
    expect(qc.getQueryData([...ME_KEY])).toBeUndefined();
    expect(track).not.toHaveBeenCalled();
  });
});
