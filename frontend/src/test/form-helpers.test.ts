/**
 * Pins the form-helper contract added in #75 and hardened in the #75
 * followup (defensive guards + multi-form rejection).
 *
 * Originally exercised `fillInputByName` too, but #132 deleted that
 * helper after every caller migrated to `screen.getByLabelText` — see
 * `forms.ts` for the rationale. `submitFormIn` stayed because the
 * multi-form guard is orthogonal to label-based input lookup.
 *
 * The companion test mirrors the Wave 3 pattern established by
 * `sonner.test.ts`, `polling.test.ts`, `dropzone.test.ts`, and
 * `security.test.ts`: every shared test helper ships with a paired
 * `.test.ts` that pins the helper's public contract, so a future
 * "simplification" can't silently regress the foot-gun protections
 * downstream callers depend on.
 *
 * Three invariants matter for downstream tests:
 *   1. `submitFormIn` throws a HELPFUL "did you forget to
 *      destructure?" message when container is undefined.
 *   2. `submitFormIn` throws when no `<form>` exists in the container.
 *   3. `submitFormIn` throws when MULTIPLE forms exist — silently
 *      submitting the first would re-introduce the same collision
 *      hazard the lift was meant to eliminate.
 *
 * The naming is `form-helpers.test.ts` (not `forms.test.ts`) because
 * `forms.test.tsx` is already taken by #76's label-wiring contract
 * test — a different concern with the same root noun.
 */
import { describe, it, expect, vi } from "vitest";
import { submitFormIn } from "./forms";

describe("submitFormIn — container + ambiguity guard contract (#75 followup)", () => {
  it("throws with a helpful 'did you forget to destructure?' message when container is undefined", () => {
    expect(() =>
      submitFormIn(undefined as unknown as HTMLElement),
    ).toThrow(/container was undefined/i);
    expect(() =>
      submitFormIn(undefined as unknown as HTMLElement),
    ).toThrow(/renderWithProviders/);
  });

  it("throws with a helpful 'no <form> found' message when container has no form", () => {
    const container = document.createElement("div");
    expect(() => submitFormIn(container)).toThrow(
      /no <form> found inside container/i,
    );
  });

  it("is container-scoped — a form outside the container is invisible to the helper", () => {
    // The whole point of moving from `document.querySelector("form")`
    // to `container.querySelector("form")` is to defend against a
    // future composite test rendering two forms. Same scope check as
    // dropzone.ts: a body-level form must NOT be picked up by a
    // helper scoped to a sibling container.
    const container = document.createElement("div");
    document.body.appendChild(container);
    const sibling = document.createElement("form");
    document.body.appendChild(sibling);

    try {
      expect(() => submitFormIn(container)).toThrow(
        /no <form> found inside container/i,
      );
    } finally {
      document.body.removeChild(container);
      document.body.removeChild(sibling);
    }
  });

  it("throws when the container has multiple <form> elements (the collision the lift was meant to defend against)", () => {
    // The whole point of moving from `document.querySelector` to
    // `container.querySelector` is to defend against a future
    // composite test rendering two forms. But `querySelector` itself
    // only returns the first match — so without an explicit multi-
    // form guard, the helper would silently submit only one of the
    // two and the test author would have no signal. Pin the throw.
    const container = document.createElement("div");
    container.appendChild(document.createElement("form"));
    container.appendChild(document.createElement("form"));

    expect(() => submitFormIn(container)).toThrow(/ambiguous/i);
    expect(() => submitFormIn(container)).toThrow(/2 <form> elements/);
  });

  it("fires a submit event on the form when exactly one is present", () => {
    const container = document.createElement("div");
    const form = document.createElement("form");
    container.appendChild(form);
    document.body.appendChild(container);

    try {
      const onSubmit = vi.fn((e: Event) => e.preventDefault());
      form.addEventListener("submit", onSubmit);
      submitFormIn(container);
      expect(onSubmit).toHaveBeenCalledTimes(1);
    } finally {
      document.body.removeChild(container);
    }
  });
});
