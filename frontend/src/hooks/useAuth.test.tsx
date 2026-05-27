/**
 * useMe() splits two responsibilities by an opts flag (#30):
 *
 *   - useMe({ skipRefresh: true })  — the LANDING-PAGE probe. Anonymous
 *     visitors must NOT pay the automatic POST /api/auth/refresh retry that
 *     lib/api.ts fires on a 401. Before #30 they did, costing every anonymous
 *     visit a wasted round-trip on the highest-traffic public page.
 *
 *   - useMe()                       — the AUTHENTICATED-ROUTE check. The
 *     dashboard layout gates rendering on me.data and redirects to /login
 *     when it's null. A user with an expired cd_session but valid cd_refresh
 *     hitting /dashboard directly relies on the auto-refresh-on-401 inside
 *     lib/api.ts. This file pins that path so a future "always skip refresh
 *     on useMe" regression can't slip back in.
 *
 * Tests use a real QueryClientProvider + mocked global fetch — the same
 * pattern ADR 0003 reserves for "tests that must exercise a hook's own logic."
 */
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { renderHook, waitFor } from "@testing-library/react";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import type { ReactNode } from "react";
import { SESSION_HINT_COOKIE, useMe, type Me } from "./useAuth";
import { ApiError } from "@/lib/api";

/**
 * Set / clear `document.cookie` for the duration of one test. jsdom honors
 * `document.cookie = "name=value"` writes; the matching expired write
 * removes the cookie (Expires=epoch). The afterEach in each describe block
 * resets cookies so a test's hint state never leaks into the next test.
 */
function setHintCookie() {
  document.cookie = `${SESSION_HINT_COOKIE}=1; path=/`;
}

function clearHintCookie() {
  document.cookie = `${SESSION_HINT_COOKIE}=; path=/; expires=Thu, 01 Jan 1970 00:00:00 GMT`;
}

// Analytics are fire-and-forget side effects in the other hooks; mock them out
// so a missing PostHog key in the test env doesn't surface as a console warning.
vi.mock("@/lib/analytics", () => ({
  identify: vi.fn(),
  resetIdentity: vi.fn(),
  track: vi.fn(),
}));

function envelope<T>(data: T) {
  return { data, error: null };
}

function errorEnvelope(code: string, message: string) {
  return { data: null, error: { code, message } };
}

function jsonResponse(status: number, body: unknown): Response {
  return new Response(JSON.stringify(body), {
    status,
    headers: { "Content-Type": "application/json" },
  });
}

function makeWrapper() {
  // Disable retries so a 401 doesn't get re-fetched by TanStack Query itself
  // (we're asserting on the network surface from lib/api.ts, not on TQ retries).
  const qc = new QueryClient({
    defaultOptions: { queries: { retry: false, gcTime: 0 } },
  });
  function QueryWrapper({ children }: { children: ReactNode }) {
    return <QueryClientProvider client={qc}>{children}</QueryClientProvider>;
  }
  return { qc, Wrapper: QueryWrapper };
}

type FetchCall = { url: string; method: string };

const ME: Me = {
  userId: "u1",
  organizationId: "o1",
  email: "owner@example.com",
  fullName: "Owner",
  role: "admin",
  plan: "pro",
  organizationName: "Acme",
  timeZone: "UTC",
};

