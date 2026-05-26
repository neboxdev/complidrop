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

> **Pinning the no-fetch contract.** When the test's whole point is "no fetch fires," install an MSW handler that THROWS for the URL you don't expect to hit (see the anonymous case in `example.test.tsx`). The default 401 handler would silently satisfy a regression where seeding stopped working — pinning the contract with a throwing override turns a silent false-green into a loud failure.

## Routing mocks — when to use which

Two-tier setup, documented here once so it doesn't get re-litigated.

- **Default (setup-file mock + harness options).** `vitest.setup.ts` mocks `next/navigation` once against the mutable `navState`. Most tests use this — they drive routing through `renderWithProviders({ router, params, searchParams, pathname })` and assert on the returned spies (or `navState.router.push.mock.calls`). `notFound()` and `redirect()` throw a `NEXT_NOT_FOUND` / `NEXT_REDIRECT` sentinel so component code after them cannot silently keep running.
- **Per-file `vi.mock("next/navigation", ...)`** (escape hatch). Required when the test needs a hoisted spy on `useSearchParams` or wants to capture the call site at module load (see `register-form.test.tsx` for the canonical example). Vitest's per-file mock registry overrides the setup-file mock within the file's own module scope — file-level mocks always win.

Pick the default unless you have a specific reason to escape it.

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
- Mocks `next/navigation` once, sourced from the mutable `navState`.
- `server.listen({ onUnhandledRequest: "error" })` so missed handlers fail loudly.
- After every test: RTL cleanup, `server.resetHandlers()`, `resetNavigation()` (rebuilds every spy in `navState`, including `notFound` / `redirect`).
- After the suite: `server.close()`.

## When NOT to use this harness

- **Fetch-level contract tests** (call counts, header order, refresh sequencing): use `vi.stubGlobal("fetch", …)` directly — see `src/lib/api.test.ts`.
- **Pure unit tests** (a util in `src/lib/`, a tiny component with no providers): plain `render()` from `@testing-library/react` is fine — see `src/components/Logo.test.tsx`.
- **E2E / multi-page flows**: those live in the Playwright suite (ticket #38).
