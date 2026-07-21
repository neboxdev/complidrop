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
 * Deferred navigation commits that haven't fired yet. `resetNavigation()`
 * cancels them so a navigation can never cross a test boundary.
 */
const pendingApplies = new Set<ReturnType<typeof setTimeout>>();

/**
 * How long a dispatched navigation takes to commit, in ms (default 0 — the
 * next macrotask).
 *
 * Applies to BOTH commit paths — `router.push`/`replace` and the History-API
 * bridge — because both are deferred in the real App Router (see
 * `applyHistoryUrl`). The underlying latencies differ in kind (an RSC fetch vs
 * a `startTransition` commit React may schedule late or interrupt), but for a
 * test the useful knob is the same one: how long the component keeps rendering
 * against the OLD `useSearchParams()` after it dispatched a write.
 *
 * The real App Router's commit latency is per-navigation: each one waits on its
 * own RSC fetch, so two navigations dispatched together land at different times
 * and can even land out of order. With a fixed 0ms defer every in-flight
 * navigation shares one deadline and lands in one drain, which makes a whole
 * class of interleaving untestable — including "one of our OWN earlier
 * navigations commits while a later one is still in flight", the window two
 * #370 defects lived in.
 *
 * Set it per navigation to stagger commits:
 *
 *     setNavigationCommitDelay(5);   fireEvent.change(statusSelect);  // lands first
 *     setNavigationCommitDelay(50);  fireEvent.change(typeSelect);    // still in flight
 *     act(() => vi.advanceTimersByTime(10));                          // only the first landed
 *
 * Reset to 0 by `resetNavigation()`.
 */
let commitDelayMs = 0;

export function setNavigationCommitDelay(ms: number): void {
  commitDelayMs = ms;
}

/**
 * Split an href into the parts a navigation applies. Shared by the router
 * mock and the History-API bridge so both agree on edge cases.
 *
 * `hasQuery` distinguishes "set the query" from "leave it alone": "/x?a=1" and
 * "?a=1" carry one, "/x" and "#top" don't. A path-only href still clears the
 * query (that's what makes Clear's "/documents" work); a hash-only href
 * touches nothing.
 */
