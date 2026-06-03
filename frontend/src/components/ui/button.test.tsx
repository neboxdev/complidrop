/**
 * Button touch targets (#181) — pins the AC "destructive icon buttons present a
 * ≥44px hit area on touch". The fix lives in the Button base classes (so EVERY
 * button — including the icon-only delete/revoke controls — inherits it), gated
 * to coarse pointers so the dense mouse layout is unchanged. JSDOM can't
 * evaluate the media query, so we assert the class is present on the rendered
 * control / variant output.
 */
import { describe, it, expect } from "vitest";
import { render, screen } from "@testing-library/react";
import { Button, buttonVariants } from "./button";

describe("Button — touch targets (#181)", () => {
  it("the base classes carry a ≥44px coarse-pointer hit area", () => {
    const cls = buttonVariants();
    expect(cls).toContain("pointer-coarse:min-h-11");
    expect(cls).toContain("pointer-coarse:min-w-11");
  });

  it("an icon button renders with the coarse-pointer min-size classes", () => {
    render(
      <Button size="icon" aria-label="probe">
        x
      </Button>,
    );
    const btn = screen.getByRole("button", { name: "probe" });
    expect(btn.className).toContain("pointer-coarse:min-h-11");
    expect(btn.className).toContain("pointer-coarse:min-w-11");
  });
});
