/**
 * Pins the form-helper contracts added in #75 and hardened in the
 * #75 followup (defensive guards + multi-form rejection).
 *
 * The companion test mirrors the Wave 3 pattern established by
 * `sonner.test.ts`, `polling.test.ts`, `dropzone.test.ts`, and
 * `security.test.ts`: every shared test helper ships with a paired
 * `.test.ts` that pins the helper's public contract, so a future
 * "simplification" can't silently regress the foot-gun protections
 * downstream callers depend on.
 *
 * Six invariants matter for downstream tests:
 *   1. `fillInputByName` throws a HELPFUL error when container is
 *      undefined — mirrors `dropFilesIn`'s container guard. Names the
 *      actual root cause (missing destructure) rather than blaming
 *      `querySelector`.
 *   2. `fillInputByName` throws a HELPFUL error when no input with
 *      the requested name exists. Echoes the name for fast diagnosis.
 *   3. `fillInputByName` is container-scoped — an input in
 *      `document.body` outside the container is invisible to the
 *      helper. This is the load-bearing rationale for the lift.
 *   4. `submitFormIn` mirrors guard 1 (undefined container).
 *   5. `submitFormIn` throws when no `<form>` exists in the container.
 *   6. `submitFormIn` throws when MULTIPLE forms exist — silently
 *      submitting the first would re-introduce the same collision
 *      hazard the lift was meant to eliminate.
 *
 * The naming is `form-helpers.test.ts` (not `forms.test.ts`) because
 * `forms.test.tsx` is already taken by #76's label-wiring contract
 * test — a different concern with the same root noun.
 */
import { describe, it, expect, vi } from "vitest";
import { fillInputByName, submitFormIn } from "./forms";

describe("fillInputByName — container guard contract (#75 followup)", () => {
  it("throws with a helpful 'did you forget to destructure?' message when container is undefined", () => {
    expect(() =>
      fillInputByName(undefined as unknown as HTMLElement, "email", "x"),
    ).toThrow(/container was undefined/i);
    expect(() =>
      fillInputByName(undefined as unknown as HTMLElement, "email", "x"),
    ).toThrow(/renderWithProviders/);
  });

  it("throws with a helpful 'no input named' message when container has no matching input", () => {
    const container = document.createElement("div");
    expect(() => fillInputByName(container, "email", "x")).toThrow(
      /no input named "email"/i,
    );
  });

  it("is container-scoped — an input outside the container is invisible to the helper", () => {
    // Build a container with NO matching input, and inject a sibling
    // `<input name="email">` directly into document.body. The pre-lift
    // global `document.querySelector` would have picked up the body-
    // level input; the container-scoped helper must NOT.
    const container = document.createElement("div");
    document.body.appendChild(container);
    const sibling = document.createElement("input");
    sibling.name = "email";
    document.body.appendChild(sibling);

    try {
      expect(() => fillInputByName(container, "email", "x")).toThrow(
        /no input named "email"/i,
      );
    } finally {
      document.body.removeChild(container);
      document.body.removeChild(sibling);
    }
  });

  it("fires an input event with the supplied value when the input is found", () => {
    // Pin that the helper uses `fireEvent.input` with `{ target: { value }}`
    // rather than e.g. `change` — RHF's auth forms wire `onInput` mode
    // and would not see a `change` event, which is why the original
    // tests deliberately use `input`.
    const container = document.createElement("div");
    const input = document.createElement("input");
    input.name = "email";
    container.appendChild(input);
    document.body.appendChild(container);

    try {
      const onInput = vi.fn();
      input.addEventListener("input", onInput);
      fillInputByName(container, "email", "owner@acme.test");
      expect(input.value).toBe("owner@acme.test");
      expect(onInput).toHaveBeenCalledTimes(1);
    } finally {
      document.body.removeChild(container);
    }
  });
});

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
    // Same scope check as fillInputByName, applied to <form>. The
    // pre-lift global `document.querySelector("form")` would have
    // picked up the body-level form; the container-scoped helper must
    // NOT.
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