function parseHref(href: string): { pathname: string; query: string; hasQuery: boolean } | null {
  // Absolute URLs (rare in-app, but `router.replace(new URL(...).toString())`
  // is legal) parse via URL; relative ones are split by hand because the
  // WHATWG parser demands a base.
  if (/^[a-z][a-z0-9+.-]*:\/\//i.test(href)) {
    const parsed = new URL(href);
    // An absolute URL always fully specifies the query.
    return { pathname: parsed.pathname, query: parsed.search.replace(/^\?/, ""), hasQuery: true };
  }
  const [beforeHash] = href.split("#");
  const queryAt = beforeHash.indexOf("?");
  return {
    pathname: queryAt === -1 ? beforeHash : beforeHash.slice(0, queryAt),
    query: queryAt === -1 ? "" : beforeHash.slice(queryAt + 1),
    hasQuery: queryAt !== -1,
  };
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
/**
 * The unpatched `history.replaceState`, captured before the bridge below wraps
 * it. Used to move `window.location` WITHOUT re-entering our own bridge (which
 * would queue a redundant deferred commit, and recurse).
 */
const nativeReplaceState =
  typeof window !== "undefined" ? window.history.replaceState.bind(window.history) : null;

/**
 * Move `window.location` to match a navigation.
 *
 * The harness models the URL in two places because the browser does: the
 * address bar (`window.location`, which `history.replaceState` updates
 * SYNCHRONOUSLY) and the router's snapshot (`useSearchParams()`, which lands a
 * commit later). Code under test reads both — `writeFilters` composes on
 * `window.location.search` precisely BECAUSE it is the synchronous one (#370)
 * — so a harness that moved only `navState` would make the page compose on a
 * URL that never changes, and every filter write would clobber the last.
 */
function syncWindowLocation(pathname: string, query: string): void {
  if (!nativeReplaceState) return;
  const path = pathname || window.location.pathname;
  nativeReplaceState(null, "", query ? `${path}?${query}` : path);
}

function applyNavigation(href: unknown): void {
  if (typeof href !== "string") return;
  const target = parseHref(href);
  if (!target) return;
  const { pathname, query, hasQuery } = target;
  // Deferred to a macrotask so the calling component's own synchronous
  // re-render (and its effects) run FIRST against the pre-navigation URL —
  // see the transition note above.
  //
  // The handle is TRACKED because `resetNavigation()` (the global afterEach)
  // rebuilds navState synchronously and cannot cancel a queued timer: a
  // navigation dispatched near the end of a test — or from a late async
  // callback, like the 401 -> /login redirect — would otherwise land AFTER the
  // reset and rewrite pathname/searchParams inside the NEXT test, re-rendering
  // whatever it has mounted, outside `act`. Vitest drains afterEach on
  // microtasks, so a macrotask always loses that race.
  const handle = setTimeout(() => {
    pendingApplies.delete(handle);
    // A hash-only href leaves BOTH the path and the query alone; a query-only
    // href ("?page=2") keeps the path.
    if (!hasQuery && !pathname) return;
    if (pathname) navState.pathname = pathname;
    navState.searchParams = new URLSearchParams(query);
    // A router navigation moves the address bar and the router snapshot
    // TOGETHER, at commit — unlike the History bridge, which splits them.
    syncWindowLocation(pathname, query);
    notifyNavigation();
  }, commitDelayMs);
  pendingApplies.add(handle);
}

/**
 * Bridge `window.history.pushState` / `replaceState` into `navState`.
 *
 * Next's App Router integrates the native History API: those calls update the
 * history entry AND sync `usePathname` / `useSearchParams`, with no route
 * navigation and no RSC fetch (docs: "Native History API" — the documented
 * path for list filtering/sorting). The documents page uses it, so the harness
 * has to model it or those writes would be invisible to the component.
 *
 * **The router sync is DEFERRED, and the two halves split apart.** This was
 * modelled as fully synchronous and that was wrong — the mistake that made
 * #370's second review pass necessary. Next's patch
 * (`next/dist/client/components/app-router.js`, `applyUrlFromHistoryPushReplace`)
 * routes the router update through `startTransition`:
 *
 *     const applyUrlFromHistoryPushReplace = (url) => {
 *       startTransition(() => { dispatchAppRouterAction({ type: ACTION_RESTORE, … }) })
 *     }
 *     window.history.replaceState = function (data, _unused, url) {
 *       if (url) applyUrlFromHistoryPushReplace(url)   // deferred
 *       return originalReplaceState(data, _unused, url) // synchronous
 *     }
 *
 * So after a `replaceState` returns:
 *   - `window.location.search` is ALREADY the new value (the native call), and
 *   - `useSearchParams()` still returns the OLD one until the transition
 *     commits.
 *
 * Skipping the RSC fetch makes the window narrower than `router.replace`'s, not
 * absent. Modelling it as absent let a page compose filter writes on a
 * transition-deferred value and still pass its own regression test.
 *
 * The synchronous half is handled by the bridge below calling the ORIGINAL
 * method; this function is only the deferred half.
 */
function applyHistoryUrl(url: unknown): void {
  if (typeof url !== "string" || url === "") return;
  const target = parseHref(url);
  if (!target) return;
  const handle = setTimeout(() => {
    pendingApplies.delete(handle);
    if (target.pathname) navState.pathname = target.pathname;
    if (target.hasQuery || target.pathname) {
      navState.searchParams = new URLSearchParams(target.query);
    }
    notifyNavigation();
  }, commitDelayMs);
  pendingApplies.add(handle);
}

if (typeof window !== "undefined" && !("__cdNavBridge" in window.history)) {
  Object.defineProperty(window.history, "__cdNavBridge", { value: true });
  for (const method of ["pushState", "replaceState"] as const) {
    const original = window.history[method].bind(window.history);
    window.history[method] = (data: unknown, unused: string, url?: string | URL | null) => {
      // Native first: this is the SYNCHRONOUS half — `window.location` is the
      // new URL the instant this returns. `applyHistoryUrl` then queues the
      // DEFERRED half (the `useSearchParams()` snapshot). Splitting them is the
      // whole point; see `applyHistoryUrl`.
      original(data, unused, url);
      applyHistoryUrl(typeof url === "string" ? url : url?.toString());
    };
  }
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
  // Cancel any deferred commit still queued from this test. Without this the
  // reset below is not authoritative: a macrotask scheduled by a navigation
  // late in test N fires during test N+1 and rewrites the URL under it.
  for (const handle of pendingApplies) clearTimeout(handle);
  pendingApplies.clear();
  commitDelayMs = 0;
  // RTL's `cleanup()` unmounts components (which unsubscribes them) before this
  // runs, but a test that subscribes by hand and throws before its `unsubscribe`
  // would leak a callback into the next test — and it would fire, because
  // `notifyNavigation` iterates whatever is in the set. Dropping them here makes
  // the reset authoritative rather than merely usually-sufficient.
  navSubscribers.clear();
  navState.router = makeRouter();
  navState.params = {};
  navState.searchParams = new URLSearchParams();
  navState.pathname = "/";
  navState.notFound = makeNotFound();
  navState.redirect = makeRedirect();
  // Keep the address bar in step with the snapshot — otherwise a query string
  // from test N is still readable via `window.location.search` in test N+1.
  syncWindowLocation("/", "");
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
  // Seeding is an AT-REST url: the address bar and the router snapshot agree
  // (they only diverge for the duration of a dispatched write). Move
  // `window.location` too, or a page that composes on `window.location.search`
  // would read "/" no matter what the test seeded — and `renderWithProviders`'s
  // `searchParams` option routes through here, so that is every seeded test.
  if (patch.searchParams !== undefined || patch.pathname !== undefined) {
    syncWindowLocation(navState.pathname, navState.searchParams.toString());
  }
  // Re-render anything already mounted — this helper is usable mid-test to
  // simulate an external URL change (e.g. the #370 same-route sidebar click).
  notifyNavigation();
}
