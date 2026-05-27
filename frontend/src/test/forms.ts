/**
 * Form-test helper — container-scoped submitFormIn.
 *
 * Originally exported `fillInputByName` too, but #132 migrated every
 * caller to `screen.getByLabelText(/.../i)` after #76 wired the
 * htmlFor/id pairs that unblocked label-based lookups. The
 * label-based query exercises the same a11y wiring screen readers
 * depend on, so the by-`name` shortcut was dropped along with its
 * contract tests. `submitFormIn` stays because the multi-form guard
 * (below) is orthogonal — it defends against an entirely different
 * hazard that no RTL query handles for us.
 *
 * ## Defensive guards (#75 followup)
 *
 * `submitFormIn` explicitly throws a helpful "did you forget to
 * destructure?" message when `container` is undefined — the exact
 * foot-gun #84 fixed in `dropzone.ts`. The callers in this repo all
 * capture `container` via a module-level `let container: HTMLElement`
 * that's reassigned per-`it` from `renderWithProviders(...)`. A
 * missing reassignment would otherwise surface as
 * `TypeError: Cannot read properties of undefined (reading
 * 'querySelector')` pointing at THIS file rather than the missing
 * destructure in the test.
 *
 * The helper additionally rejects an ambiguous multi-form container —
 * #75's stated rationale was to defend against simultaneous forms, so
 * silently submitting only the first would re-introduce the very
 * hazard the lift was meant to eliminate.
 */
import { fireEvent } from "@testing-library/react";

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
