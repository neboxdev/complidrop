/**
 * `renderWithProviders` — the single render entry point for component tests.
 *
 * Provides:
 *   - A fresh `QueryClient` with retries off and `staleTime: Infinity`, so
 *     seeded cache data is always served from cache regardless of which
 *     hook's per-query `staleTime` happens to be (zero coupling between
 *     the harness and any hook's internal options). Tests that genuinely
 *     want refetch-on-mount behavior pass their own client.
 *   - Optional cache priming for `useMe()` via the `auth` option — pass
 *     `authedMe` to render as if the user just logged in, pass `null` for
 *     an explicit "I AM anonymous" assertion (avoids the 401 round-trip
 *     in the default MSW handler).
 *   - Router + params injection via `setNavigationState` (the setup-file
 *     `vi.mock("next/navigation", …)` reads from `navState`).
 *
 * Returns the React Testing Library `RenderResult` plus the `QueryClient`
 * the test can introspect / mutate (e.g. to assert cache invalidation
 * after a mutation: `expect(qc.getQueryData(["documents", "list"])).…`).
 */
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { render, type RenderOptions, type RenderResult } from "@testing-library/react";
import type { ReactElement, ReactNode } from "react";
import { ME_KEY, ME_PROBE_KEY, type Me } from "@/hooks/useAuth";
import { createQueryClient } from "@/lib/query-client";
import { setNavigationState, type NavigationState, type RouterMock } from "./navigation";

/**
 * `renderHook` wrapper companion to `renderWithProviders`. The 3 hook test
 * files (useDocuments / useDashboard / useVendors) all need a
 * `QueryClientProvider` with the same retry / staleTime / gcTime tuning
 * as the page-level harness; this helper hands them the same client +
 * Wrapper without re-deriving it. See `src/test/README.md` for the
 * `gcTime: Infinity` rationale.
 *
 * Usage:
 *
 *     const { qc, Wrapper } = createTestWrapper();
 *     const { result } = renderHook(() => useDocuments(), { wrapper: Wrapper });
 *     // …
 *     expect(qc.getQueryData(["documents", "list"])).toMatchObject(...);
 */
export function createTestWrapper(): {
  qc: QueryClient;
  Wrapper: (props: { children: ReactNode }) => ReactElement;
} {
  const qc = createTestQueryClient();
  function Wrapper({ children }: { children: ReactNode }) {
    return <QueryClientProvider client={qc}>{children}</QueryClientProvider>;
  }
  return { qc, Wrapper };
}

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

export function createTestQueryClient(): QueryClient {
  // Routed through the production `createQueryClient` so tests exercise the
  // REAL global error handling (auth-error → null the me-cache → redirect;
  // `meta: { errorToast: true }` mutation failure → toast) instead of a
  // hand-rolled client that silently diverges from production. Only the
  // defaultOptions differ — test-tuned for determinism:
  //   - retry: false        — a 4xx in a test should fail fast.
  //   - staleTime: Infinity — seeded cache entries never go stale.
  //   - gcTime: Infinity    — seeded entries must SURVIVE for cache
  //     assertions even when no component is observing them. The login
  //     page, for example, only uses useLogin (a mutation) — nothing
  //     subscribes to ME_KEY directly, so with the default `gcTime: 0`
  //     the mutation's onSuccess write would be reaped before the test
  //     could read it. Per-render isolation is provided by spawning a
  //     fresh QueryClient per render, not by per-query GC, so Infinity
  //     here doesn't leak between tests.
  //   - refetchOnWindowFocus: false — jsdom triggers focus events
  //     from the test runner; refetches would race the MSW handlers.
  return createQueryClient({
    queries: {
      retry: false,
      gcTime: Infinity,
      staleTime: Infinity,
      refetchOnWindowFocus: false,
    },
    mutations: { retry: false },
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
    client.setQueryData(ME_KEY, auth);
    client.setQueryData(ME_PROBE_KEY, auth);
  }

  // Always call through — `setNavigationState` no-ops on each undefined
  // field, so unconditionally invoking keeps the call surface uniform
  // (no asymmetric guard). The shared `navState` is reset between tests
  // by `vitest.setup.ts`'s afterEach, so unset fields from one test
  // never leak into the next.
  setNavigationState({ router, params, searchParams, pathname });

  function Wrapper({ children }: { children: ReactNode }) {
    return <QueryClientProvider client={client}>{children}</QueryClientProvider>;
  }

  const result = render(ui, { wrapper: Wrapper, ...rtl });
  return { ...result, queryClient: client };
}