describe("useMe({ skipRefresh: true }) — landing-page probe (#69)", () => {
  const calls: FetchCall[] = [];
  let fetchMock: ReturnType<typeof vi.fn>;

  beforeEach(() => {
    calls.length = 0;
    fetchMock = vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
      const url = typeof input === "string" ? input : input.toString();
      const method = (init?.method ?? "GET").toUpperCase();
      calls.push({ url, method });
      return jsonResponse(401, errorEnvelope("auth.unauthorized", "unauthenticated"));
    });
    vi.stubGlobal("fetch", fetchMock);
    // Default each test to "no hint cookie" — the most common state
    // (anonymous visitor). Tests that simulate an authed-at-some-point
    // browser set the hint explicitly via `setHintCookie()`.
    clearHintCookie();
  });

  afterEach(() => {
    clearHintCookie();
    vi.unstubAllGlobals();
    vi.restoreAllMocks();
  });

  // ── AC #1 from the ticket: zero auth calls when no hint cookie. This
  // is the whole point of #69 — the lone /me round-trip that survived
  // #30 must NOT fire when the browser has never been authenticated.
  it("anonymous visitor (no cd_session_hint): zero fetch calls, query stays disabled", async () => {
    const { Wrapper } = makeWrapper();
    const { result } = renderHook(() => useMe({ skipRefresh: true }), { wrapper: Wrapper });

    // The gate (`enabled: hasSessionHint()`) flips the query to disabled
    // synchronously on mount. There's no "loading then idle" interim
    // because the queryFn never runs: status goes idle → idle. Wait a
    // tick to let any scheduled microtask resolve, then assert zero
    // network activity.
    await new Promise((r) => setTimeout(r, 10));
    expect(calls).toHaveLength(0);
    // `enabled: false` on TanStack Query => `isLoading: false` and
    // `data: undefined`. The landing page's `!!me` derives `authed:
    // false`, matching the logged-out-CTA branch.
    expect(result.current.isLoading).toBe(false);
    expect(result.current.fetchStatus).toBe("idle");
    expect(result.current.data).toBeUndefined();
  });

  // ── AC #2: hint cookie present + valid session → exactly one /me 200.
  it("authenticated visitor (hint + valid session): one GET /api/auth/me → 200, no refresh", async () => {
    setHintCookie();
    fetchMock.mockImplementation(async (input: RequestInfo | URL, init?: RequestInit) => {
      const url = typeof input === "string" ? input : input.toString();
      const method = (init?.method ?? "GET").toUpperCase();
      calls.push({ url, method });
      return jsonResponse(200, envelope(ME));
    });

    const { Wrapper } = makeWrapper();
    const { result } = renderHook(() => useMe({ skipRefresh: true }), { wrapper: Wrapper });

    await waitFor(() => expect(result.current.isSuccess).toBe(true));
    expect(result.current.data).toEqual(ME);

    expect(calls).toHaveLength(1);
    expect(calls[0].url).toMatch(/\/api\/auth\/me$/);
    expect(calls[0].method).toBe("GET");
    expect(calls.some((c) => c.url.endsWith("/api/auth/refresh"))).toBe(false);
  });

  // ── AC #3: stale hint (cookie present but session expired or
  // tampered-cleared) → exactly one /me 401. `skipRefresh` keeps the
  // cost at one call — the SAME as today's worst case, only triggered
  // by edge tampering (e.g. DevTools-cleared cd_session) instead of by
  // every anonymous visit.
  it("stale hint (hint cookie present but session cleared): one /me 401, no /refresh fired", async () => {
    setHintCookie();
    // fetchMock default = 401 envelope.

    const { Wrapper } = makeWrapper();
    const { result } = renderHook(() => useMe({ skipRefresh: true }), { wrapper: Wrapper });

    await waitFor(() => expect(result.current.isSuccess).toBe(true));
    // 401 maps to null via the queryFn's try/catch (same as pre-#69).
    expect(result.current.data).toBeNull();

    // Exactly one call — the smoking gun that `skipRefresh` is still in
    // effect on the stale-hint path. Two calls (a follow-up POST
    // /refresh) would mean a regression on #30's contract.
    expect(calls).toHaveLength(1);
    expect(calls[0].url).toMatch(/\/api\/auth\/me$/);
    expect(calls.some((c) => c.url.endsWith("/api/auth/refresh"))).toBe(false);
    expect(calls.some((c) => c.method === "POST")).toBe(false);
  });

  it("non-401 error from /api/auth/me surfaces as a 500 ApiError (does not silently map to null)", async () => {
    // Guard: the 401→null mapping in useMe must be tight. A 500 from /me is
    // a real failure and must bubble up — otherwise the landing page would
    // silently render the logged-out CTAs through a backend outage. Requires
    // the hint cookie so the query actually fires (post-#69 gating).
    setHintCookie();
    fetchMock.mockImplementation(async (input: RequestInfo | URL, init?: RequestInit) => {
      const url = typeof input === "string" ? input : input.toString();
      const method = (init?.method ?? "GET").toUpperCase();
      calls.push({ url, method });
      return jsonResponse(500, errorEnvelope("server.error", "boom"));
    });

    const { Wrapper } = makeWrapper();
    const { result } = renderHook(() => useMe({ skipRefresh: true }), { wrapper: Wrapper });

    await waitFor(() => expect(result.current.isError).toBe(true));
    expect(result.current.error).toBeInstanceOf(ApiError);
    expect((result.current.error as ApiError).status).toBe(500);
  });
});

