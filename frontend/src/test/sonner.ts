/**
 * Mutable spies for `sonner` toast methods.
 *
 * `vitest.setup.ts` mocks `sonner` once with these spies — tests import
 * them from `@/test` and assert on toast calls without re-declaring the
 * `vi.mock("sonner", …)` boilerplate at the top of every file. The
 * setup-file `afterEach` calls `resetSonner()` so a `success` call from
 * one test never leaks into the next.
 *
 * Why module-level spies instead of `vi.mock` in every test file:
 *   - Per-file `vi.mock` factories can't capture per-test variables (the
 *     factory is hoisted ABOVE every `vi.hoisted`/`let` in the file).
 *     Exporting stable spies from this module and reading them from the
 *     setup-file factory is the standard workaround.
 *   - 14 test files used to redeclare the same `vi.hoisted` + `vi.mock`
 *     pair; lifting it removes the duplication. As a bonus, the harness
 *     extends the spy surface to every documented `toast.*` method —
 *     production code that starts calling `toast.warning(...)` (or any
 *     currently-unused method) becomes automatically observable in
 *     tests instead of throwing `TypeError: toast.warning is not a
 *     function` only in test (because the previous per-file mocks only
 *     wired the methods each file happened to use).
 *
 * Unlike `navigation.ts`, there is NO `setSonnerState` writer. The
 * spies are stable Mock references and tests assert on them directly;
 * there is nothing to override field-by-field (the way navigation.ts's
 * `setNavigationState` swaps a single `router.push` spy while keeping
 * the other no-op router spies). For a custom shape (a throwing spy,
 * a real `toast.promise` implementation), use the per-file
 * `vi.mock("sonner", …)` escape hatch — Vitest's per-file mock
 * registry overrides the setup-file mock within that file's scope.
 *
 * Each spy is a STABLE reference — `resetSonner()` clears call records
 * but does NOT replace the instances, so test files that
 * `import { toastSuccess } from "@/test"` always assert on the same
 * Mock the mock factory is routing calls into.
 *
 * Test files that need a custom sonner shape (e.g. a spy that throws,
 * a `toast.promise` mock) still call `vi.mock("sonner", …)` at the top
 * of their own file — Vitest's per-file mock registry overrides any
 * setup-file mock within the file's own module scope.
 */
import { vi, type Mock } from "vitest";

export const toastSuccess: Mock = vi.fn();
export const toastError: Mock = vi.fn();
export const toastInfo: Mock = vi.fn();
export const toastWarning: Mock = vi.fn();
export const toastLoading: Mock = vi.fn();
export const toastDismiss: Mock = vi.fn();
export const toastMessage: Mock = vi.fn();
export const toastPromise: Mock = vi.fn();

/**
 * Clear all toast spies' call records between tests. Called by the
 * setup-file `afterEach` alongside `resetNavigation()`. Does NOT
 * replace the spy instances — anyone holding a reference to
 * `toastSuccess` from `@/test` keeps the same Mock across tests.
 */
export function resetSonner(): void {
  toastSuccess.mockClear();
  toastError.mockClear();
  toastInfo.mockClear();
  toastWarning.mockClear();
  toastLoading.mockClear();
  toastDismiss.mockClear();
  toastMessage.mockClear();
  toastPromise.mockClear();
}
