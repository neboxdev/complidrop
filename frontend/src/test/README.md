# Frontend test harness

Reusable test scaffolding for the Next.js frontend. Pairs with [ADR 0003](../../../docs/adr/0003-frontend-testing-with-vitest.md) (Vitest + RTL + jsdom decision).

## What's in the box

| Module                 | Purpose                                                                                |
| ---------------------- | -------------------------------------------------------------------------------------- |
| `render.tsx`           | `renderWithProviders(ui, opts)` — QueryClient + auth-cache seeding + router/params.    |
| `server.ts`            | One MSW `setupServer` for the whole vitest run. Started in `vitest.setup.ts`.          |
| `handlers.ts`          | Default handlers (anonymous 401 baseline). Override per-test with `server.use(...)`.   |
| `helpers.ts`           | `TEST_API_BASE`, `url("/api/...")`, `jsonOk(data)`, `jsonError(code, msg, { status })`. |
| `fixtures.ts`          | Named typed fixtures — `authedMe`, `documentsAllStatuses{Response}`, `portalInfo`, `expiredLink404`. |
| `navigation.ts`        | Mutable container behind the global `vi.mock("next/navigation", ...)`.                 |
| `example.test.tsx`     | Template test — **copy this as the starting point for new suites.**                    |

`src/test/index.ts` re-exports the common surface, so most test files only need:

```ts
import { renderWithProviders, server, url, jsonOk, authedMe } from "@/test";
import { http } from "msw";
```

## Why MSW (and not `vi.stubGlobal("fetch", …)`)

We use **MSW** for component/hook tests that exercise the data layer end-to-end. Two reasons:

1. **Reads like the real network.** Handlers register against full URLs (`http://localhost:5292/api/auth/me`), the same URLs `lib/api.ts` produces. No first-arg-of-fetch indirection in the test reader's head.
2. **One central source of default behavior.** `handlers.ts` defines "anonymous 401" once; each test only declares what it overrides.

Tests that pin the api client's _own_ fetch contract (refresh-on-401 sequencing) — `lib/api.test.ts`, `hooks/useAuth.test.tsx` — keep using `vi.stubGlobal("fetch", …)`. That's deliberate: those tests need to count calls and reject the global symbol entirely, which short-circuits MSW. Both approaches coexist.

## How `renderWithProviders` reads

```ts
renderWithProviders(<DocumentsPage />, {
  // Skip the auth round-trip by priming the cache:
  auth: authedMe,
  // Drive useParams() / useSearchParams() / useRouter():
  params: { id: "d_completed_01" },
  searchParams: { plan: "annual" },
  router: { push: vi.fn() },
  // Optional: stable client across multiple renders in one test.
  queryClient,
});
```

Returns the RTL `RenderResult` plus the `QueryClient`, so a test can assert e.g. `expect(qc.getQueryData(["documents", "list"])).toBeDefined()` after a mutation.

### Anonymous vs. authed seeding

- `auth` omitted → useMe() hits MSW (default returns 401 → null).
- `auth: null` → cache seeded with null. No fetch, immediate "logged-out" branch.
- `auth: authedMe` → cache seeded with the Me. No fetch, immediate "authed" branch.

Use the seeds for component tests that aren't about the auth fetch. Drop the seed when the auth fetch _is_ the subject (use `renderHook` + MSW directly in that case).

## Writing a new test — the mechanical recipe

1. Copy [`example.test.tsx`](./example.test.tsx) next to the unit under test.
2. Change the component import + the assertion.
3. Add per-test `server.use(http.get(url("/api/your-route"), () => jsonOk(yourFixture)))` for any endpoint your subject calls.
4. Run `npm test`.

## Adding a new fixture

Add to `fixtures.ts` only if it's reused across files. Per-file one-shots stay inline.

Fixtures should:

- Be typed against the real DTO (`Me`, `DocumentListItem`, …) — a TS rename catches stale shapes.
- Use absolute ISO-8601 dates, not relative offsets — tests stay deterministic across days.
- Expose a `makeXxx(overrides)` factory if more than one variant is needed.

## Adding a new default handler

Only if every test would otherwise have to redeclare it. The bar is high: a default that's wrong is harder to spot than a missing one (the latter fails loudly via `onUnhandledRequest: "error"`).

## Lifecycle (what `vitest.setup.ts` does)

- Pins `NEXT_PUBLIC_API_URL` before any module reads it.
- Mocks `next/navigation` once, sourced from the mutable `navState`.
- `server.listen({ onUnhandledRequest: "error" })` so missed handlers fail loudly.
- After every test: RTL cleanup, `server.resetHandlers()`, `resetNavigation()`.
- After the suite: `server.close()`.

## When NOT to use this harness

- **Fetch-level contract tests** (call counts, header order, refresh sequencing): use `vi.stubGlobal("fetch", …)` directly — see `src/lib/api.test.ts`.
- **Pure unit tests** (a util in `src/lib/`, a tiny component with no providers): plain `render()` from `@testing-library/react` is fine — see `src/components/Logo.test.tsx`.
- **E2E / multi-page flows**: those live in the Playwright suite (ticket #38).
