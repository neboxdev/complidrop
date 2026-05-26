import "@testing-library/jest-dom/vitest";
import { afterAll, afterEach, beforeAll, vi } from "vitest";
import { cleanup } from "@testing-library/react";

// Pin the API origin BEFORE `frontend/src/lib/api.ts` is imported by any test —
// `API_BASE` is computed once at module load, so a later assignment wouldn't
// take effect. Tests + MSW handlers must agree on this exact origin, which is
// the production fallback ("http://localhost:5292").
process.env.NEXT_PUBLIC_API_URL = "http://localhost:5292";

import { server } from "./src/test/server";
import { navState, resetNavigation } from "./src/test/navigation";

// Default mock for `next/navigation`. Test files that need different shapes
// (e.g. a hoisted `useSearchParams` spy) still call `vi.mock("next/navigation",
// …)` at the top of their own file — Vitest hoists the file-level mock above
// this setup-file one, so per-file overrides win.
//
// Reads from `navState` (a mutable container in `src/test/navigation.ts`);
// tests drive routing via `renderWithProviders({ router, params, ... })` or
// the lower-level `setNavigationState(…)` helper.
vi.mock("next/navigation", () => ({
  useRouter: () => navState.router,
  useParams: () => navState.params,
  useSearchParams: () => navState.searchParams,
  usePathname: () => navState.pathname,
  // notFound() throws in real Next; tests assert on the spy instead.
  notFound: vi.fn(),
  // redirect() throws in real Next too; same treatment.
  redirect: vi.fn(),
}));

// MSW lifecycle:
//   - `listen({ onUnhandledRequest: 'error' })` makes any unmocked request
//     fail the test — that's the whole point of "no real network calls".
//   - `resetHandlers()` drops per-test `server.use(...)` overrides so the
//     next test starts from `defaultHandlers`.
//   - `close()` releases the interceptor on suite teardown.
beforeAll(() => server.listen({ onUnhandledRequest: "error" }));

// With `globals: false`, RTL can't auto-register its cleanup on the global
// afterEach, so unmount between tests explicitly — otherwise renders
// accumulate in the jsdom document and queries leak across tests.
afterEach(() => {
  cleanup();
  server.resetHandlers();
  resetNavigation();
});

afterAll(() => server.close());
