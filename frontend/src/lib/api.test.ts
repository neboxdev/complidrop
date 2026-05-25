/**
 * Pins the skipRefresh contract on the api.* verb surface.
 *
 * The request() wrapper has always honored skipRefresh, but until #30 only
 * api.post and api.postForm exposed the opts pass-through; api.get always
 * sent the flag as undefined. Anonymous visitors on the landing page
 * therefore took TWO auth round-trips per visit (GET /api/auth/me → 401,
 * then automatic POST /api/auth/refresh → 401 with no cd_refresh cookie).
 *
 * These tests fail if api.get either:
 *   - drops the skipRefresh opt (would reintroduce the wasted refresh), or
 *   - regresses the default-refresh behavior used by every other GET.
 */
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { api, ApiError } from "./api";

type FetchCall = { url: string; method: string };

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

describe("api.get skipRefresh contract", () => {
  const calls: FetchCall[] = [];
  let fetchMock: ReturnType<typeof vi.fn>;

  beforeEach(() => {
    calls.length = 0;
    fetchMock = vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
      const url = typeof input === "string" ? input : input.toString();
      const method = (init?.method ?? "GET").toUpperCase();
      calls.push({ url, method });
      // Default: 401 the requested resource, also 401 the refresh attempt
      // (mirroring an anonymous visitor with no cd_refresh cookie).
      if (url.endsWith("/api/auth/refresh")) {
        return jsonResponse(401, errorEnvelope("auth.unauthorized", "no refresh"));
      }
      return jsonResponse(401, errorEnvelope("auth.unauthorized", "unauthenticated"));
    });
    vi.stubGlobal("fetch", fetchMock);
  });

  afterEach(() => {
    vi.unstubAllGlobals();
    vi.restoreAllMocks();
  });

  it("makes exactly one request when skipRefresh: true and the server 401s", async () => {
    await expect(api.get("/api/auth/me", { skipRefresh: true })).rejects.toBeInstanceOf(ApiError);

    expect(calls).toHaveLength(1);
    expect(calls[0].url).toMatch(/\/api\/auth\/me$/);
    expect(calls[0].method).toBe("GET");
    // The smoking gun: no refresh attempt should ever appear.
    expect(calls.some((c) => c.url.endsWith("/api/auth/refresh"))).toBe(false);
  });

  it("still fires POST /api/auth/refresh on 401 when skipRefresh is not set (default behavior preserved)", async () => {
    // Other authenticated call sites rely on this — session refresh-on-expiry
    // must continue to work everywhere except the explicit skipRefresh opt-in.
    // Here refresh ALSO 401s, so doRefresh() returns false and the wrapper does
    // not retry: net 2 calls (original 401 + refresh attempt).
    await expect(api.get("/api/some-protected-thing")).rejects.toBeInstanceOf(ApiError);

    expect(calls).toHaveLength(2);
    expect(calls[0]).toEqual(expect.objectContaining({ method: "GET" }));
    expect(calls[0].url).toMatch(/\/api\/some-protected-thing$/);
    expect(calls[1]).toEqual(expect.objectContaining({ method: "POST" }));
    expect(calls[1].url).toMatch(/\/api\/auth\/refresh$/);
  });

  it("retries the original request after a successful refresh (default behavior)", async () => {
    // First /me 401, then refresh succeeds, then retried /me succeeds.
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
        return jsonResponse(401, errorEnvelope("auth.unauthorized", "expired"));
      }
      return jsonResponse(200, envelope({ ok: true }));
    });

    const result = await api.get<{ ok: boolean }>("/api/auth/me");
    expect(result).toEqual({ ok: true });

    expect(calls).toHaveLength(3);
    expect(calls[0]).toEqual(expect.objectContaining({ method: "GET" }));
    expect(calls[0].url).toMatch(/\/api\/auth\/me$/);
    expect(calls[1]).toEqual(expect.objectContaining({ method: "POST" }));
    expect(calls[1].url).toMatch(/\/api\/auth\/refresh$/);
    expect(calls[2]).toEqual(expect.objectContaining({ method: "GET" }));
    expect(calls[2].url).toMatch(/\/api\/auth\/me$/);
  });

  it("does NOT retry after a 401 when skipRefresh: true, even if refresh would have succeeded", async () => {
    // Belt-and-braces: even if the refresh endpoint were reachable, skipRefresh
    // must short-circuit the retry path entirely.
    fetchMock.mockImplementation(async (input: RequestInfo | URL, init?: RequestInit) => {
      const url = typeof input === "string" ? input : input.toString();
      const method = (init?.method ?? "GET").toUpperCase();
      calls.push({ url, method });
      if (url.endsWith("/api/auth/refresh")) {
        return new Response(null, { status: 204 });
      }
      return jsonResponse(401, errorEnvelope("auth.unauthorized", "unauthenticated"));
    });

    await expect(api.get("/api/auth/me", { skipRefresh: true })).rejects.toBeInstanceOf(ApiError);

    expect(calls).toHaveLength(1);
    expect(calls[0].url).toMatch(/\/api\/auth\/me$/);
  });

  it("passes through to a 200 on /api/auth/me when authenticated (skipRefresh path)", async () => {
    fetchMock.mockImplementation(async (input: RequestInfo | URL, init?: RequestInit) => {
      const url = typeof input === "string" ? input : input.toString();
      const method = (init?.method ?? "GET").toUpperCase();
      calls.push({ url, method });
      return jsonResponse(200, envelope({ email: "owner@example.com" }));
    });

    const result = await api.get<{ email: string }>("/api/auth/me", { skipRefresh: true });

    expect(result).toEqual({ email: "owner@example.com" });
    expect(calls).toHaveLength(1);
  });
});
