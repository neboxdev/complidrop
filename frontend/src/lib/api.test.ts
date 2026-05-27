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
import { api, ApiError, GENERIC_FALLBACK_MESSAGE } from "./api";

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

  it("burst 401s: five concurrent api.get calls share exactly ONE POST /api/auth/refresh (#68)", async () => {
    // The race #68 fixes: with the previous `refreshing = refreshing
    // ?? doRefresh()` + eager null pattern, a fourth (or more) 401
    // arriving AFTER the first batch nulled the singleton but BEFORE
    // its retry fetches completed would fire a SECOND doRefresh()
    // POST racing the first batch's retries. With refcounted reset,
    // the singleton lives exactly as long as any consumer is still
    // inside the 401-recovery window. Five concurrent api.get calls
    // that all 401 must therefore share exactly ONE refresh POST.
    //
    // Refresh succeeds, retry fetches return 200 — so every consumer
    // unwinds cleanly, refcount returns to zero, and a follow-up call
    // can start a fresh refresh.
    let meCalls = 0;
    let refreshCalls = 0;
    fetchMock.mockImplementation(async (input: RequestInfo | URL, init?: RequestInit) => {
      const url = typeof input === "string" ? input : input.toString();
      const method = (init?.method ?? "GET").toUpperCase();
      calls.push({ url, method });
      if (url.endsWith("/api/auth/refresh")) {
        refreshCalls++;
        // LOAD-BEARING: this `setTimeout(0)` forces a macrotask
        // boundary inside doRefresh() so all five concurrent 401
        // recoveries land on the same singleton BEFORE the refresh
        // resolves. Without it the first awaiter could resolve, run
        // its retry, and (under the OLD buggy pre-#68 code) null out
        // the singleton — letting the remaining four 401s each fire
        // their own doRefresh, defeating the coalescing the test
        // exists to verify. DO NOT REMOVE without replacing the
        // synchronization with another macrotask boundary (e.g.
        // vi.useFakeTimers + advanceTimersByTime).
        await new Promise<void>((resolve) => setTimeout(resolve, 0));
        return new Response(null, { status: 204 });
      }
      meCalls++;
      if (meCalls <= 5) {
        return jsonResponse(401, errorEnvelope("auth.unauthorized", "expired"));
      }
      return jsonResponse(200, envelope({ email: "owner@example.com" }));
    });

    const results = await Promise.all(
      Array.from({ length: 5 }, () =>
        api.get<{ email: string }>("/api/auth/me"),
      ),
    );

    // All five callers got the post-refresh successful response.
    expect(results).toHaveLength(5);
    for (const r of results) {
      expect(r).toEqual({ email: "owner@example.com" });
    }

    // The smoking gun: exactly ONE refresh POST, not five.
    expect(refreshCalls).toBe(1);
    // Sanity: 5 first attempts (all 401) + 1 refresh + 5 retry attempts = 11.
    const refreshPosts = calls.filter((c) =>
      c.url.endsWith("/api/auth/refresh"),
    );
    expect(refreshPosts).toHaveLength(1);
  });

  it("staggered race: a 401 arriving DURING the first batch's retry-unwind window shares the same refresh — does NOT fire a second doRefresh (#68)", async () => {
    // This is the smoking-gun test for the #68 race. The naive
    // simultaneous-Promise.all burst test above pins the coalescing
    // invariant but does NOT differentiate the OLD buggy code from
    // the NEW fix — under the old code, 5 simultaneously-launched
    // 401s also share ONE doRefresh because they all reach the 401
    // branch before any of them awaits past the refresh.
    //
    // The actual #68 race needs STAGGERED arrival:
    //   1. Burst A 401s, starts refresh (singleton created).
    //   2. Refresh resolves; A's await on refresh resolves.
    //   3. Under OLD code, A immediately `refreshing = null`. A
    //      hasn't completed its retry fetch yet.
    //   4. A LATE caller B 401s in this window. OLD code: refreshing
    //      is null → B fires a SECOND doRefresh.
    //   5. NEW code: refcount still > 0 from A's in-flight retry, so
    //      `refreshing` is still the resolved singleton. B reuses
    //      it. Exactly ONE doRefresh.
    //
    // Externally-controlled deferred promises pin the timing.
    let resolveRefresh: ((res: Response) => void) | null = null;
    let resolveRetryA: ((res: Response) => void) | null = null;
    const refreshDeferred = new Promise<Response>((r) => {
      resolveRefresh = r;
    });
    const retryADeferred = new Promise<Response>((r) => {
      resolveRetryA = r;
    });

    let refreshCalls = 0;
    let meCalls = 0;
    fetchMock.mockImplementation((input: RequestInfo | URL, init?: RequestInit) => {
      const url = typeof input === "string" ? input : input.toString();
      const method = (init?.method ?? "GET").toUpperCase();
      calls.push({ url, method });
      if (url.endsWith("/api/auth/refresh")) {
        refreshCalls++;
        return refreshDeferred;
      }
      meCalls++;
      // First /me from A: 401. A's retry (the next /me) awaits the
      // externally-controlled retryADeferred so we can hold it open
      // and inject B's late 401 during that window.
      if (meCalls === 1) {
        return Promise.resolve(
          jsonResponse(401, errorEnvelope("auth.unauthorized", "expired")),
        );
      }
      if (meCalls === 2) {
        return retryADeferred;
      }
      // B's first /me: 401. B's retry: 200.
      if (meCalls === 3) {
        return Promise.resolve(
          jsonResponse(401, errorEnvelope("auth.unauthorized", "expired")),
        );
      }
      return Promise.resolve(jsonResponse(200, envelope({ email: "B@example.com" })));
    });

    // Launch A. It will hit 401, enter the refresh branch, and await
    // the singleton (which is awaiting refreshDeferred).
    const promiseA = api.get<{ email: string }>("/api/auth/me");

    // Yield enough microtasks for A's initial fetch to resolve and
    // for A to reach `await getRefreshPromise()`. Two microtask
    // boundaries cover: (1) initial fetch promise resolve, (2) A
    // entering the 401 branch and assigning the singleton.
    await Promise.resolve();
    await Promise.resolve();

    // Resolve refresh. A's await on the singleton resolves; A starts
    // its retry fetch (which awaits retryADeferred — still pending).
    // Under OLD code, A would now have `refreshing = null` and any
    // late 401 would fire a second doRefresh.
    resolveRefresh!(new Response(null, { status: 204 }));

    // Yield for A's continuation to run (resolve the refresh promise,
    // then dispatch the retry fetch). retryADeferred remains pending.
    await Promise.resolve();
    await Promise.resolve();
    await Promise.resolve();

    // NOW launch B — the LATE caller that arrives during A's
    // retry-unwind window. Under OLD code, this would see refreshing
    // === null and fire a SECOND doRefresh. Under NEW code, refcount
    // still > 0 (A's retry hasn't unwound), singleton is alive, B
    // joins the existing resolved promise.
    const promiseB = api.get<{ email: string }>("/api/auth/me");

    // Yield for B to enter the 401 branch and call getRefreshPromise.
    await Promise.resolve();
    await Promise.resolve();
    await Promise.resolve();

    // Release A's retry so A unwinds. B's retry will also fire.
    resolveRetryA!(jsonResponse(200, envelope({ email: "A@example.com" })));

    const [resultA, resultB] = await Promise.all([promiseA, promiseB]);
    expect(resultA).toEqual({ email: "A@example.com" });
    expect(resultB).toEqual({ email: "B@example.com" });

    // The smoking gun: B did NOT fire a second doRefresh because the
    // refcount kept the singleton alive while A's retry was in flight.
    expect(refreshCalls).toBe(1);
  });

  it("burst with failing refresh: all callers throw ApiError, refcount drains via finally, follow-up burst can refresh again (#68)", async () => {
    // The new try/finally in request() promises that releaseRefresh()
    // runs even when `refreshed === false` (refresh fails). Without
    // this test, a future refactor that conditionally skipped
    // releaseRefresh on the failed path would slip past the existing
    // suite: burst 1's refcount would NEVER decrement, the singleton
    // would stay alive forever holding a stale "false" result, and
    // every subsequent burst would silently skip refresh entirely.
    //
    // Mock contract:
    //   Burst 1 ("fail"):    3 /me → 401, refresh → 401 (doRefresh
    //                        returns false), 0 retries. All 3 reach
    //                        the !res.ok block and throw ApiError(401).
    //   Burst 2 ("succeed"): 3 /me → 401, refresh → 204, 3 retries
    //                        → 200. All 3 callers return data.
    // First 6 /me fetches are 401 (3 burst 1 initial + 3 burst 2
    // initial); /me 7-9 are 200 (burst 2's retries only — burst 1
    // had no retries because refresh failed).
    let refreshCalls = 0;
    let meCalls = 0;
    let refreshShouldSucceed = false;
    fetchMock.mockImplementation(async (input: RequestInfo | URL, init?: RequestInit) => {
      const url = typeof input === "string" ? input : input.toString();
      const method = (init?.method ?? "GET").toUpperCase();
      calls.push({ url, method });
      if (url.endsWith("/api/auth/refresh")) {
        refreshCalls++;
        // Tiny await to let all concurrent 401s land on the singleton
        // before the refresh resolves.
        await new Promise<void>((r) => setTimeout(r, 0));
        return refreshShouldSucceed
          ? new Response(null, { status: 204 })
          : new Response(null, { status: 401 });
      }
      meCalls++;
      if (meCalls <= 6) {
        return jsonResponse(401, errorEnvelope("auth.unauthorized", "expired"));
      }
      return jsonResponse(200, envelope({ email: "owner@example.com" }));
    });

    // Burst 1: 3 concurrent /me, refresh fails. All three should
    // throw ApiError(401), refcount drains to 0 via finally.
    const results = await Promise.allSettled(
      Array.from({ length: 3 }, () => api.get("/api/auth/me")),
    );
    for (const r of results) {
      expect(r.status).toBe("rejected");
      expect((r as PromiseRejectedResult).reason).toBeInstanceOf(ApiError);
      expect((r as PromiseRejectedResult).reason.status).toBe(401);
    }
    expect(refreshCalls).toBe(1);

    // Burst 2: 3 concurrent /me, refresh now succeeds. If burst 1's
    // refcount leaked, the singleton would still hold its stale
    // "false" result and these calls would skip the refresh —
    // refreshCalls would stay at 1.
    refreshShouldSucceed = true;
    const burst2 = await Promise.all(
      Array.from({ length: 3 }, () =>
        api.get<{ email: string }>("/api/auth/me"),
      ),
    );
    for (const r of burst2) {
      expect(r).toEqual({ email: "owner@example.com" });
    }
    expect(refreshCalls).toBe(2);
  });

  it("refresh succeeds + retry fetch THROWS: refcount drains via try/finally so a follow-up burst can refresh again (#68 followup)", async () => {
    // The try/finally in request() guarantees releaseRefresh() runs
    // even when fetchOrFriendlyThrow throws on the retry — but no
    // existing test pins it. A regression that moved releaseRefresh()
    // inside the `if (refreshed)` block (rather than the finally)
    // would leak refcount on this path, leaving the singleton stuck
    // alive and silencing the next 401's refresh attempt forever.
    //
    // Sequence: 3 concurrent /me 401s → 1 refresh (204) → 3 retries
    // all reject with TypeError → 3 ApiError(network.unreachable)
    // propagate out, refcount drains to 0 via finally → second burst
    // confirms a new refresh fires.
    let meCalls = 0;
    let refreshCalls = 0;
    let phase: "throw_retry" | "succeed" = "throw_retry";
    fetchMock.mockImplementation(async (input: RequestInfo | URL) => {
      const url = typeof input === "string" ? input : input.toString();
      if (url.endsWith("/api/auth/refresh")) {
        refreshCalls++;
        await new Promise<void>((r) => setTimeout(r, 0));
        return new Response(null, { status: 204 });
      }
      meCalls++;
      if (phase === "throw_retry") {
        // First 3 fetches are 401 (trigger refresh), then 3 retries
        // throw TypeError (network failure post-refresh).
        if (meCalls <= 3) {
          return jsonResponse(401, errorEnvelope("auth.unauthorized", "expired"));
        }
        throw new TypeError("Failed to fetch");
      }
      // Burst 2 (phase=succeed): 3 401 + 3 retry-200.
      if (meCalls <= 9) {
        return jsonResponse(401, errorEnvelope("auth.unauthorized", "expired"));
      }
      return jsonResponse(200, envelope({ email: "owner@example.com" }));
    });

    // Burst 1: all 3 throw ApiError(network.unreachable). Refcount
    // must drain via finally even though the retries threw.
    const results = await Promise.allSettled(
      Array.from({ length: 3 }, () => api.get("/api/auth/me")),
    );
    for (const r of results) {
      expect(r.status).toBe("rejected");
      expect((r as PromiseRejectedResult).reason).toBeInstanceOf(ApiError);
      expect((r as PromiseRejectedResult).reason.code).toBe("network.unreachable");
    }
    expect(refreshCalls).toBe(1);

    // Burst 2: if refcount leaked, the singleton would still hold the
    // stale "true" result from burst 1 and these calls would skip
    // refresh entirely. The new refresh confirms try/finally drained
    // correctly even on the retry-throw path.
    phase = "succeed";
    const burst2 = await Promise.all(
      Array.from({ length: 3 }, () =>
        api.get<{ email: string }>("/api/auth/me"),
      ),
    );
    for (const r of burst2) {
      expect(r).toEqual({ email: "owner@example.com" });
    }
    expect(refreshCalls).toBe(2);
  });

  it("refresh refcount resets cleanly between bursts — a follow-up burst fires its own refresh (#68)", async () => {
    // Pins the inverse of the test above: once the first burst's
    // retries unwind, the singleton must be torn down so the NEXT
    // 401-burst can start its own refresh. A bug where releaseRefresh
    // failed to reset (or refcount stayed > 0) would manifest as the
    // second burst reusing a stale "true" result from the first.
    //
    // Sequencing strategy: per-burst we want the initial 3 fetches to
    // all 401 (so they all trigger refresh), and the post-refresh 3
    // retries to all 200. A burst-aware counter (rather than a global
    // request counter) is the natural fit — meCalls % 6 < 3 ⇒ 401,
    // meCalls % 6 >= 3 ⇒ 200, mod-6 because each burst sends 3 initial
    // + 3 retry = 6 /me fetches.
    let meCalls = 0;
    let refreshCalls = 0;
    fetchMock.mockImplementation(async (input: RequestInfo | URL, init?: RequestInit) => {
      const url = typeof input === "string" ? input : input.toString();
      const method = (init?.method ?? "GET").toUpperCase();
      calls.push({ url, method });
      if (url.endsWith("/api/auth/refresh")) {
        refreshCalls++;
        // Same micro-delay as the burst-coalescing test so all
        // concurrent 401s land on the singleton before it resolves.
        await new Promise<void>((resolve) => setTimeout(resolve, 0));
        return new Response(null, { status: 204 });
      }
      const idx = meCalls++ % 6;
      if (idx < 3) {
        return jsonResponse(401, errorEnvelope("auth.unauthorized", "expired"));
      }
      return jsonResponse(200, envelope({ email: "owner@example.com" }));
    });

    // Burst 1: three concurrent /me calls — all 401, share one refresh, retries 200.
    await Promise.all(
      Array.from({ length: 3 }, () => api.get("/api/auth/me")),
    );
    expect(refreshCalls).toBe(1);

    // Burst 2: another three concurrent /me calls. With refcount
    // properly reset to zero between bursts, a NEW refresh fires.
    // A buggy implementation that leaked refresh-state would see this
    // burst skip refresh entirely (reusing the stale "true" promise).
    await Promise.all(
      Array.from({ length: 3 }, () => api.get("/api/auth/me")),
    );
    expect(refreshCalls).toBe(2);
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

  it.each(["get", "delete"] as const)(
    "api.%s (no body) sends credentials: 'include' so cd_session / cd_refresh cookies attach",
    async (verb) => {
      const callable = api[verb] as (path: string) => Promise<unknown>;
      await callable("/api/auth/me");
      expect(inits).toHaveLength(1);
      expect(inits[0].credentials).toBe("include");
    },
  );

  // Body-bearing verbs route through a separate branch in request() that
  // sets Content-Type: application/json. Exercise it explicitly — testing
  // only the no-body path would miss a regression that dropped credentials
  // OR mangled the Content-Type ONLY when a body is present.
  it.each([
    ["post", { name: "Acme Inc" }],
    ["put", { name: "Updated" }],
  ] as const)(
    "api.%s (with JSON body) sends credentials: 'include' AND Content-Type: application/json",
    async (verb, body) => {
      const callable = api[verb] as (path: string, body?: unknown) => Promise<unknown>;
      await callable("/api/vendors", body);
      expect(inits).toHaveLength(1);
      expect(inits[0].credentials).toBe("include");
      const headers = new Headers(inits[0].headers ?? {});
      expect(headers.get("Content-Type")).toBe("application/json");
      // Body serialized to JSON, not posted raw.
      expect(inits[0].body).toBe(JSON.stringify(body));
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

  it("non-JSON error body uses the jargon-free generic fallback, NOT res.statusText (#77)", async () => {
    // Most production 5xx responses from a Cloudflare/proxy/CDN edge
    // arrive as HTML, not the JSON ApiEnvelope. Before #77, api.ts
    // initialized `message = res.statusText`, so err.message became
    // "Bad Gateway" / "Service Unavailable" — HTTP jargon hostile to
    // the SMB target audience, violating #35 AC #3 ("toast copy must
    // be human and jargon-free, no raw codes (no auth.email_taken,
    // no bare status string)").
    //
    // After #77, the fallback is a jargon-free user-facing string; the
    // envelope's `error.message` still wins when the body is valid
    // JSON with a present message. ApiError.status preserves 502 so
    // Sentry / logs can still discriminate by code.
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
    expect((err as ApiError).message).toBe("Something went wrong. Try again.");
    // statusText must NOT leak through under any circumstance.
    expect((err as ApiError).message).not.toMatch(/bad gateway/i);
    expect((err as ApiError).status).toBe(502);
    // code stays as the default "server.error" — symmetric with the
    // network-failure test which pins code === "network.unreachable",
    // so a consumer branching on code can discriminate the two.
    expect((err as ApiError).code).toBe("server.error");
  });

  it("non-OK with a valid JSON envelope whose `error` is null falls back to the generic message (#77)", async () => {
    // The third envelope-parse branch added by #77: body is valid JSON
    // and parses fine, but `error` is null (a malformed-by-edge-layer
    // response or a misbehaving backend). Pre-#77 this would have
    // surfaced res.statusText; post-#77 it falls through to the
    // generic fallback because `body.error?.message` is undefined.
    fetchMock.mockResolvedValueOnce(
      new Response(JSON.stringify({ data: null, error: null }), {
        status: 500,
        statusText: "Internal Server Error",
        headers: { "Content-Type": "application/json" },
      }),
    );

    const err = await api
      .post("/api/auth/login", {})
      .catch((e) => e as ApiError);

    expect(err).toBeInstanceOf(ApiError);
    expect((err as ApiError).message).toBe("Something went wrong. Try again.");
    expect((err as ApiError).code).toBe("server.error");
    expect((err as ApiError).status).toBe(500);
  });

  it("non-OK envelope with an empty / whitespace-only error.message falls back to the generic message (#77)", async () => {
    // Belt-and-braces against a server that returns `{ error: { code,
    // message: "" } }`. `??` alone would let the empty string
    // overwrite the fallback. api.ts uses `.trim()` so only a string
    // with content can override.
    fetchMock.mockResolvedValueOnce(
      new Response(
        JSON.stringify({
          data: null,
          error: { code: "server.error", message: "   " },
        }),
        {
          status: 500,
          headers: { "Content-Type": "application/json" },
        },
      ),
    );

    const err = await api
      .post("/api/auth/login", {})
      .catch((e) => e as ApiError);

    expect(err).toBeInstanceOf(ApiError);
    expect((err as ApiError).message).toBe("Something went wrong. Try again.");
  });

  it("network failure (fetch reject) surfaces as ApiError with the jargon-free fallback, NOT a raw TypeError (#77)", async () => {
    // fetch() throws a TypeError on offline / DNS-fail / CORS-drop /
    // connection-reset. Before #77, the TypeError escaped unhandled,
    // and any consumer that surfaced `err.message` (login/register
    // toasts, the documents/vendors list error card from #80) would
    // render the browser's raw "Failed to fetch" (Chromium) or "Load
    // failed" (Safari) — the same HTTP-jargon class as "Bad Gateway"
    // above. api.ts now wraps fetch() and converts any reject into an
    // ApiError with the generic fallback message and status=0 ("never
    // reached the server"). Downstream `instanceof Error` checks still
    // succeed since ApiError extends Error.
    fetchMock.mockRejectedValueOnce(new TypeError("Failed to fetch"));

    const err = await api
      .post("/api/auth/login", {})
      .catch((e) => e as ApiError);

    expect(err).toBeInstanceOf(ApiError);
    // Documented Error-compatibility contract from api.ts: ApiError
    // extends Error. A future refactor that switched ApiError to a
    // non-Error class (e.g. POJO) would silently break try/catch sites
    // and Sentry's auto error-capturer that rely on Error-shape duck
    // typing. Pin both classes explicitly.
    expect(err).toBeInstanceOf(Error);
    expect((err as ApiError).message).toBe("Something went wrong. Try again.");
    expect((err as ApiError).code).toBe("network.unreachable");
    // status=0 signals "no HTTP response was received" so callers /
    // Sentry can distinguish a network failure from a 5xx.
    expect((err as ApiError).status).toBe(0);
    // Pin the no-retry contract: a network failure must NOT trigger
    // the 401-refresh retry loop. A future refactor that added bare
    // retries on the first fetch would silently regress this without
    // the explicit call-count assertion.
    expect(fetchMock).toHaveBeenCalledTimes(1);
  });

  it("retry-after-401 + retry network-fails: surfaces friendly network.unreachable, NOT the stale 401 (#77 followup)", async () => {
    // Combined path #77's review surfaced as untested: initial 401 →
    // refresh succeeds → retry fetch rejects with TypeError. The
    // documented behavior is that the proximate (network) failure
    // wins — surfacing a stale 401 ("Invalid credentials") after a
    // SUCCESSFUL refresh would mislead the user.
    //
    // Also pins the refcount-drain-on-throw contract: even though the
    // retry threw, the try/finally in request() must still call
    // releaseRefresh so the next 401 can refresh.
    let meCalls = 0;
    fetchMock.mockImplementation(async (input: RequestInfo | URL) => {
      const url = typeof input === "string" ? input : input.toString();
      if (url.endsWith("/api/auth/refresh")) {
        return new Response(null, { status: 204 });
      }
      meCalls++;
      if (meCalls === 1) {
        // Initial fetch: 401, triggers refresh.
        return jsonResponse(401, errorEnvelope("auth.unauthorized", "expired"));
      }
      // Retry fetch (post-refresh): network-fails.
      throw new TypeError("Failed to fetch");
    });

    const err = await api
      .get("/api/auth/me")
      .catch((e) => e as ApiError);

    expect(err).toBeInstanceOf(ApiError);
    // The proximate failure (network, post-refresh) wins, not the
    // original 401 — surfacing the stale 401 would mislead the user.
    expect((err as ApiError).code).toBe("network.unreachable");
    expect((err as ApiError).status).toBe(0);
    expect((err as ApiError).message).toBe("Something went wrong. Try again.");
  });

  // AbortError pass-through — parametrized across BOTH the
  // DOMException variant (default `controller.abort()`) AND the
  // custom-reason variant (`controller.abort(errorWithAbortErrorName)`)
  // per #118. The predicate in fetchOrFriendlyThrow widened from
  // `instanceof DOMException` to `instanceof Error` to cover both
  // — a single it.each block keeps the two cases co-located so a
  // regression that narrowed the check back to DOMException-only
  // fails the second case loudly.
  it.each([
    {
      name: "DOMException (default controller.abort())",
      rejection: new DOMException("aborted", "AbortError"),
    },
    {
      // Modern AbortSignal spec lets callers pass a custom reason
      // via `controller.abort(reason)`; fetch rejects with that
      // reason directly. Convention is an Error with
      // `.name === "AbortError"` so consumers can detect it
      // uniformly. The previous `instanceof DOMException` check
      // mis-converted this into a network-failure toast.
      // (#118 — surfaced by the #77 post-merge review.)
      name: "plain Error with name === 'AbortError' (custom controller.abort(reason))",
      rejection: Object.assign(new Error("user-cancelled"), {
        name: "AbortError",
      }),
    },
  ])(
    "AbortError pass-through: $name is re-thrown unchanged, NOT wrapped as a network failure (#77 / #118)",
    async ({ rejection }) => {
      // A future caller passing an AbortSignal (e.g. TanStack Query's
      // built-in queryFn cancellation on unmount) needs the abort to
      // surface as cancellation, NOT as a real-looking network error
      // toast. api.ts deliberately re-throws the original error
      // instead of wrapping it in ApiError("network.unreachable").
      fetchMock.mockRejectedValueOnce(rejection);

      const err = await api
        .post("/api/auth/login", {})
        .catch((e) => e as Error);

      // Lead with the load-bearing identity check: `expect(...).toBe`
      // uses Object.is, so a regression that cloned the rejection
      // (e.g. `throw new (Object.getPrototypeOf(err).constructor)
      // (err.message, err.name)`) would still pass the supporting
      // isErrorLike / .name assertions but fail the identity check.
      // Putting identity first means the FIRST failure line points
      // at the actual contract violation. The supporting
      // assertions below serve as readable documentation of the
      // expected shape. (#118 review — test-quality reviewer.)
      expect(err).toBe(rejection);
      expect(err).not.toBeInstanceOf(ApiError);

      // Shape documentation: the SAME (Error || DOMException) union
      // shape as the production predicate in fetchOrFriendlyThrow.
      // jsdom's DOMException polyfill doesn't extend Error, so a
      // bare `instanceof Error` here would false-fail on the
      // DOMException case in this test environment even though
      // the production runtime treats DOMException as Error.
      const isErrorLike =
        err instanceof Error || err instanceof DOMException;
      expect(isErrorLike).toBe(true);
      expect((err as Error | DOMException).name).toBe("AbortError");
    },
  );

  // Negative-pair: every rejection-shape that is NOT a recognized
  // AbortError must convert to the friendly ApiError("network.
  // unreachable") so a `.catch((e: ApiError) => e.code)` consumer
  // never crashes on raw strings/POJOs and a non-abort DOMException
  // never silently bubbles up. Parametrized so each branch of the
  // predicate ((Error || DOMException) && name === "AbortError")
  // has its own negative — a regression that drops EITHER half
  // would surface as a specific test failure pointing at the gap.
  // (#118 second-pass review — test-quality reviewer.)
  it.each([
    {
      name: "plain Error with default name 'Error' (no AbortError tag)",
      rejection: new Error("boom"),
    },
    {
      // Catches the asymmetry the reviewer flagged: a regression
      // that special-cased `if (err instanceof DOMException) throw
      // err;` (early-return WITHOUT the name check) would silently
      // pass through every NotAllowedError, TimeoutError, QuotaExceeded
      // etc. into a `.catch((e: ApiError) => ...)` consumer and
      // crash on `.code` access. Pin the DOMException-side name
      // check explicitly.
      name: "DOMException with a non-AbortError name (NotAllowedError)",
      rejection: new DOMException("forbidden", "NotAllowedError"),
    },
    {
      // controller.abort("some string") rejects fetch with the
      // raw string. The (Error || DOMException) gate correctly
      // wraps it because surfacing a string to a typed ApiError
      // consumer would crash on `.code` / `.message` access.
      name: "string reason (controller.abort('user-cancelled'))",
      rejection: "user-cancelled",
    },
    {
      // controller.abort({ name: 'AbortError', ... }) — a POJO
      // that LOOKS like an abort but isn't Error-shaped. The
      // predicate's `instanceof` gate correctly rejects it; a
      // future loosening to `if (err?.name === 'AbortError')`
      // would silently let this through and crash downstream
      // assumptions of `err instanceof Error`.
      name: "POJO with abort-error shape but no Error constructor",
      rejection: { name: "AbortError", message: "cancelled" },
    },
  ])(
    "non-AbortError rejection: $name is wrapped as ApiError('network.unreachable'), NOT passed through (#118 negative)",
    async ({ rejection }) => {
      fetchMock.mockRejectedValueOnce(rejection);

      const err = await api
        .post("/api/auth/login", {})
        .catch((e) => e as Error);

      expect(err).toBeInstanceOf(ApiError);
      expect((err as ApiError).code).toBe("network.unreachable");
      expect((err as ApiError).status).toBe(0);
      expect((err as ApiError).message).toBe(GENERIC_FALLBACK_MESSAGE);
    },
  );
});
