/**
 * Test-harness bridge for `AbortSignal` across the jsdom ↔ undici realm boundary.
 *
 * ## The problem (#222)
 *
 * Production code threads TanStack Query's `queryFn({ signal })` into `api.get(path,
 * { signal })` → `fetch(url, { signal })` so unmount / navigation cancels in-flight
 * requests. In a real browser this is fine. In the vitest **jsdom** environment it is not:
 *
 *   - jsdom omits `fetch`/`Request`/`Response`, so the test realm's `globalThis.fetch`
 *     and `globalThis.Request` are **Node/undici's**.
 *   - jsdom DOES install its own `globalThis.AbortController`, so `new AbortController()`
 *     (which is exactly what TanStack Query calls internally) produces a **jsdom**
 *     `AbortSignal`.
 *   - undici's `Request` constructor (invoked by MSW's request interceptor) validates
 *     `init.signal` and rejects anything that isn't one of *its* `AbortSignal`s with
 *     `TypeError: Expected signal ("AbortSignal {}") to be an instance of AbortSignal`.
 *
 * Empirically (verified while writing this), under this harness NO test-realm signal is
 * accepted by that `Request` — not a `new AbortController().signal`, not even
 * `AbortSignal.timeout(...)`. And Node's native `AbortController` is unrecoverable once
 * jsdom has shadowed the global (it isn't exported by any `node:` module, and `delete`
 * doesn't resurface it). So threading the signal naively makes every signal-passing query
 * throw at the fetch boundary — the documented "122 component/hook tests fail" blocker.
 *
 * ## The fix
 *
 * Wrap `globalThis.fetch` (after MSW installs its interceptor, so this wrapper is the
 * outermost layer) to:
 *   1. Strip the realm-incompatible `signal` from the `init` handed to the underlying
 *      (MSW/undici) fetch — so the `Request` constructor never sees it and never throws.
 *   2. Re-implement the signal's *observable behavior* at this layer: if the signal is
 *      already aborted, reject immediately; otherwise reject the moment it aborts, with a
 *      real `AbortError` (the same shape `api.ts`'s `fetchOrFriendlyThrow` re-throws
 *      unchanged). The underlying fetch still settles in the background (MSW responds
 *      instantly), but the caller already saw the cancellation — exactly the contract the
 *      real `signal` provides.
 *
 * This is a TEST-ONLY adaptation: production `api.ts` is unchanged and passes the real,
 * realm-correct signal in browsers. It keeps cancellation testable (an aborted signal
 * still rejects the fetch with an `AbortError`) without papering over behavior.
 */

let installed = false;

function abortError(reason: unknown): Error {
  // Prefer the signal's own reason when it's already an AbortError-shaped Error (the
  // convention for cancellation), so a caller inspecting `.name`/`.message` sees it
  // verbatim. Otherwise synthesize the standard DOMException("…","AbortError"), which is
  // what a real aborted fetch rejects with and what api.ts detects.
  if (reason instanceof Error && reason.name === "AbortError") return reason;
  return new DOMException("The operation was aborted.", "AbortError");
}

/**
 * Wrap the current `globalThis.fetch` so a passed `AbortSignal` is honored at the harness
 * layer instead of being handed to the realm-incompatible undici `Request`. Call AFTER
 * `server.listen()` so this wraps MSW's interceptor (the outermost layer). Idempotent.
 */
export function installAbortSignalBridge(): void {
  if (installed) return;
  installed = true;

  const innerFetch = globalThis.fetch;
  globalThis.fetch = function bridgedFetch(
    input: RequestInfo | URL,
    init?: RequestInit,
  ): Promise<Response> {
    const signal = init?.signal;
    if (!signal) return innerFetch(input, init);

    // Hand the underlying fetch everything EXCEPT the cross-realm signal.
    const rest: RequestInit = { ...init };
    delete rest.signal;

    if (signal.aborted) return Promise.reject(abortError(signal.reason));

    return new Promise<Response>((resolve, reject) => {
      const onAbort = () => reject(abortError(signal.reason));
      signal.addEventListener("abort", onAbort, { once: true });
      innerFetch(input, rest).then(
        (res) => {
          signal.removeEventListener("abort", onAbort);
          resolve(res);
        },
        (err) => {
          signal.removeEventListener("abort", onAbort);
          reject(err);
        },
      );
    });
  };
}
