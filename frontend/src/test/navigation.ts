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

/**
 * Subscribers to `navState` changes — the bridge that lets a `router.push` /
 * `router.replace` actually re-render a component reading `useSearchParams()`
 * or `usePathname()`. `vitest.setup.ts` wires those two hooks through
 * `useSyncExternalStore(subscribeNavigation, …)`.
 */
const navSubscribers = new Set<() => void>();

export function subscribeNavigation(onChange: () => void): () => void {
  navSubscribers.add(onChange);
  return () => {
    navSubscribers.delete(onChange);
  };
}

function notifyNavigation(): void {
  for (const notify of [...navSubscribers]) notify();
}

/**
 * Apply a navigation href to `navState`, the way the real App Router does.
 *
 * Without this, `router.replace` was an inert spy and `useSearchParams()`
 * returned the same object forever — so any bug about the URL and component
 * state disagreeing (#370) was literally inexpressible in a test: the mock
 * could not represent "the URL changed". Tests that only care that a
 * navigation was REQUESTED still inject their own `replace` spy, which
 * overrides this (see `setNavigationState`'s field-by-field merge).
 *
 * **The commit is DEFERRED on purpose.** The real App Router routes
 * `push`/`replace` through a transition, so the new `useSearchParams()` value
 * lands in a LATER commit — a component that calls `replace` from an event
 * handler re-renders at least once with the OLD query string still readable.
 * That one-commit lag is not an implementation detail to paper over; it is
 * precisely the window #370's scenario A lived in (an effect re-running
 * against the pre-navigation snapshot and re-dispatching the param the click
 * had just cleared). A synchronous mock closes that window and would let the
 * bug pass its own regression test — verified: with a synchronous apply, the
 * scenario-A test passes against the UNFIXED page.
 *
 * `searchParams` is rebuilt as a NEW object on every navigation because
 * `useSyncExternalStore` compares snapshots by reference — mutating the
 * existing instance in place would not re-render.
 */
function applyNavigation(href: unknown): void {
  if (typeof href !== "string") return;
  // Absolute URLs (rare in-app, but `router.replace(new URL(...).toString())`
  // is legal) parse via URL; relative ones are split by hand because the
  // WHATWG parser demands a base.
  let pathname: string;
  let query: string;
  if (/^[a-z][a-z0-9+.-]*:\/\//i.test(href)) {
    const parsed = new URL(href);
    pathname = parsed.pathname;
    query = parsed.search.replace(/^\?/, "");
  } else {
    const [beforeHash] = href.split("#");
    const queryAt = beforeHash.indexOf("?");
    pathname = queryAt === -1 ? beforeHash : beforeHash.slice(0, queryAt);
    query = queryAt === -1 ? "" : beforeHash.slice(queryAt + 1);
  }
  // Deferred to a macrotask so the calling component's own synchronous
  // re-render (and its effects) run FIRST against the pre-navigation URL —
  // see the transition note above.
  setTimeout(() => {
    // A hash-only or query-only href ("?page=2") leaves the path alone.
    if (pathname) navState.pathname = pathname;
    navState.searchParams = new URLSearchParams(query);
    notifyNavigation();
  }, 0);
}

function makeRouter(): RouterMock {
  return {
    // push/replace are live: they're still assertable spies, but they also
    // move `navState` so subscribed components re-render against the new URL.
    push: vi.fn(applyNavigation),
    replace: vi.fn(applyNavigation),
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
  // Re-render anything already mounted — this helper is usable mid-test to
  // simulate an external URL change (e.g. the #370 same-route sidebar click).
  notifyNavigation();
}
