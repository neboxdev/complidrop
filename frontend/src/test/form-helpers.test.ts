/**
 * Pins the form-helper contracts:
 *
 * - `submitFormIn` (#75 + #75 followup): defensive guards + multi-form
 *   rejection. Originally tested `fillInputByName` alongside it; #132
 *   deleted that helper after every caller migrated to
 *   `screen.getByLabelText`.
 * - `fillByLabel` (#135): the lifted label-based input driver — every
 *   auth-form test used to duplicate this one-line shim around
 *   `screen.getByLabelText` + `fireEvent.input`.
 *
 * The companion test mirrors the Wave 3 pattern established by
 * `sonner.test.ts`, `polling.test.ts`, `dropzone.test.ts`, and
 * `security.test.ts`: every shared test helper ships with a paired
 * `.test.ts` that pins the helper's public contract, so a future
 * "simplification" can't silently regress the foot-gun protections
 * downstream callers depend on.
 *
 * Invariants:
 *   1. `fillByLabel` resolves an input by label and fires the DOM
 *      `input` event — the event React synthesizes `onChange` from
 *      and RHF's `register(...)` subscribes to.
 *   2. `fillByLabel` does NOT fire the DOM `change` event — pins the
 *      `fireEvent.input` primitive choice so a refactor to
 *      `fireEvent.change` (or a hand-rolled dispatchEvent) is caught.
 *   3. `fillByLabel` accepts a plain string label too.
 *   4. `fillByLabel` propagates RTL's "unable to find a label" error
 *      when the label is missing — no swallowed signal.
 *   5. `fillByLabel(label, value, container)` scopes to that container
 *      so a future composite test rendering two forms with identical
 *      labels can disambiguate. (#134's shape.)
 *   6. `submitFormIn` throws a HELPFUL "did you forget to
 *      destructure?" message when container is undefined.
 *   7. `submitFormIn` throws when no `<form>` exists in the container.
 *   8. `submitFormIn` throws when MULTIPLE forms exist — silently
 *      submitting the first would re-introduce the same collision
 *      hazard the lift was meant to eliminate.
 *
 * The naming is `form-helpers.test.ts` (not `forms.test.ts`) because
 * `forms.test.tsx` is already taken by #76's label-wiring contract
 * test — a different concern with the same root noun.
 */
import { afterEach, describe, it, expect, vi } from "vitest";
import { fillByLabel, submitFormIn } from "./forms";

// `screen.getByLabelText` walks the global `document` — each fillByLabel
// test mounts a minimal labeled-input DOM into `document.body` directly
// (createElement, not JSX) so this stays a `.ts` file consistent with
// the other helper contracts (`dropzone.test.ts`, `polling.test.ts`).
function mountLabeledInput(opts: {
  labelText: string;
  inputId: string;
  inputName?: string;
}): { input: HTMLInputElement; cleanup: () => void } {
  const form = document.createElement("form");
  const label = document.createElement("label");
  label.setAttribute("for", opts.inputId);
  label.textContent = opts.labelText;
  const input = document.createElement("input");
  input.id = opts.inputId;
  if (opts.inputName) input.name = opts.inputName;
  form.appendChild(label);
  form.appendChild(input);
  document.body.appendChild(form);
  return {
    input,
    cleanup: () => {
      document.body.removeChild(form);
    },
  };
}

describe("fillByLabel — label-based input driver contract (#135)", () => {
  let cleanups: Array<() => void> = [];

  afterEach(() => {
    for (const c of cleanups) c();
    cleanups = [];
  });

  it("resolves an input by label text (regex) and fires the DOM `input` event (NOT `change`)", () => {
    // Pins both halves of the primitive choice: `fireEvent.input` MUST
    // fire `input`, MUST NOT fire `change`. A refactor to
    // `fireEvent.change` (which fires both) would still pass an
    // input-only assertion via RTL's cross-dispatching; the negative
    // `change` assertion is what catches the regression cleanly.
    const { input, cleanup } = mountLabeledInput({
      labelText: "Email",
      inputId: "f-email",
      inputName: "email",
    });
    cleanups.push(cleanup);

    const onInput = vi.fn();
    const onChange = vi.fn();
    input.addEventListener("input", onInput);
    input.addEventListener("change", onChange);

    fillByLabel(/^email$/i, "owner@acme.test");

    expect(input.value).toBe("owner@acme.test");
    expect(onInput).toHaveBeenCalledTimes(1);
    expect(onChange).not.toHaveBeenCalled();
  });

  it("accepts a plain string label too (passes through to getByLabelText)", () => {
    const { input, cleanup } = mountLabeledInput({
      labelText: "Full name",
      inputId: "f-fullname",
    });
    cleanups.push(cleanup);

    fillByLabel("Full name", "Owner Name");

    expect(input.value).toBe("Owner Name");
  });

  it("propagates RTL's 'unable to find a label' error when the label is missing", () => {
    const { cleanup } = mountLabeledInput({
      labelText: "Email",
      inputId: "f-email",
    });
    cleanups.push(cleanup);

    expect(() => fillByLabel(/nonexistent/i, "x")).toThrow(
      /unable to find a label/i,
    );
  });

  it("scopes to the supplied container — sibling form's matching label is invisible", () => {
    // Pins the #134 escape hatch: when two forms in one document share
    // a label (e.g. composite "vendor invite + portal token" test),
    // passing the per-form container scopes the lookup so neither
    // form picks up the other's input. Build two forms, drive only
    // one via container-scoped fillByLabel, assert the other untouched.
    const containerA = document.createElement("div");
    const formA = document.createElement("form");
    const labelA = document.createElement("label");
    labelA.setAttribute("for", "form-a-email");
    labelA.textContent = "Email";
    const inputA = document.createElement("input");
    inputA.id = "form-a-email";
    formA.appendChild(labelA);
    formA.appendChild(inputA);
    containerA.appendChild(formA);

    const containerB = document.createElement("div");
    const formB = document.createElement("form");
    const labelB = document.createElement("label");
    labelB.setAttribute("for", "form-b-email");
    labelB.textContent = "Email";
    const inputB = document.createElement("input");
    inputB.id = "form-b-email";
    formB.appendChild(labelB);
    formB.appendChild(inputB);
    containerB.appendChild(formB);

    document.body.appendChild(containerA);
    document.body.appendChild(containerB);
    cleanups.push(() => {
      document.body.removeChild(containerA);
      document.body.removeChild(containerB);
    });

    fillByLabel(/^email$/i, "a@example.test", containerA);
    expect(inputA.value).toBe("a@example.test");
    expect(inputB.value).toBe("");
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
