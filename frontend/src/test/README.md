# Frontend test harness

Reusable test scaffolding for the Next.js frontend. Pairs with [ADR 0003](../../../docs/adr/0003-frontend-testing-with-vitest.md) (Vitest + RTL + jsdom decision).

## What's in the box

| Module                 | Purpose                                                                                |
| ---------------------- | -------------------------------------------------------------------------------------- |
| `render.tsx`           | `renderWithProviders(ui, opts)` — QueryClient + auth-cache seeding + router/params.    |
| `server.ts`            | One MSW `setupServer` for the whole vitest run. Started in `vitest.setup.ts`.          |
| `handlers.ts`          | Default handlers (anonymous 401 baseline). Override per-test with `server.use(...)`.   |
| `helpers.ts`           | `TEST_API_BASE`, `url("/api/...")`, `jsonOk(data)`, `jsonError(code, msg, { status })`. |
| `fixtures.ts`          | Named typed fixtures — `authedMe`, `documentsAllStatuses{Response}`, `portalInfo`, `expiredPortalLinkHandler`, `expiredLink404`. |
| `navigation.ts`        | Mutable container behind the global `vi.mock("next/navigation", ...)`.                 |
| `sonner.ts`            | Mutable `toast.*` spies behind the global `vi.mock("sonner", ...)`. Import `toastSuccess`/`toastError` from `@/test` and assert on them.   |
| `polling.ts`           | `sequencedJsonOk(...payloads)` and `sequencedResponses(...factories)` return MSW handlers that yield in order then repeat the last (terminal state). Use `sequencedJsonOk` for all-success sequences; reach for `sequencedResponses` when the sequence mixes `jsonOk` / `jsonError`. |
| `dropzone.ts`          | `dropFilesIn(container, files)` + `makeFile(name, type?, size?)` — container-scoped helpers for driving `react-dropzone` in tests. |
| `security.ts`          | `assertNotInDom(value, root?)` — assert a sensitive value is NOT rendered into the DOM (scans both `textContent` and `innerHTML`). |
| `forms.ts`             | `fillByLabel(label, value, container?)` + `submitFormIn(container)` — label-based input driver + container-scoped submit helper (multi-form guard). Pass the third `container` arg when two forms in the same document share a label (see `form-helpers.integration.test.tsx`, #134). Standard auth-form test idiom after #132 / #135. |
| `example.test.tsx`     | Template test — **copy this as the starting point for new suites.**                    |

`src/test/index.ts` re-exports the common surface, so most test files only need:

```ts
import {
  renderWithProviders,
  server,
  url,
  jsonOk,
  jsonError,
  authedMe,
  documentsAllStatusesResponse,
  portalInfo,
  expiredPortalLinkHandler,
} from "@/test";
import { http } from "msw";
```

Each leaf module is also importable directly (`import { url } from "@/test/helpers"`) so a non-rendering assertion doesn't pull in React Testing Library or MSW.

## Why MSW (and not `vi.stubGlobal("fetch", …)`)

We use **MSW** for component/hook tests that exercise the data layer end-to-end. Two reasons:

1. **Reads like the real network.** Handlers register against full URLs (`http://localhost:5292/api/auth/me`), the same URLs `lib/api.ts` produces. No first-arg-of-fetch indirection in the test reader's head.
2. **One central source of default behavior.** `handlers.ts` defines "anonymous 401" once; each test only declares what it overrides.

Tests that pin the api client's _own_ fetch contract (refresh-on-401 sequencing) — `lib/api.test.ts`, `hooks/useAuth.test.tsx` — keep using `vi.stubGlobal("fetch", …)`. That's deliberate: those tests need to count calls and reject the global symbol entirely, which short-circuits MSW. Both approaches coexist.

> **Portal-page caveat.** `frontend/src/app/portal/[token]/page.tsx` calls `fetch()` directly (it can't use the cookie-bearing api client) and parses the envelope inline. MSW handlers still match by URL, but `ApiError` is never thrown for portal responses — assert on the page's own error-string state, not on an `ApiError` instance.

## How `renderWithProviders` reads

```ts
import { vi } from "vitest";
import { QueryClient } from "@tanstack/react-query";
import { renderWithProviders, authedMe } from "@/test";

renderWithProviders(<DocumentsPage />, {
  // Skip the auth round-trip by priming the cache:
  auth: authedMe,
  // Drive useParams() / useSearchParams() / useRouter():
  params: { id: "d_completed_01" },
  searchParams: { plan: "annual" },
  router: { push: vi.fn() },
  // Optional: stable client across multiple renders in one test.
  queryClient: new QueryClient(),
});
```

Returns the RTL `RenderResult` plus the `QueryClient`, so a test can assert e.g. `expect(qc.getQueryData(["documents", "list"])).toBeDefined()` after a mutation. See `example.test.tsx`'s `params + router.push spy + returned queryClient introspection` test for the full pattern.

### Anonymous vs. authed seeding

- `auth` omitted → useMe() hits MSW (default returns 401 → null).
- `auth: null` → cache seeded with null. No fetch, immediate "logged-out" branch.
- `auth: authedMe` → cache seeded with the Me. No fetch, immediate "authed" branch.

Use the seeds for component tests that aren't about the auth fetch. Drop the seed when the auth fetch _is_ the subject (use `renderHook` + MSW directly in that case).

### Why `gcTime: Infinity` on the test QueryClient

The harness's test client sets `gcTime: Infinity`. Per-render isolation comes from spawning a **fresh** QueryClient per `renderWithProviders` call — not from per-query GC. The `Infinity` GC ensures cache entries written by a mutation's `onSuccess` (or by the `auth` seed itself) survive long enough for the test to read them, even when no rendered component subscribes to the key.

Concrete case: `LoginPage` mounts `useLogin` (a mutation) but NOT `useMe` (a query). With `gcTime: 0`, TanStack Query reaps a query entry the moment its observer count drops to zero — including entries written via `setQueryData` from a mutation `onSuccess` when nothing is subscribed. The cache assertion `expect(qc.getQueryData(ME_KEY)).toMatchObject(...)` would race the GC sweep and return `undefined`. With `gcTime: Infinity`, the entry persists for the lifetime of the (per-test) QueryClient, so the assertion is deterministic. Cross-test leakage is impossible because the entire client is unreachable as soon as the test scope ends.

When asserting on cache state, use the **exported** `ME_KEY` / `ME_PROBE_KEY` constants from `@/hooks/useAuth`, not the literal `["auth", "me"]` — a rename in `useAuth.ts` then fails the assertion loudly instead of silently matching `undefined` against the stale literal.

> **Pinning the no-fetch contract.** When the test's whole point is "no fetch fires," install an MSW handler that THROWS for the URL you don't expect to hit (see the anonymous case in `example.test.tsx`). The default 401 handler would silently satisfy a regression where seeding stopped working — pinning the contract with a throwing override turns a silent false-green into a loud failure.

## Polling tests

When a hook polls via TanStack Query's `refetchInterval`, tests sequence MSW responses to drive the state machine. `sequencedJsonOk` lifts the mechanical bit:

```ts
import { sequencedJsonOk } from "@/test";

let calls = 0;
const seq = sequencedJsonOk(
  makeDocumentDetail({ extractionStatus: "Pending" }),
  makeDocumentDetail({ extractionStatus: "Processing" }),
  makeDocumentDetail({ extractionStatus: "Completed" }),
);
server.use(
  http.get(url("/api/documents/:id"), () => {
    calls++;
    return seq();
  }),
);
// ... drive timers ...
expect(calls).toBeGreaterThanOrEqual(3);
```

The handler clamps to the LAST response after the list is exhausted — matches the "terminal state stays terminal" contract of `refetchInterval` predicates that return `false` once the response reaches a terminal status.

### Mixed jsonOk/jsonError sequences — `sequencedResponses`

`sequencedJsonOk` wraps every element in `jsonOk` — sequences are success-only. For mixed-code sequences (e.g. first call 500, second call 200), reach for `sequencedResponses`, which takes Response **factories** (composable with the existing `jsonOk` / `jsonError` helpers via arrow-function wrappers) instead of raw payloads:

```ts
import { sequencedResponses, jsonOk, jsonError } from "@/test";

let calls = 0;
const seq = sequencedResponses(
  () => jsonError("server.error", "DB blip.", { status: 500 }),
  () => jsonOk(makeDocumentsResponse({ items: [], total: 0 })),
);
server.use(
  http.get(url("/api/documents"), () => {
    calls++;
    return seq();
  }),
);
```

Same terminal-clamp + RangeError + per-invocation-counter semantics as `sequencedJsonOk`. Factories — not pre-built Responses — are required because Response body streams are single-use; a refetch-interval test that does one extra advance on the terminal step needs each call to produce a fresh Response. See the migrated `retry-on-5xx` test in `documents/page.test.tsx` for the canonical migration shape (#124).

**Gotchas** (the helper does NOT solve these for you):

- `vi.useFakeTimers({ shouldAdvanceTime: true })` is REQUIRED for RTL's `waitFor` to work — without it the `waitFor` polling loop blocks on the fake-timer queue.
- Fake timers must be activated BEFORE the component mounts so the `refetchInterval` is scheduled on the fake queue. Activate them in a `beforeEach`, not inside the test body.
- For call-count assertions, keep a `let calls = 0; calls++` outside the handler — `sequencedJsonOk` doesn't expose its internal counter on purpose (keeps the signature simple).

## Toasts

`vitest.setup.ts` mocks `sonner` once against mutable spies in `sonner.ts`. Tests import the spies from `@/test`:

```ts
import { toastSuccess, toastError } from "@/test";

it("on save → success toast", async () => {
  // ... drive the component ...
  await waitFor(() => expect(toastSuccess).toHaveBeenCalledWith("Saved"));
  expect(toastError).not.toHaveBeenCalled();
});
```

The setup file's `afterEach` calls `resetSonner()` so a `toastSuccess` from one test never leaks into the next. `toast.info`, `toast.warning`, `toast.loading`, `toast.dismiss`, `toast.message`, and `toast.promise` are all spied too — import the matching `toastInfo` / `toastWarning` / … as needed.

**Negative-assertion convention for smoke tests:** when a test renders a component whose subject does NOT fire a toast (smoke renders, loading-state tests, populated-state tests where no mutation runs), explicitly assert `expect(toastSuccess).not.toHaveBeenCalled()` / `expect(toastError).not.toHaveBeenCalled()` at the end of the test. The harness's `resetSonner()` between tests is not a substitute for the assertion — a regression that auto-fires a toast on mount would only surface as a noisy DOM in a later test if the assertion is missing. See `vendors/[id]/page.test.tsx` for the canonical pattern.

Per-file `vi.mock("sonner", …)` still works as an escape hatch (Vitest's per-file mock registry overrides the setup-file mock within the file's own scope) — reach for it only when a test needs a custom shape (e.g. a spy that throws, a real `toast.promise` implementation).

## Routing mocks — when to use which

Two-tier setup, documented here once so it doesn't get re-litigated.

- **Default (setup-file mock + harness options).** `vitest.setup.ts` mocks `next/navigation` once against the mutable `navState`. Most tests use this — they drive routing through `renderWithProviders({ router, params, searchParams, pathname })` and assert on the returned spies (or `navState.router.push.mock.calls`). `notFound()` and `redirect()` throw a `NEXT_NOT_FOUND` / `NEXT_REDIRECT` sentinel so component code after them cannot silently keep running.

  **`push` / `replace` actually navigate (#370).** They remain assertable spies, but they also apply the href to `navState` — so `useSearchParams()` / `usePathname()` return the NEW value afterwards and subscribed components re-render (both hooks read through `useSyncExternalStore`). Two properties this buys, both load-bearing:

  - The commit is **deferred by a macrotask**, mirroring the real App Router's transition. A component that calls `replace` in an event handler re-renders at least once with the OLD query string still readable. Do not "fix" this into a synchronous apply: #370's scenario A lived exactly in that window, and a synchronous mock lets the bug pass its own regression test (verified, not assumed).
  - Injecting your own spy (`renderWithProviders({ router: { replace } })`) **overrides** the live behavior for that field, because `setNavigationState` merges the router field-by-field. Use that when you want to assert a navigation was requested without the URL actually moving.

  To simulate a URL change the page did NOT initiate — a same-route sidebar click, Back, an external deep link — call `setNavigationState({ searchParams, pathname })` mid-test; it notifies subscribers, so mounted components re-render against the new URL.

  **`window.history.pushState` / `replaceState` are bridged too, and they SPLIT (#370).** Next's App Router integrates the native History API: those calls update the URL and sync `usePathname`/`useSearchParams` with no route navigation and no RSC fetch (the documented path for list filter/sort state — the documents page uses it). But the two halves do not land together:

  - `window.location` updates **synchronously** (the native call).
  - `useSearchParams()` / `usePathname()` update **a commit later** — Next routes its own sync through `startTransition` (`app-router.js`, `applyUrlFromHistoryPushReplace`). Skipping the RSC fetch makes this window narrower than `router.replace`'s, not absent.

  This was previously documented — and mocked — as fully synchronous, with a "don't collapse it" note attached. That was wrong, and it cost a whole review pass: a page composed its filter writes on the transition-deferred hook, and the synchronous mock meant its own regression tests passed anyway. **When you need "has the URL changed", assert `window.location.search`; use `navState.searchParams` only when the router snapshot is itself the subject, and `await` it.**

  `setNavigationCommitDelay(ms)` staggers commit latency within a test, for BOTH mechanisms. Real `router` commits wait on their own RSC fetch, so two dispatched together land at different times — with a single shared deadline that interleaving is untestable. A History-API commit waits on a transition instead, which React can schedule late or interrupt; same knob, since what a test cares about is how long the component keeps reading the old value. Reset to 0 between tests.
- **Per-file `vi.mock("next/navigation", ...)`** (escape hatch). Required when the test needs a hoisted spy on `useSearchParams` or wants to capture the call site at module load (see `register-form.test.tsx` for the canonical example). Vitest's per-file mock registry overrides the setup-file mock within the file's own module scope — file-level mocks always win.

Pick the default unless you have a specific reason to escape it.

## Dropzone tests

`react-dropzone` v15+ evaluates `accept` + `maxSize` INSIDE its `onDrop` callback (not at the browser-level input filter), which jsdom can't enforce. Drive uploads through the hidden file input:

```ts
import { dropFilesIn, makeFile } from "@/test";

const { container } = renderWithProviders(<PortalPage />, { params: { token } });
await waitFor(() => expect(screen.getByText(/upload/i)).toBeInTheDocument());

dropFilesIn(container, [makeFile("coi.pdf"), makeFile("license.pdf")]);
```

The container is mandatory: a `document.querySelector('input[type="file"]')` lookup would silently collide if a future composite test rendered two dropzone-bearing trees at once. `makeFile` defaults to a 1 KiB `application/pdf`; pass `type` and `sizeBytes` to drive the size-limit / wrong-MIME error paths.

## Writing a new test — the mechanical recipe

1. Copy [`example.test.tsx`](./example.test.tsx) next to the unit under test.
2. Change the component import + the assertion.
3. Add per-test `server.use(http.get(url("/api/your-route"), () => jsonOk(yourFixture)))` for any endpoint your subject calls.
4. Run `npm test`.

## Adding a new fixture

Add to `fixtures.ts` only if it's reused across files. Per-file one-shots stay inline.

Fixtures should:

- Be typed against the real DTO (`Me`, `DocumentListItem`, …) — a TS rename catches stale shapes.
- Be typed as `Readonly<…>` / `ReadonlyArray<…>` so a misbehaving test that mutates the shared object is a compile error, not a silent leak into the next test.
- Use absolute ISO-8601 UTC dates with the explicit `Z` suffix — matches the backend `DateTime` (Kind=Utc) serialization, parses identically across host timezones.
- Mirror only values the backend actually emits. `complianceStatus` is the backend enum — `Pending | Compliant | NonCompliant | ExpiringSoon | Expired`, never `"Unknown"`.
- Expose a `makeXxx(overrides)` factory whenever a test might want to vary fields. Tests use the factory; the shared object stays read-only.

## Adding a new default handler

Only if every test would otherwise have to redeclare it. The bar is high: a default that's wrong is harder to spot than a missing one (the latter fails loudly via `onUnhandledRequest: "error"`).

## Lifecycle (what `vitest.setup.ts` does)

- Pins `NEXT_PUBLIC_API_URL` before any module reads it.
- Mocks `next/navigation` once, sourced from the mutable `navState`; `useSearchParams` / `usePathname` subscribe via `useSyncExternalStore` so a `push`/`replace` re-renders them.
- `server.listen({ onUnhandledRequest: "error" })` so missed handlers fail loudly.
- After every test: RTL cleanup, `server.resetHandlers()`, `resetNavigation()` (rebuilds every spy in `navState`, including `notFound` / `redirect`).
- After the suite: `server.close()`.

## Security assertions

For tests verifying that a session / portal / credential value is NOT rendered into the DOM, use `assertNotInDom`:

```ts
import { assertNotInDom } from "@/test";

const sensitiveToken = "very-secret-vendor-token-XYZ";
renderWithProviders(<PortalPage />, { params: { token: sensitiveToken } });
await waitFor(() => expect(screen.getByRole("heading", { name: /hi /i })).toBeInTheDocument());

assertNotInDom(sensitiveToken);
```

Scans BOTH `root.textContent` (visible copy) AND `root.innerHTML` (attribute values, hidden nodes). Skipping either scan leaves a hole — a leak via `aria-label`, `data-*`, `title`, or a hidden `<input>` value would slip past a `textContent`-only check. Defaults to `document.body`; pass an explicit `root` to narrow further. `<head>` injection paths are out of scope for component tests — import `expectTokenNotInHead` from `frontend/e2e/support/security.ts` (#127) into the relevant Playwright smoke spec for head coverage.

## When NOT to use this harness

- **Fetch-level contract tests** (call counts, header order, refresh sequencing): use `vi.stubGlobal("fetch", …)` directly — see `src/lib/api.test.ts`.
- **Pure unit tests** (a util in `src/lib/`, a tiny component with no providers): plain `render()` from `@testing-library/react` is fine — see `src/components/Logo.test.tsx`.
- **E2E / multi-page flows**: those live in the Playwright suite (ticket #38).
