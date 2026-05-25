/**
 * useMe() is rendered on the public landing page — the highest-traffic page in
 * the product — for EVERY visitor, authenticated or not. This file pins two
 * things:
 *
 *   1. Anonymous (401 from /api/auth/me) must NOT trigger the automatic refresh
 *      retry inside lib/api.ts. Before #30 it did, costing every anonymous
 *      landing-page visit a wasted POST /api/auth/refresh round-trip.
 *
 *   2. Authenticated (200 from /api/auth/me) still resolves to a Me object so
 *      the nav can swap to "Go to dashboard".
 *
 * Tests use a real QueryClientProvider + mocked global fetch — the same pattern
 * ADR 0003 reserves for "tests that must exercise a hook's own logic."
 */
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { renderHook, waitFor } from "@testing-library/react";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import type { ReactNode } from "react";
import { useMe } from "./useAuth";

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

function wrapper() {
  // Disable retries so a 401 doesn't get re-fetched by TanStack Query itself
  // (we're asserting on the network surface from lib/api.ts, not on TQ retries).
  const qc = new QueryClient({
    defaultOptions: { queries: { retry: false, gcTime: 0 } },
  });
  function QueryWrapper({ children }: { children: ReactNode }) {
    return <QueryClientProvider client={qc}>{children}</QueryClientProvider>;
  }
  return QueryWrapper;
}

type FetchCall = { url: string; method: string };

describe("useMe() landing-page probe", () => {
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
    const { result } = renderHook(() => useMe(), { wrapper: wrapper() });

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

  it("authenticated visitor: 200 resolves to the Me object so the nav can swap to 'Go to dashboard'", async () => {
    const me = {
      userId: "u1",
      organizationId: "o1",
      email: "owner@example.com",
      fullName: "Owner",
      role: "admin",
      plan: "pro",
      organizationName: "Acme",
      timeZone: "UTC",
    };
    fetchMock.mockImplementation(async (input: RequestInfo | URL, init?: RequestInit) => {
      const url = typeof input === "string" ? input : input.toString();
      const method = (init?.method ?? "GET").toUpperCase();
      calls.push({ url, method });
      return jsonResponse(200, envelope(me));
    });

    const { result } = renderHook(() => useMe(), { wrapper: wrapper() });

    await waitFor(() => expect(result.current.isSuccess).toBe(true));
    expect(result.current.data).toEqual(me);

    expect(calls).toHaveLength(1);
    expect(calls[0].url).toMatch(/\/api\/auth\/me$/);
  });

  it("non-401 error from /api/auth/me still surfaces as an error (does not silently map to null)", async () => {
    // Guard: the 401→null mapping in useMe must be tight. A 500 from /me
    // is a genuine failure and should bubble up — otherwise the landing page
    // would silently render the logged-out CTAs through a server outage.
    fetchMock.mockImplementation(async (input: RequestInfo | URL, init?: RequestInit) => {
      const url = typeof input === "string" ? input : input.toString();
      const method = (init?.method ?? "GET").toUpperCase();
      calls.push({ url, method });
      return jsonResponse(500, errorEnvelope("server.error", "boom"));
    });

    const { result } = renderHook(() => useMe(), { wrapper: wrapper() });

    await waitFor(() => expect(result.current.isError).toBe(true));
    expect(calls.some((c) => c.url.endsWith("/api/auth/refresh"))).toBe(false);
  });
});
