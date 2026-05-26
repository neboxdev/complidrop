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
 */
import { fireEvent } from "@testing-library/react";

/**
 * Set the value of `<input name="...">` inside `container` by firing
 * an `input` event. Throws on missing input — always a programming
 * error in test setup, not an expected branch.
 *
 * Prefer `screen.getByLabelText(...)` for newer tests; this helper is
 * the migration target for the pre-#76 form tests that grabbed inputs
 * via the `name` attribute.
 */
export function fillInputByName(
  container: HTMLElement,
  name: string,
  value: string,
): void {
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
 * missing form. Container-scoped so two simultaneous forms don't
 * collide on `document.querySelector("form")`.
 */
export function submitFormIn(container: HTMLElement): void {
  const form = container.querySelector("form");
  if (!form) {
    throw new Error("submitFormIn: no <form> found inside container");
  }
  fireEvent.submit(form);
}
