# 0003. Frontend testing with Vitest + React Testing Library

- **Status:** accepted
- **Date:** 2026-05-22
- **Deciders:** Ruben G.

## Context

The frontend (`frontend/`, Next.js 16 + React 19 + Tailwind 4 + shadcn/ui) had no
test runner at all — `package.json` exposed only `dev`/`build`/`start`/`lint`. The
backend has had xUnit + Testcontainers since the hardening epic, but the frontend's
most user-visible surfaces (the landing page, auth forms, the dashboard) were
unguarded. CLAUDE.md already anticipates this: "Frontend tests use Jest/Vitest."

Ticket #22 (wire real login/sign-up CTAs into the landing page) needed a test that
asserts the homepage links to `/login` and `/register`, which forced the choice of a
runner now rather than later. This ADR records that choice and the conventions so the
next frontend test doesn't re-litigate them.

## Decision

Use **Vitest + React Testing Library + jsdom** for frontend component/unit tests.

Conventions established by the first suite (`frontend/src/app/page.test.tsx`):

- Config at `frontend/vitest.config.mts`: `jsdom` environment, the `@/` path alias
  mirrored from `tsconfig.json`, `setupFiles: ["./vitest.setup.ts"]` (which imports
  `@testing-library/jest-dom/vitest`), and `include: ["src/**/*.{test,spec}.{ts,tsx}"]`.
- Tests are colocated next to the unit under test (`page.test.tsx` beside `page.tsx`).
- `npm test` → `vitest run`; wired into `.github/workflows/frontend-ci.yml` as a
  dedicated step so regressions fail CI, not just a local run.
- Component tests **mock the boundaries** rather than booting the framework: `next/link`
  is mocked as a plain `<a>` (no router context needed to assert hrefs), and data hooks
  like `useMe()` are mocked (via `vi.hoisted`) to drive component state. A real
  `QueryClientProvider` + mocked `fetch` is reserved for tests that must exercise a
  hook's own logic (e.g. `useMe`'s 401→null mapping) — out of scope for static renders.
- Only `vitest.config.mts` is excluded from the Next build's TypeScript check (it trips
  a dual-vite `Plugin` type clash between `@vitejs/plugin-react`'s vite and vitest's
  bundled vite). Test files and `vitest.setup.ts` stay in the typecheck, so `tsc --noEmit`
  and `next build` continue to type-check test code.

## Consequences

### Positive
- The highest-traffic public page now has regression coverage that runs in CI.
- Vitest shares Vite's transform pipeline, so config is minimal and fast; it's the
  natural fit for a Vite-era React 19 project (vs. Jest's heavier ts/babel setup).
- Mocking boundaries keeps component tests fast and free of network/router flakiness.

### Negative
- Two test "philosophies" now exist in one repo (xUnit/Testcontainers on the backend,
  Vitest/jsdom on the frontend). That's inherent to a split stack, but contributors must
  know which side they're testing.
- jsdom is not a real browser; layout/visual regressions and true responsiveness are not
  covered by these tests (they assert structure/behavior, not pixels).

### Neutral
- No end-to-end runner (Playwright/Cypress) is adopted here; if cross-page flows need
  coverage later, that's a separate decision/ADR.
- "Mock the boundaries" is generic; the specific layer used for URL-level interception
  in component/hook tests is **MSW** (`msw/node`'s `setupServer`), wired into
  `vitest.setup.ts` with `onUnhandledRequest: "error"`. The reusable harness lives at
  `frontend/src/test/` with its own README — see ticket [#34](https://github.com/neboxdev/complidrop/issues/34).
  Tests that pin the api client's own fetch contract (`lib/api.test.ts`,
  `hooks/useAuth.test.tsx`) keep using `vi.stubGlobal("fetch", …)` because they need to
  count calls and replace the global symbol entirely. Both approaches coexist.

## Alternatives considered

### Option A — Jest + React Testing Library
The long-standing default. Rejected: heavier configuration for a Vite/ESM, React 19,
TypeScript project (transform/ESM interop friction), and slower than Vitest, which
reuses the existing Vite transform. RTL is identical on either runner, so nothing is
lost by choosing Vitest.

### Option B — defer test infra, note the gap in the PR
The ticket explicitly allowed "if no infra exists, note it in the PR." Rejected: the
project's current focus is hardening toward launch with full test coverage, so
establishing the runner now (rather than leaving the landing page untested) is the
higher-value path.

## References

- Tickets: [#22](https://github.com/neboxdev/complidrop/issues/22)
- Config: `frontend/vitest.config.mts`, `frontend/vitest.setup.ts`, `frontend/package.json`
- First suite: `frontend/src/app/page.test.tsx`
- CI: `.github/workflows/frontend-ci.yml`
