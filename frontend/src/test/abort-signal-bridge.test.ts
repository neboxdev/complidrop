/**
 * Contract tests for the test-harness AbortSignal bridge (#222) and the end-to-end
 * cancellation it enables through the real `api` client.
 *
 * The bridge (src/test/abort-signal-bridge.ts) lets production code thread a TanStack
 * `queryFn({ signal })` into `fetch` under jsdom, where the test-realm AbortSignal is
 * otherwise rejected by the undici Request MSW constructs. These tests pin BOTH that the
 * bridge preserves normal resolution AND that an aborted signal still surfaces as a real
 * cancellation (AbortError) — so the "cancel in-flight request" behavior stays testable.
 */
import { describe, it, expect } from "vitest";
import { http, HttpResponse, delay } from "msw";
import { server, url, jsonOk } from "@/test";
import { api, ApiError } from "@/lib/api";

describe("AbortSignal bridge (#222)", () => {
  it("a request with NO signal resolves normally", async () => {
    server.use(http.get(url("/api/bridge/plain"), () => HttpResponse.json({ ok: true })));
    const res = await fetch("http://localhost:5292/api/bridge/plain", { credentials: "include" });
    expect((await res.json()).ok).toBe(true);
  });

  it("a request with a fresh (non-aborted) signal resolves normally", async () => {
    // This is the case that broke 122 tests before the bridge: jsdom's AbortSignal was
    // rejected by undici's Request. The bridge strips it for the underlying fetch.
    server.use(http.get(url("/api/bridge/signal"), () => HttpResponse.json({ ok: true })));
    const res = await fetch("http://localhost:5292/api/bridge/signal", {
      credentials: "include",
      signal: new AbortController().signal,
    });
    expect((await res.json()).ok).toBe(true);
  });

  it("a pre-aborted signal rejects with an AbortError without hitting the network", async () => {
    let handlerCalled = false;
    server.use(
      http.get(url("/api/bridge/preaborted"), () => {
        handlerCalled = true;
        return jsonOk({ ok: true });
      }),
    );
    const ctrl = new AbortController();
    ctrl.abort();

    await expect(
      fetch("http://localhost:5292/api/bridge/preaborted", { signal: ctrl.signal }),
    ).rejects.toMatchObject({ name: "AbortError" });
    expect(handlerCalled).toBe(false);
  });

  it("aborting mid-flight rejects the in-flight request with an AbortError", async () => {
    server.use(
      http.get(url("/api/bridge/slow"), async () => {
        await delay(200); // still in flight when we abort below
        return HttpResponse.json({ ok: true });
      }),
    );
    const ctrl = new AbortController();
    const p = fetch("http://localhost:5292/api/bridge/slow", { signal: ctrl.signal });
    ctrl.abort();

    await expect(p).rejects.toMatchObject({ name: "AbortError" });
  });

  // AC #3: threading the signal into api.get makes a cancellation cancel the request —
  // surfaced as an AbortError (which api.ts re-throws unchanged, NOT as a network error /
  // toast), exactly what TanStack needs to discard a superseded/unmounted poll.
  it("api.get(path, { signal }) cancels in-flight when the signal aborts", async () => {
    server.use(
      http.get(url("/api/bridge/api-slow"), async () => {
        await delay(200);
        return jsonOk({ value: 1 });
      }),
    );
    const ctrl = new AbortController();
    const p = api.get<{ value: number }>("/api/bridge/api-slow", { signal: ctrl.signal });
    ctrl.abort();

    const err = await p.catch((e) => e);
    expect((err as Error).name).toBe("AbortError");
    // It must NOT be coerced into a friendly network ApiError — cancellation is not failure.
    expect(err).not.toBeInstanceOf(ApiError);
  });

  it("api.get(path, { signal }) resolves normally when the signal never aborts", async () => {
    server.use(http.get(url("/api/bridge/api-ok"), () => jsonOk({ value: 42 })));
    const out = await api.get<{ value: number }>("/api/bridge/api-ok", {
      signal: new AbortController().signal,
    });
    expect(out.value).toBe(42);
  });
});
