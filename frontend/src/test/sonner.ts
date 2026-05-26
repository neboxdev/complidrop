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
 *     pair; lifting it removes the duplication and the foot-gun where a
 *     test file forgot to spy on `info` / `loading` and silently lost
 *     coverage of that path.
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
