# 0019. The frontend test harness bridges `AbortSignal` across the jsdom ↔ undici realm boundary

- **Status:** accepted
- **Date:** 2026-06-05
- **Deciders:** Ruben G.

## Context

[#222](https://github.com/neboxdev/complidrop/issues/222) threads TanStack Query's
`queryFn({ signal })` into `api.get(path, { signal })` across every GET query so that an
unmount / navigation / superseding refetch cancels the in-flight request (notably the 3s
document-detail and 5s list polls). `frontend/src/lib/api.ts` was already signal-ready
(`RequestInitEx extends RequestInit`, and `fetchOrFriendlyThrow` deliberately re-throws
`AbortError` unchanged), so the production change is a one-token edit per query.

The blocker — and the reason #222 existed as its own ticket — is the **test harness**.
Threading the signal naively made **122 component/hook tests fail**. Root cause, established
empirically:

- The harness runs under vitest's **jsdom** environment. jsdom omits `fetch`/`Request`/
  `Response`, so the test realm's `globalThis.fetch` and `globalThis.Request` are **Node/
  undici's**. But jsdom **does** install its own `globalThis.AbortController`, so
  `new AbortController()` — exactly what TanStack Query calls internally to make the
  `queryFn` signal — produces a **jsdom** `AbortSignal`.
- undici's `Request` constructor (invoked by MSW's request interceptor) validates
  `init.signal` and rejects anything that isn't one of *its* `AbortSignal`s:
  `TypeError: Expected signal ("AbortSignal {}") to be an instance of AbortSignal`.

So every signal-passing query throws at the fetch boundary. Worse, the boundary is total:
under this harness **no** test-realm signal is accepted by that `Request` — not a
`new AbortController().signal`, not even `AbortSignal.timeout(...)`. And Node's native
`AbortController` is **unrecoverable** once jsdom has shadowed the global: it isn't exported
by any `node:` module, `delete globalThis.AbortController` doesn't resurface it, and
`AbortSignal.any([jsdomSignal])` rejects the foreign input too.

## Decision

Add a **test-only fetch bridge** (`frontend/src/test/abort-signal-bridge.ts`), wired into
`vitest.setup.ts`'s `beforeAll` **after** `server.listen()` so it wraps MSW's interceptor as
the outermost layer. The bridge wraps `globalThis.fetch` to:

1. **Strip** the realm-incompatible `signal` from the `init` handed to the underlying (MSW/
   undici) fetch — so the `Request` constructor never sees it and never throws.
2. **Re-implement the signal's observable behavior** at the wrapper layer: if the signal is
   already aborted, reject immediately; otherwise reject the instant it aborts, with a real
   `AbortError` (`DOMException("…","AbortError")`, the shape `api.ts` re-throws unchanged).
   The underlying fetch still settles in the background (MSW responds instantly), but the
   caller already saw the cancellation — exactly the contract the real `signal` provides.

Production `api.ts` is **unchanged**; in a real browser the realm-correct signal flows
straight to `fetch`. This is purely a harness adaptation.

## Consequences

### Positive

- Threading the signal into all 13 GET queryFns no longer breaks the suite; queries resolve
  normally, and **cancellation stays testable** — an aborted signal rejects the fetch with an
  `AbortError` (proven by `abort-signal-bridge.test.ts` and the `useDocuments` /
  document-detail cancellation tests that spy the threaded signal and assert it aborts).
- No new runtime dependency, no environment swap, no production code change.

### Negative / Neutral

- **MSW handlers can no longer observe `request.signal`** (the bridge strips it before MSW
  sees it). No current handler reads it; a future test that needs to assert on the request
  signal at the handler level must account for this. This is the accepted limitation and the
  reason it is recorded here rather than only in a helper comment.
- The bridge re-implements abort semantics at the `fetch` wrapper rather than at the
  `Request` layer, so test reality diverges slightly from the browser. The divergence is
  confined to "where the abort is observed," not "whether it is observed," and the
  end-to-end `api.get` cancellation test pins that the client surfaces `AbortError`
  (not a network error / toast) on abort.
- Install-once for the whole run, with no `reset`/`uninstall` companion (unlike
  `resetNavigation` / `resetSonner`, which reset per-test state): it wraps the persistent MSW
  interceptor, so a per-test teardown would be wrong.

## Alternatives considered

### Option A — switch the test environment to `happy-dom`

happy-dom uses Node's `fetch`/`Request`/`AbortController` consistently, so the realm mismatch
disappears. Rejected: swapping the environment for the entire frontend suite is a large,
high-blast-radius change (subtle DOM-API differences across ~65 test files) to fix one
boundary — disproportionate, and it trades a known, localized shim for an unknown set of
environment differences.

### Option B — recover / inject Node's native `AbortController`

Make `globalThis.AbortController` produce undici-compatible signals. Rejected because it is
not achievable: Node's `AbortController` is not exported by any module, is gone from
`globalThis` once jsdom shadows it, and cannot be reconstructed from the recoverable
`AbortSignal` class (its constructor is illegal and `AbortSignal.any` rejects foreign inputs).

### Option C — configure MSW / undici to accept the foreign signal, or upgrade msw

Rejected: there is no MSW/undici knob to loosen the `Request` signal validation, and
upgrading msw to chase a possible fix is higher-risk than a self-contained harness shim that
we fully control and test.

## References

- Ticket: [#222](https://github.com/neboxdev/complidrop/issues/222)
- Builds on [ADR 0003](0003-frontend-testing-with-vitest.md) (MSW as the URL-interception
  layer; `vi.stubGlobal` coexistence for fetch-contract tests)
- Code: `frontend/src/test/abort-signal-bridge.ts` (+ `.test.ts`), `frontend/vitest.setup.ts`,
  `frontend/src/lib/api.ts` (`fetchOrFriendlyThrow` AbortError pass-through)
