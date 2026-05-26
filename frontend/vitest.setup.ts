import "@testing-library/jest-dom/vitest";
import { afterAll, afterEach, beforeAll, vi } from "vitest";
import { cleanup } from "@testing-library/react";
import { server } from "./src/test/server";
import { navState, resetNavigation } from "./src/test/navigation";
import {
  toastSuccess,
  toastError,
  toastInfo,
  toastWarning,
  toastLoading,
  toastDismiss,
  toastMessage,
  toastPromise,
  resetSonner,
} from "./src/test/sonner";

// `NEXT_PUBLIC_API_URL` is pinned in `vitest.config.mts` via `test.env`, which
// runs strictly before this file's imports resolve. `frontend/src/lib/api.ts`'s
// module-load-time `API_BASE` therefore sees the test origin
// ("http://localhost:5292") and MSW handlers built from `TEST_API_BASE` agree.

// Default mock for `next/navigation`. Test files that need different shapes
// (e.g. a hoisted `useSearchParams` spy) still call `vi.mock("next/navigation",
// …)` at the top of their own file — Vitest's per-file mock registry
// overrides any setup-file mock within the file's own module scope
// (file-level mocks win because they're file-scoped, not because of hoist
// order across files).
//
// Reads from `navState` (a mutable container in `src/test/navigation.ts`);
// tests drive routing via `renderWithProviders({ router, params, ... })` or
// the lower-level `setNavigationState(…)` helper. `notFound` and `redirect`
// throw a sentinel error so component code after them cannot silently keep
// running (matching real Next semantics); tests still assert on the spy.
vi.mock("next/navigation", () => ({
  useRouter: () => navState.router,
  useParams: () => navState.params,
  useSearchParams: () => navState.searchParams,
  usePathname: () => navState.pathname,
  notFound: (...args: unknown[]) => navState.notFound(...args),
  redirect: (...args: unknown[]) => navState.redirect(...args),
}));

// Default mock for `sonner`. Same shape as the navigation mock — reads
// the stable spy references exported by `src/test/sonner.ts` so per-file
// `vi.hoisted` + `vi.mock` boilerplate isn't needed. The 14 component-
// test files that used to redeclare this drop their copies and `import
// { toastSuccess, toastError } from "@/test"` directly. `Toaster` is
// stubbed to render nothing.
//
// Test files that need a custom shape (e.g. throw on success) can still
// call `vi.mock("sonner", …)` at the top of their own file; Vitest's
// per-file mock registry wins for that file.
vi.mock("sonner", () => ({
  toast: {
    success: toastSuccess,
    error: toastError,
    info: toastInfo,
    warning: toastWarning,
    loading: toastLoading,
    dismiss: toastDismiss,
    message: toastMessage,
    promise: toastPromise,
  },
  Toaster: () => null,
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
  resetSonner();
});

afterAll(() => server.close());
