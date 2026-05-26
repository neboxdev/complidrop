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
 *     override this — Vitest's per-file mock registry overrides any
 *     setup-file mock within the file's own module scope (file-level mocks
 *     win because they're file-scoped, not because of hoist order across
 *     files). Existing tests (e.g. `register-form.test.tsx`) keep working
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
  /**
   * `notFound()` in real Next throws a NEXT_NOT_FOUND error to halt
   * rendering. A bare `vi.fn()` returning undefined would let a component's
   * "abort and bail" branch keep executing — masking real bugs where code
   * after a `notFound()` call could leak data. The mock below throws a
   * sentinel so abort semantics survive, and tests can still assert on the
   * spy's call args. Rebuilt every `resetNavigation()` so call counts don't
   * leak between tests in the same file.
   */
  notFound: Mock;
  /**
   * Same story as `notFound` — real Next `redirect()` throws to halt
   * rendering. We throw a sentinel error here so component code after the
   * redirect cannot silently keep running under the mock. Tests inspect the
   * spy's first-call args to assert the destination URL.
   */
  redirect: Mock;
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

class NextNotFoundError extends Error {
  digest = "NEXT_NOT_FOUND";
  constructor() {
    super("NEXT_NOT_FOUND");
  }
}

class NextRedirectError extends Error {
  digest: string;
  constructor(public url: string, type: "replace" | "push" = "replace") {
    super(`NEXT_REDIRECT;${type};${url}`);
    this.digest = `NEXT_REDIRECT;${type};${url}`;
  }
}

function makeNotFound(): Mock {
  return vi.fn(() => {
    throw new NextNotFoundError();
  });
}

function makeRedirect(): Mock {
  return vi.fn((url: string, type: "replace" | "push" = "replace") => {
    throw new NextRedirectError(url, type);
  });
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
  notFound: makeNotFound(),
  redirect: makeRedirect(),
};

/**
 * Restore defaults between tests so a `push` spy from one test never leaks
 * into the next. `vitest.setup.ts` calls this in `afterEach`. Rebuilds every
 * mock — including `notFound` / `redirect` — so call counts never carry
 * across tests in the same file.
 */
export function resetNavigation(): void {
  navState.router = makeRouter();
  navState.params = {};
  navState.searchParams = new URLSearchParams();
  navState.pathname = "/";
  navState.notFound = makeNotFound();
  navState.redirect = makeRedirect();
}

/**
 * Apply a partial state update.
 *
 *   - `router` merges field-by-field, so a test that wants to spy ONLY on
 *     `push` doesn't lose the other no-op spies.
 *   - `params`, `searchParams`, `pathname` REPLACE (no field-level merge).
 *   - Pass an undefined field to leave it untouched.
 */
export function setNavigationState(patch: {
  router?: Partial<RouterMock>;
  params?: NavigationState["params"];
  searchParams?: URLSearchParams | Record<string, string>;
  pathname?: string;
}): void {
  if (patch.router) Object.assign(navState.router, patch.router);
  if (patch.params !== undefined) navState.params = patch.params;
  if (patch.searchParams !== undefined) {
    navState.searchParams =
      patch.searchParams instanceof URLSearchParams
        ? patch.searchParams
        : new URLSearchParams(patch.searchParams);
  }
  if (patch.pathname !== undefined) navState.pathname = patch.pathname;
}
