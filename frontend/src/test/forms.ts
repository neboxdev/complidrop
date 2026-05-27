/**
 * Form-test helpers — container-scoped fillInputByName / submitFormIn.
 *
 * Three form-test files used to redeclare nearly-identical
 * `fillField` + `submitForm` helpers, each grabbing the input via
 * `document.querySelector('input[name="..."]')` and the form via
 * `document.querySelector("form")`. Two foot-guns:
 *
 *   1. The global lookup silently picks the wrong input if a future
 *      composite test renders two forms in the same document.
 *   2. The pattern survived only because the auth forms didn't wire
 *      labels via htmlFor/id (#76). Now that they do, RTL's
 *      `getByLabelText(/email/i)` is the preferred path. These helpers
 *      remain useful for tests that interact with the form by `name`
 *      attribute (e.g. when the test exercises an unlabeled hidden
 *      field), but new tests should reach for `getByLabelText` first.
 *
 * ## Defensive guards (#75 followup)
 *
 * Both helpers explicitly throw a helpful "did you forget to
 * destructure?" message when `container` is undefined — the exact
 * foot-gun #84 fixed in `dropzone.ts`. The callers in this repo all
 * capture `container` via a module-level `let container: HTMLElement`
 * that's reassigned per-`it` from `renderWithProviders(...)`. A
 * missing reassignment would otherwise surface as
 * `TypeError: Cannot read properties of undefined (reading
 * 'querySelector')` pointing at THIS file rather than the missing
 * destructure in the test. `submitFormIn` additionally rejects an
 * ambiguous multi-form container — the lift's stated rationale is to
 * defend against simultaneous forms, so silently submitting only the
 * first would re-introduce the very hazard the lift was meant to
 * eliminate.
 */
import { fireEvent } from "@testing-library/react";

/**
 * Set the value of `<input name="...">` inside `container` by firing
 * an `input` event. Throws on missing input — always a programming
 * error in test setup, not an expected branch.
 *
 * Prefer `screen.getByLabelText(...)` for newer tests; this helper is
 * the migration target for the pre-#76 form tests that grabbed inputs
 * via the `name` attribute. The migration of the existing auth-form
 * tests to `getByLabelText` is tracked in #132.
 */
export function fillInputByName(
  container: HTMLElement,
  name: string,
  value: string,
): void {
  if (!container) {
    throw new Error(
      "fillInputByName: container was undefined — did you forget " +
        "`({ container } = renderWithProviders(...))` in this test?",
    );
  }
  const input = container.querySelector(
    `input[name="${name}"]`,
  ) as HTMLInputElement | null;
  if (!input) {
    throw new Error(`fillInputByName: no input named "${name}" in container`);
  }
  fireEvent.input(input, { target: { value } });
}

/**
 * Fire a `submit` event on the `<form>` inside `container`. Throws on
 * missing form, ambiguous (multi-form) container, or undefined
 * container. Container-scoped so two simultaneous forms don't collide
 * on `document.querySelector("form")`.
 *
 * The multi-form guard is the load-bearing piece: `querySelector`
 * returns only the FIRST match, so a future composite test rendering
 * two forms inside one container would silently submit only one
 * without any signal to the test author. We throw an explicit error
 * instead — at which point the test must build a smaller container
 * around the form-under-test, or call `fireEvent.submit(specificForm)`
 * directly.
 */
export function submitFormIn(container: HTMLElement): void {
  if (!container) {
    throw new Error(
      "submitFormIn: container was undefined — did you forget " +
        "`({ container } = renderWithProviders(...))` in this test?",
    );
  }
  const forms = container.querySelectorAll("form");
  if (forms.length === 0) {
    throw new Error("submitFormIn: no <form> found inside container");
  }
  if (forms.length > 1) {
    throw new Error(
      `submitFormIn: ambiguous — ${forms.length} <form> elements in ` +
        "container. Narrow the container around the form under test, " +
        "or call `fireEvent.submit(specificForm)` directly.",
    );
  }
  fireEvent.submit(forms[0]);
}
