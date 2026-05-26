/**
 * Mutable container for `next/navigation` mocks.
 *
 * `vitest.setup.ts` mocks `next/navigation` once with these getters — tests
 * (or the `renderWithProviders` helper) write into `navState` to drive a
 * specific component path.
 *
 * Why mutable state instead of `vi.mock` in every test file:
 *   - File-level `vi.mock` factories can't capture per-test variables (the
 *     factory is hoisted ABOVE every `vi.hoisted`/`let` in the file).
 *     Routing into a shared mutable object is the standard workaround.
 *   - Test files that need their own `vi.mock("next/navigation", …)` still
 *     override this — Vitest hoists the file-level mock above the setup-file
 *     one. Existing tests (e.g. `register-form.test.tsx`) keep working
 *     unchanged.
 */
import { vi, type Mock } from "vitest";

export type RouterMock = {
  push: Mock;
  replace: Mock;
  back: Mock;
  forward: Mock;
  refresh: Mock;
  prefetch: Mock;
};

export type NavigationState = {
  router: RouterMock;
  params: Record<string, string | string[] | undefined>;
  searchParams: URLSearchParams;
  pathname: string;
};

function makeRouter(): RouterMock {
  return {
    push: vi.fn(),
    replace: vi.fn(),
    back: vi.fn(),
    forward: vi.fn(),
    refresh: vi.fn(),
    prefetch: vi.fn(),
  };
}

/**
 * Live state read by the setup-file `vi.mock("next/navigation", …)`.
 * Mutate via `setNavigationState` or pass options to `renderWithProviders`.
 */
export const navState: NavigationState = {
  router: makeRouter(),
  params: {},
  searchParams: new URLSearchParams(),
  pathname: "/",
};

/**
 * Restore defaults between tests so a `push` spy from one test never leaks
 * into the next. `vitest.setup.ts` calls this in `afterEach`.
 */
export function resetNavigation(): void {
  navState.router = makeRouter();
  navState.params = {};
  navState.searchParams = new URLSearchParams();
  navState.pathname = "/";
}

/**
 * Apply a partial state update. Router overrides merge field-by-field so a
 * test that wants to spy ONLY on `push` doesn't lose the other no-op spies.
 */
export function setNavigationState(patch: {
  router?: Partial<RouterMock>;
  params?: NavigationState["params"];
  searchParams?: URLSearchParams | Record<string, string>;
  pathname?: string;
}): void {
  if (patch.router) Object.assign(navState.router, patch.router);
  if (patch.params) navState.params = patch.params;
  if (patch.searchParams !== undefined) {
    navState.searchParams =
      patch.searchParams instanceof URLSearchParams
        ? patch.searchParams
        : new URLSearchParams(patch.searchParams);
  }
  if (patch.pathname !== undefined) navState.pathname = patch.pathname;
}
