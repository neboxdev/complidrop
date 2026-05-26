/**
 * Single MSW server instance for the whole vitest run. `vitest.setup.ts`
 * starts it once, resets handlers between tests, and closes it on teardown.
 *
 * Why MSW (vs. `vi.stubGlobal('fetch', …)`):
 *   - The api client in `frontend/src/lib/api.ts` already builds full URLs
 *     against `NEXT_PUBLIC_API_URL`. MSW matches on those URLs, so handlers
 *     read like the route paths reviewers already know — no
 *     "first-arg-shape" indirection.
 *   - One central place to declare default behavior (see `handlers.ts`),
 *     overridable per-test via `server.use(...)`.
 *   - Standard pattern in the React/Next ecosystem; ADR 0003 explicitly left
 *     "a real `fetch` + mock" path open for hook tests and this is that path.
 *
 * Existing tests that pre-date this harness (`api.test.ts`, `useAuth.test.tsx`)
 * keep using `vi.stubGlobal("fetch", …)`. That's intentional: those tests
 * assert the api client's own fetch-level contract (refresh-on-401, retry
 * sequencing) and `vi.stubGlobal` short-circuits MSW because it replaces the
 * global symbol entirely — the precondition MSW relies on no longer exists.
 * Both approaches coexist without interfering with each other.
 */
import { setupServer } from "msw/node";
import { defaultHandlers } from "./handlers";

export const server = setupServer(...defaultHandlers);
