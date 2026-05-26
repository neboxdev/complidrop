/**
 * `renderWithProviders` — the single render entry point for component tests.
 *
 * Provides:
 *   - A fresh `QueryClient` with retries off and `staleTime: 0`, so a test
 *     never reuses cached data from the previous test.
 *   - Optional cache priming for `useMe()` via the `auth` option — pass
 *     `authedMe` to render as if the user just logged in, pass `null` for an
 *     explicit "I AM anonymous" assertion (avoids the 401 round-trip in the
 *     default MSW handler).
 *   - Router + params injection via `setNavigationState` (the setup-file
 *     `vi.mock("next/navigation", …)` reads from `navState`).
 *
 * Returns the React Testing Library `RenderResult` plus the `QueryClient`
 * the test can introspect / mutate (e.g. to assert cache invalidation).
 */
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { render, type RenderOptions, type RenderResult } from "@testing-library/react";
import type { ReactElement, ReactNode } from "react";
import type { Me } from "@/hooks/useAuth";
import { setNavigationState, type NavigationState, type RouterMock } from "./navigation";

export type RenderWithProvidersOptions = Omit<RenderOptions, "wrapper"> & {
  /**
   * Prime the `useMe()` query cache.
   *   - omitted     → use whatever MSW returns (default: 401 → null).
   *   - `null`      → seed both ME_KEY and ME_PROBE_KEY with null directly,
   *                   skipping the network entirely.
   *   - `Me` object → seed both keys with the user; useMe() resolves
   *                   immediately without firing a network call.
   *
   * Use the seed paths when the test's subject does NOT need to exercise
   * the auth fetch itself (most component tests). Drop the seed when the
   * test IS the auth fetch (those use `renderHook` + MSW directly).
   */
  auth?: Me | null;

  /**
   * Override the QueryClient — useful for tests that need to assert across
   * multiple renders sharing one client, or to pre-seed extra cache entries.
   * When omitted, a fresh client is created per render.
   */
  queryClient?: QueryClient;

  /**
   * Patch the router methods exposed by `useRouter()`. Defaults are vi.fn()
   * spies for every method; supply real-ish behavior or a custom spy when
   * the test needs to assert a navigation call.
   */
  router?: Partial<RouterMock>;

  /**
   * Set `useParams()` return value — e.g. `{ token: "abc" }` for portal
   * tests, or `{ id: "d_completed_01" }` for the document detail page.
   */
  params?: NavigationState["params"];

  /**
   * Set `useSearchParams()` return value — accepts a `URLSearchParams` or a
   * record (e.g. `{ plan: "annual" }`) for ergonomics.
   */
  searchParams?: URLSearchParams | Record<string, string>;

  /**
   * Set `usePathname()` return value.
   */
  pathname?: string;
};

// Cache keys mirror `frontend/src/hooks/useAuth.ts`. Keep in sync; tests that
// import from this file rely on the two staying lockstep.
const ME_KEY = ["auth", "me"] as const;
const ME_PROBE_KEY = ["auth", "me", "probe"] as const;

function createTestQueryClient(): QueryClient {
  return new QueryClient({
    defaultOptions: {
      // No retries: a 4xx in a test should fail fast, not hammer the mock.
      queries: { retry: false, gcTime: 0, staleTime: 0, refetchOnWindowFocus: false },
      mutations: { retry: false },
    },
  });
}

export function renderWithProviders(
  ui: ReactElement,
  {
    auth,
    queryClient,
    router,
    params,
    searchParams,
    pathname,
    ...rtl
  }: RenderWithProvidersOptions = {},
): RenderResult & { queryClient: QueryClient } {
  const client = queryClient ?? createTestQueryClient();

  if (auth !== undefined) {
    client.setQueryData([...ME_KEY], auth);
    client.setQueryData([...ME_PROBE_KEY], auth);
  }

  if (router || params || searchParams !== undefined || pathname !== undefined) {
    setNavigationState({ router, params, searchParams, pathname });
  }

  function Wrapper({ children }: { children: ReactNode }) {
    return <QueryClientProvider client={client}>{children}</QueryClientProvider>;
  }

  const result = render(ui, { wrapper: Wrapper, ...rtl });
  return { ...result, queryClient: client };
}

/**
 * Convenience re-export so test files can grab everything from one path:
 *
 *     import { renderWithProviders, authedMe, server, url } from "@/test";
 */
export { server } from "./server";
export { url, jsonOk, jsonError, TEST_API_BASE } from "./helpers";
export * from "./fixtures";
export { navState, resetNavigation, setNavigationState } from "./navigation";
