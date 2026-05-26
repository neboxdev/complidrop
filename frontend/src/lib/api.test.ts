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

/**
 * Cookie + envelope contract (#35).
 *
 * Two contracts that the auth pages (and every authenticated dashboard
 * route) rely on:
 *
 *   1. Every verb sends `credentials: "include"` so the cd_session /
 *      cd_refresh cookies are attached on every request. A regression
 *      that dropped credentials would silently log every user out on
 *      every navigation.
 *   2. The error envelope `{ data: null, error: { code, message,
 *      correlationId? } }` is mapped to `ApiError` whose `.message`
 *      carries the server's HUMAN copy (not the dot-namespaced code).
 *      The login/register pages forward `err.message` straight to
 *      `toast.error(...)` — if api.ts ever fell back to `code` (or to
 *      `res.statusText`), users would see "auth.email_taken" instead of
 *      "An account with that email already exists."
 */
describe("api.* — credentials: include on every verb (#35)", () => {
  let fetchMock: ReturnType<typeof vi.fn>;
  const inits: RequestInit[] = [];

  beforeEach(() => {
    inits.length = 0;
    fetchMock = vi.fn(async (_input: RequestInfo | URL, init?: RequestInit) => {
      inits.push(init ?? {});
      return jsonResponse(200, envelope({ ok: true }));
    });
    vi.stubGlobal("fetch", fetchMock);
  });

  afterEach(() => {
    vi.unstubAllGlobals();
    vi.restoreAllMocks();
  });

  it.each(["get", "post", "put", "delete"] as const)(
    "api.%s sends credentials: 'include' so cd_session / cd_refresh cookies attach",
    async (verb) => {
      const callable = api[verb] as (path: string) => Promise<unknown>;
      await callable("/api/auth/me");
      expect(inits).toHaveLength(1);
      expect(inits[0].credentials).toBe("include");
    },
  );

  it("api.postForm sends credentials: 'include' AND lets fetch set the multipart boundary", async () => {
    const form = new FormData();
    form.append("file", new Blob(["x"], { type: "application/pdf" }), "x.pdf");
    await api.postForm("/api/documents/upload", form);

    expect(inits).toHaveLength(1);
    expect(inits[0].credentials).toBe("include");
    // Critical: lib/api.ts only sets Content-Type to application/json when
    // the body is NOT FormData — otherwise fetch must populate it with the
    // multipart boundary token. A regression that hard-coded application/json
    // would corrupt every portal/file upload silently.
    const headers = new Headers(inits[0].headers ?? {});
    expect(headers.get("Content-Type")).toBeNull();
  });

  it("api.post + idempotencyKey attaches the Idempotency-Key header", async () => {
    await api.post("/api/documents", undefined, {
      idempotencyKey: "00000000-0000-4000-8000-000000000000",
    });
    const headers = new Headers(inits[0].headers ?? {});
    expect(headers.get("Idempotency-Key")).toBe(
      "00000000-0000-4000-8000-000000000000",
    );
    expect(inits[0].credentials).toBe("include");
  });
});

describe("api.* — error-envelope → ApiError message mapping (#35)", () => {
  let fetchMock: ReturnType<typeof vi.fn>;

  beforeEach(() => {
    fetchMock = vi.fn();
    vi.stubGlobal("fetch", fetchMock);
  });

  afterEach(() => {
    vi.unstubAllGlobals();
    vi.restoreAllMocks();
  });

  // The pages call toast.error(err.message), so the EXACT property that
  // ends up in the user's face is ApiError.message. These tests pin that
  // it's the server's human copy, never the namespaced code.
  it.each([
    [401, "auth.invalid_credentials", "Invalid email or password."],
    [409, "auth.email_taken", "An account with that email already exists."],
    [400, "validation.email", "Enter a valid email."],
    [423, "auth.locked", "Account temporarily locked. Try again later."],
    [500, "server.error", "Something went wrong on our end."],
  ] as const)(
    "%d %s → ApiError.message is the human copy, ApiError.code preserves the code",
    async (status, code, message) => {
      fetchMock.mockResolvedValueOnce(
        jsonResponse(status, errorEnvelope(code, message)),
      );

      const err = await api
        .post("/api/auth/login", {})
        .catch((e) => e as ApiError);

      expect(err).toBeInstanceOf(ApiError);
      expect((err as ApiError).message).toBe(message);
      expect((err as ApiError).code).toBe(code);
      expect((err as ApiError).status).toBe(status);
    },
  );

  it("preserves correlationId from the error envelope for downstream Sentry / log lookups", async () => {
    fetchMock.mockResolvedValueOnce(
      new Response(
        JSON.stringify({
          data: null,
          error: {
            code: "server.error",
            message: "Boom.",
            correlationId: "trace-abc-123",
          },
        }),
        { status: 500, headers: { "Content-Type": "application/json" } },
      ),
    );

    const err = await api
      .post("/api/auth/login", {})
      .catch((e) => e as ApiError);

    expect(err).toBeInstanceOf(ApiError);
    expect((err as ApiError).correlationId).toBe("trace-abc-123");
  });

  it("non-JSON error body falls back to res.statusText, NOT to the bare status code", async () => {
    fetchMock.mockResolvedValueOnce(
      new Response("internal server error html page", {
        status: 502,
        statusText: "Bad Gateway",
        headers: { "Content-Type": "text/html" },
      }),
    );

    const err = await api
      .post("/api/auth/login", {})
      .catch((e) => e as ApiError);

    expect(err).toBeInstanceOf(ApiError);
    // The page forwards err.message to toast.error; statusText is at
    // least a real noun phrase, not a numeric string.
    expect((err as ApiError).message).toBe("Bad Gateway");
    expect((err as ApiError).status).toBe(502);
  });
});
