/**
 * Form-test helpers — fillByLabel + submitFormIn.
 *
 * The label-based input helper (`fillByLabel`) is the canonical
 * input-driving shape after #76 wired every `<label htmlFor=…>` ↔
 * `<input id=…>` and #131 added the lint rule that enforces it. Each
 * auth form test used to define its own one-line `fillField` shim
 * around `screen.getByLabelText`; #135 lifted it here so a fourth
 * form-test file can pick up the same idiom without copy-paste.
 *
 * `submitFormIn` stays orthogonal — its multi-form guard defends
 * against a future composite test (#134) rendering two forms in the
 * same container, which no RTL query handles for us.
 *
 * ## Defensive guards
 *
 * - `fillByLabel` delegates to RTL's `screen.getByLabelText`, which
 *   throws a useful "unable to find a label" error when the label is
 *   missing — no custom guard needed.
 * - `submitFormIn` throws helpful "did you forget to destructure?"
 *   when container is undefined (mirrors the `dropFilesIn` guard from
 *   #84), throws on missing form, and REJECTS ambiguous multi-form
 *   containers. #75's stated rationale was to defend against
 *   simultaneous forms, so silently submitting only the first would
 *   re-introduce the very hazard the lift was meant to eliminate.
 */
import { fireEvent, screen } from "@testing-library/react";

/**
 * Fill the input associated with the given accessible label by firing
 * an `input` event. Throws via RTL's own `getByLabelText` if no
 * matching label exists.
 *
 * RHF's auth forms wire `onInput` mode and would NOT see a `change`
 * event — `fireEvent.input` is deliberate.
 *
 * Resolves globally via `screen` (not container-scoped). Today every
 * auth test renders one form, so the global lookup is unambiguous. If
 * a future composite test renders two forms in the same document and
 * both expose the same label, scope the lookup with
 * `within(container).getByLabelText(...)` at the call site instead of
 * importing this helper. (#134 will introduce that pattern.)
 */
export function fillByLabel(
  label: RegExp | string,
  value: string,
): void {
  const input = screen.getByLabelText(label);
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
