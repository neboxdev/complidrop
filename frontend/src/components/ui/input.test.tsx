/**
 * Input touch target (#181) — pins that the Input carries a ≥44px coarse-pointer
 * min-height (a listed deliverable: "button.tsx/input.tsx h-8/h-7"). JSDOM can't
 * evaluate the media query, so we assert the class is present on the control.
 */
import { describe, it, expect } from "vitest";
import { render, screen } from "@testing-library/react";
import { Input } from "./input";

describe("Input — touch target (#181)", () => {
  it("renders with a coarse-pointer min-height (≥44px on touch)", () => {
    render(<Input aria-label="probe" />);
    const input = screen.getByRole("textbox", { name: "probe" });
    expect(input.className).toContain("pointer-coarse:min-h-11");
  });
});
