/**
 * Integration-level proof for the form-helper container-scoping
 * rationale (#134).
 *
 * `form-helpers.test.ts` pins the unit-level invariant: when given a
 * hand-built DOM with a sibling input/form OUTSIDE the container, the
 * helpers ignore it. That's necessary but not sufficient. The whole
 * rationale for moving from `document.querySelector` to
 * `container.querySelector` was "defending against a future composite
 * test rendering two forms" — until that composite test actually
 * exists, the migration is comment-justified, not test-justified. A
 * regression that re-introduced `document.querySelector` in either
 * helper would still pass every unit test.
 *
 * This file fills that gap by rendering REAL component trees twice in
 * one `it()` block and proving the helpers stay scoped.
 *
 * ## Why two `renderWithProviders` calls in one test is safe
 *
 * RTL's `render()` creates a fresh `<div>` container per call and
 * appends it to `document.body`. The harness's `renderWithProviders`
 * additionally creates a fresh `QueryClient` per call (see
 * `createTestQueryClient` in `src/test/render.tsx`), so the two trees
 * NEVER share React-Query state. `vitest.setup.ts`'s afterEach calls
 * RTL's `cleanup()` (unmounts every tracked render + removes every
 * container) and `resetNavigation()`, so neither container nor any
 * cache entry leaks into the next test. The collision the helpers
 * defend against is intra-test (two forms LIVE in the same document
 * at the same moment) — exactly the scenario this file exercises.
 *
 * Composing two `<LoginPage />` instances (rather than mixing LoginPage
 * and RegisterForm) deliberately picks the strongest disambiguation
 * pressure: both trees emit the SAME accessible label text ("Email",
 * "Password"), so a global `screen.getByLabelText(/^email$/i)` would
 * resolve to two elements and throw "Found multiple elements" — pinning
 * that the container-scoped overload is the only correct shape for the
 * composite case.
 */
import { describe, it, expect } from "vitest";
import { screen } from "@testing-library/react";
import LoginPage from "@/app/(auth)/login/page";
import { fillByLabel, renderWithProviders, submitFormIn } from "@/test";

describe("composite two-form: container-scoped helpers (#134)", () => {
  it("fillByLabel scopes per-container — two LoginPage trees fill independently", () => {
    const a = renderWithProviders(<LoginPage />, { auth: null });
    const b = renderWithProviders(<LoginPage />, { auth: null });

    // Sanity check: a global lookup MUST fail with "multiple matches"
    // — this pins that the test setup actually creates the collision
    // the container-scope is meant to disambiguate. If a future RTL
    // upgrade or React useId change made the labels globally unique
    // somehow, this assertion would surface the silent setup change
    // rather than this test passing for the wrong reason.
    expect(() => screen.getByLabelText(/^email$/i)).toThrow(
      /found multiple elements/i,
    );

    // Now fill each via the container-scoped overload — neither
    // resolution leaks into the other tree.
    fillByLabel(/^email$/i, "a@one.test", a.container);
    fillByLabel(/^password$/i, "alpha-pass-1", a.container);
    fillByLabel(/^email$/i, "b@two.test", b.container);
    fillByLabel(/^password$/i, "bravo-pass-2", b.container);

    const aEmail = a.container.querySelector(
      'input[type="email"]',
    ) as HTMLInputElement;
    const aPass = a.container.querySelector(
      'input[type="password"]',
    ) as HTMLInputElement;
    const bEmail = b.container.querySelector(
      'input[type="email"]',
    ) as HTMLInputElement;
    const bPass = b.container.querySelector(
      'input[type="password"]',
    ) as HTMLInputElement;

    expect(aEmail.value).toBe("a@one.test");
    expect(aPass.value).toBe("alpha-pass-1");
    expect(bEmail.value).toBe("b@two.test");
    expect(bPass.value).toBe("bravo-pass-2");
  });

  it("submitFormIn scopes per-container — each form submits without ambiguity", () => {
    // Each container holds exactly ONE <form>; the multi-form guard
    // (`forms.length > 1` throw) only trips when both forms live in
    // ONE container. With separate containers, both submit cleanly.
    const a = renderWithProviders(<LoginPage />, { auth: null });
    const b = renderWithProviders(<LoginPage />, { auth: null });

    const formA = a.container.querySelector("form") as HTMLFormElement;
    const formB = b.container.querySelector("form") as HTMLFormElement;

    let aSubmitted = 0;
    let bSubmitted = 0;
    formA.addEventListener("submit", (e) => {
      e.preventDefault();
      aSubmitted++;
    });
    formB.addEventListener("submit", (e) => {
      e.preventDefault();
      bSubmitted++;
    });

    submitFormIn(a.container);
    submitFormIn(b.container);

    expect(aSubmitted).toBe(1);
    expect(bSubmitted).toBe(1);
  });

  it("submitFormIn rejects a container holding BOTH forms (the original collision)", () => {
    // Build the inverse of the previous test: stuff both rendered
    // trees into the SAME parent container. This is the literal
    // hazard the lift from `document.querySelector` to
    // `container.querySelector` PLUS the multi-form ambiguity guard
    // exist to catch — `querySelectorAll("form").length === 2`
    // throws by design.
    const wrapper = document.createElement("div");
    document.body.appendChild(wrapper);
    try {
      const a = renderWithProviders(<LoginPage />, {
        auth: null,
        container: wrapper.appendChild(document.createElement("div")),
      });
      const b = renderWithProviders(<LoginPage />, {
        auth: null,
        container: wrapper.appendChild(document.createElement("div")),
      });

      // Sanity: each per-render container still has exactly one form,
      // so individual submitFormIn calls work on the per-render
      // container — only the combined wrapper trips the guard.
      expect(a.container.querySelectorAll("form").length).toBe(1);
      expect(b.container.querySelectorAll("form").length).toBe(1);
      expect(wrapper.querySelectorAll("form").length).toBe(2);

      expect(() => submitFormIn(wrapper)).toThrow(/ambiguous/i);
      expect(() => submitFormIn(wrapper)).toThrow(/2 <form> elements/);
    } finally {
      document.body.removeChild(wrapper);
    }
  });
});
