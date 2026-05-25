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
import { useMe, type Me } from "./useAuth";
import { ApiError } from "@/lib/api";

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

describe("useMe({ skipRefresh: true }) — landing-page probe", () => {
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
  });

  afterEach(() => {
    vi.unstubAllGlobals();
    vi.restoreAllMocks();
  });

  it("anonymous visitor: 401 maps to null AND triggers no POST /api/auth/refresh (#30)", async () => {
    const { Wrapper } = makeWrapper();
    const { result } = renderHook(() => useMe({ skipRefresh: true }), { wrapper: Wrapper });

    await waitFor(() => expect(result.current.isSuccess).toBe(true));
    expect(result.current.data).toBeNull();

    // Acceptance criterion: "at most one auth request". Before the fix this was
    // two (GET /me + POST /refresh); after the fix it's exactly one.
    expect(calls).toHaveLength(1);
    expect(calls[0].url).toMatch(/\/api\/auth\/me$/);
    expect(calls[0].method).toBe("GET");
    // The smoking gun: no refresh attempt for anonymous visitors.
    expect(calls.some((c) => c.url.endsWith("/api/auth/refresh"))).toBe(false);
    expect(calls.some((c) => c.method === "POST")).toBe(false);
  });

  it("authenticated visitor on /: 200 resolves to the Me object so the nav swaps to 'Go to dashboard'", async () => {
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
  });

  it("non-401 error from /api/auth/me surfaces as a 500 ApiError (does not silently map to null)", async () => {
    // Guard: the 401→null mapping in useMe must be tight. A 500 from /me is
    // a real failure and must bubble up — otherwise the landing page would
    // silently render the logged-out CTAs through a backend outage.
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
  });

  afterEach(() => {
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
});
