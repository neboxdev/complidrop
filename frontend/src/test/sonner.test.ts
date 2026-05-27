/**
 * Pins the resetSonner() contract added in #74.
 *
 * The setup-file `afterEach` calls `resetSonner()` so a `success` /
 * `error` / etc. call from one test never leaks into the next. That
 * isolation guarantee is the entire reason the harness lift was
 * worth doing — but nothing was asserting it actually worked. A
 * future refactor that (a) added a new spy (e.g. `toastBatch`) but
 * forgot to clear it in `resetSonner()`, or (b) accidentally replaced
 * a spy instance instead of clearing call records, would silently
 * regress the inter-test isolation.
 *
 * Two contracts pinned here:
 *   1. After `resetSonner()`, every spy reports zero calls.
 *   2. Spy IDENTITY is preserved — anyone holding the imported
 *      reference (the 14 migrated test files do exactly this) keeps
 *      pointing at the same Mock instance.
 */
import { describe, it, expect } from "vitest";
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
} from "./sonner";

describe("resetSonner — toast-spy isolation contract (#74)", () => {
  it("clears every spy's call records", () => {
    // Populate every spy with a call so we can assert each one is
    // cleared independently. (Catches a regression where someone
    // added a new toastX spy export but forgot to add toastX.mockClear()
    // to resetSonner.)
    toastSuccess("a");
    toastError("b");
    toastInfo("c");
    toastWarning("d");
    toastLoading("e");
    toastDismiss("f");
    toastMessage("g");
    toastPromise(Promise.resolve(), { loading: "h", success: "i", error: "j" });

    expect(toastSuccess).toHaveBeenCalledTimes(1);
    expect(toastError).toHaveBeenCalledTimes(1);
    expect(toastInfo).toHaveBeenCalledTimes(1);
    expect(toastWarning).toHaveBeenCalledTimes(1);
    expect(toastLoading).toHaveBeenCalledTimes(1);
    expect(toastDismiss).toHaveBeenCalledTimes(1);
    expect(toastMessage).toHaveBeenCalledTimes(1);
    expect(toastPromise).toHaveBeenCalledTimes(1);

    resetSonner();

    expect(toastSuccess).toHaveBeenCalledTimes(0);
    expect(toastError).toHaveBeenCalledTimes(0);
    expect(toastInfo).toHaveBeenCalledTimes(0);
    expect(toastWarning).toHaveBeenCalledTimes(0);
    expect(toastLoading).toHaveBeenCalledTimes(0);
    expect(toastDismiss).toHaveBeenCalledTimes(0);
    expect(toastMessage).toHaveBeenCalledTimes(0);
    expect(toastPromise).toHaveBeenCalledTimes(0);
  });

  it("preserves spy identity across resetSonner() — imports stay valid", () => {
    // The 14 migrated test files do `import { toastSuccess } from
    // "@/test"` once at module load and assert on the SAME reference
    // for every test in the file. If resetSonner() ever swapped out
    // the underlying Mock instance instead of clearing it, those
    // long-held references would point at a stale spy that production
    // code's `toast.success(...)` calls never hit — every test
    // assertion would mysteriously read zero calls.
    const successRef = toastSuccess;
    const errorRef = toastError;

    toastSuccess("x");
    toastError("y");
    resetSonner();

    expect(toastSuccess).toBe(successRef);
    expect(toastError).toBe(errorRef);

    // Sanity: the references still receive calls after the reset
    // (otherwise the reset would have orphaned them).
    successRef("z");
    expect(toastSuccess).toHaveBeenCalledTimes(1);
    expect(toastSuccess).toHaveBeenCalledWith("z");
  });
});