describe("useMe() — authenticated routes (dashboard layout, settings)", () => {
  const calls: FetchCall[] = [];
  let fetchMock: ReturnType<typeof vi.fn>;

  beforeEach(() => {
    calls.length = 0;
    fetchMock = vi.fn();
    vi.stubGlobal("fetch", fetchMock);
    // The plain `useMe()` (no opts) is deliberately NOT gated by the
    // hint cookie (#69) — dashboard layout depends on its
    // 401→refresh→retry chain to keep an expired-cd_session user on
    // their page. Clear any leakage from a previous describe so the
    // assertions here exercise that path without false confidence.
    clearHintCookie();
  });

  afterEach(() => {
    clearHintCookie();
    vi.unstubAllGlobals();
    vi.restoreAllMocks();
  });

  it("expired cd_session + valid cd_refresh: 401 → POST /refresh 204 → retry /me 200 (no /login bounce)", async () => {
    // This is the canonical authenticated entry point: a user with a valid
    // refresh cookie hits /dashboard directly. The dashboard layout depends
    // on this path to NOT return null (otherwise it redirects to /login).
    // Regression scenario from #30 review: hard-coding skipRefresh: true on
    // useMe would have made this path return null instead of retrying.
    let meCalls = 0;
    fetchMock.mockImplementation(async (input: RequestInfo | URL, init?: RequestInit) => {
      const url = typeof input === "string" ? input : input.toString();
      const method = (init?.method ?? "GET").toUpperCase();
      calls.push({ url, method });
      if (url.endsWith("/api/auth/refresh")) {
        return new Response(null, { status: 204 });
      }
      meCalls++;
      if (meCalls === 1) {
        return jsonResponse(401, errorEnvelope("auth.unauthorized", "session expired"));
      }
      return jsonResponse(200, envelope(ME));
    });

    const { Wrapper } = makeWrapper();
    const { result } = renderHook(() => useMe(), { wrapper: Wrapper });

    await waitFor(() => expect(result.current.isSuccess).toBe(true));
    expect(result.current.data).toEqual(ME);

    // 1: initial /me 401, 2: POST /refresh 204, 3: retried /me 200.
    expect(calls).toHaveLength(3);
    expect(calls[0]).toEqual(expect.objectContaining({ method: "GET" }));
    expect(calls[0].url).toMatch(/\/api\/auth\/me$/);
    expect(calls[1]).toEqual(expect.objectContaining({ method: "POST" }));
    expect(calls[1].url).toMatch(/\/api\/auth\/refresh$/);
    expect(calls[2]).toEqual(expect.objectContaining({ method: "GET" }));
    expect(calls[2].url).toMatch(/\/api\/auth\/me$/);
  });

  it("truly anonymous on a dashboard route: 401 → refresh 401 → useMe returns null so the layout can redirect", async () => {
    // The other half of the dashboard layout contract: when refresh ALSO
    // 401s (no cd_refresh cookie), the wrapper does not retry and useMe
    // returns null. The layout's useEffect then sends the user to /login.
    fetchMock.mockImplementation(async (input: RequestInfo | URL, init?: RequestInit) => {
      const url = typeof input === "string" ? input : input.toString();
      const method = (init?.method ?? "GET").toUpperCase();
      calls.push({ url, method });
      return jsonResponse(401, errorEnvelope("auth.unauthorized", "unauthenticated"));
    });

    const { Wrapper } = makeWrapper();
    const { result } = renderHook(() => useMe(), { wrapper: Wrapper });

    await waitFor(() => expect(result.current.isSuccess).toBe(true));
    expect(result.current.data).toBeNull();

    // 1: initial /me 401, 2: POST /refresh 401 (also fails, no retry).
    expect(calls).toHaveLength(2);
    expect(calls[1].url).toMatch(/\/api\/auth\/refresh$/);
    expect(calls[1].method).toBe("POST");
  });

  it("probe and authoritative reads use distinct cache keys — probe-null cannot poison the dashboard read", async () => {
    // Regression check from #30: if both call sites shared queryKey ["auth","me"],
    // a landing-page probe that resolved to null would be returned from the
    // cache for the dashboard's useMe() within staleTime, skipping the actual
    // refresh-and-retry path entirely. Distinct keys keep the two consumers
    // independent. Co-render both in ONE QueryClient and check the fetch
    // count — if the cache were shared, the second hook would never fetch.
    //
    // #69: the probe is now gated on `cd_session_hint`. To exercise the
    // stale-hint branch (probe fires and 401s), the test must explicitly
    // set the hint cookie BEFORE rendering. Without it, the probe is
    // disabled, the cache key never receives a write, and the test would
    // pass trivially without proving cache separation.
    setHintCookie();
    let meCalls = 0;
    fetchMock.mockImplementation(async (input: RequestInfo | URL, init?: RequestInit) => {
      const url = typeof input === "string" ? input : input.toString();
      const method = (init?.method ?? "GET").toUpperCase();
      calls.push({ url, method });
      if (url.endsWith("/api/auth/refresh")) {
        return new Response(null, { status: 204 });
      }
      meCalls++;
      // 1st /me (probe): 401. 2nd /me (dashboard initial): 401. 3rd (retry): 200.
      if (meCalls === 1) {
        return jsonResponse(401, errorEnvelope("auth.unauthorized", "no probe"));
      }
      if (meCalls === 2) {
        return jsonResponse(401, errorEnvelope("auth.unauthorized", "expired"));
      }
      return jsonResponse(200, envelope(ME));
    });

    // ONE QueryClient shared across both renders so we genuinely test cache
    // separation rather than per-render isolation.
    const { Wrapper } = makeWrapper();
    const probe = renderHook(() => useMe({ skipRefresh: true }), { wrapper: Wrapper });
    await waitFor(() => expect(probe.result.current.isSuccess).toBe(true));
    expect(probe.result.current.data).toBeNull();

    const authed = renderHook(() => useMe(), { wrapper: Wrapper });
    await waitFor(() => expect(authed.result.current.isSuccess).toBe(true));
    expect(authed.result.current.data).toEqual(ME);

    // Expected: probe (1 /me 401) + authed (1 /me 401, 1 /refresh 204, 1 retried /me 200) = 4.
    // If the cache were shared, authed would read cached null and total would be 1.
    expect(calls).toHaveLength(4);
  });

  // ── #69 regression: the plain `useMe()` (no opts) MUST keep firing
  // even when the hint cookie is absent. Dashboard layout, settings,
  // and every authenticated route call it without `skipRefresh`; if a
  // future refactor over-eagerly applied `enabled: hasSessionHint()`
  // to the no-opts branch too, a user with an expired cd_session who
  // happened to have lost the hint cookie would silently render as
  // anonymous in the dashboard layout and bounce to /login instead of
  // recovering via /api/auth/refresh.
  it("plain useMe() (no opts) is NOT gated by the hint cookie — still fires the 401→refresh→retry chain", async () => {
    clearHintCookie(); // explicit: gate would suppress the call if applied here.
    let meCalls = 0;
    fetchMock.mockImplementation(async (input: RequestInfo | URL, init?: RequestInit) => {
      const url = typeof input === "string" ? input : input.toString();
      const method = (init?.method ?? "GET").toUpperCase();
      calls.push({ url, method });
      if (url.endsWith("/api/auth/refresh")) {
        return new Response(null, { status: 204 });
      }
      meCalls++;
      if (meCalls === 1) {
        return jsonResponse(401, errorEnvelope("auth.unauthorized", "session expired"));
      }
      return jsonResponse(200, envelope(ME));
    });

    const { Wrapper } = makeWrapper();
    const { result } = renderHook(() => useMe(), { wrapper: Wrapper });

    await waitFor(() => expect(result.current.isSuccess).toBe(true));
    expect(result.current.data).toEqual(ME);
    // Same three-step recovery as the canonical authenticated test
    // above — proof that the no-opts branch is exempt from #69's gate.
    expect(calls).toHaveLength(3);
    expect(calls[1].url).toMatch(/\/api\/auth\/refresh$/);
  });
});
