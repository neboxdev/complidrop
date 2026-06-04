/**
 * Status badges — color is never the sole signal (leading icon) AND the detail
 * page's old color collapse is fixed: Expired / ExpiringSoon / Pending each get
 * a distinct hue + icon, not one slate pill. (#189)
 */
import { describe, it, expect } from "vitest";
import { render, screen } from "@testing-library/react";
import { ComplianceBadge, ExtractionBadge } from "./StatusBadges";

describe("ComplianceBadge (#189)", () => {
  it("renders the humanized label with a leading icon (non-color signal)", () => {
    const { container } = render(<ComplianceBadge status="NonCompliant" />);
    expect(screen.getByText("Action needed")).toBeInTheDocument();
    // An svg icon precedes the text — survives grayscale.
    expect(container.querySelector("svg")).not.toBeNull();
  });

  it("gives Expired, ExpiringSoon, and Pending DISTINCT hues (no collapse)", () => {
    const hue = (status: string) => {
      const { container } = render(<ComplianceBadge status={status} />);
      return container.firstElementChild?.className ?? "";
    };
    const expired = hue("Expired");
    const expiring = hue("ExpiringSoon");
    const pending = hue("Pending");
    // Each picks a different color family — the bug was all three collapsing
    // to the same slate pill on the detail page.
    expect(expiring).toContain("amber");
    expect(pending).toContain("slate");
    expect(expired).toContain("rose");
    expect(expiring).not.toBe(pending);
  });

  it("forwards data-testid (used by the detail page)", () => {
    render(<ComplianceBadge status="Compliant" data-testid="compliance-status" />);
    expect(screen.getByTestId("compliance-status")).toHaveTextContent("Compliant");
  });
});

describe("ExtractionBadge (#189)", () => {
  it("renders the humanized label with a leading icon", () => {
    const { container } = render(<ExtractionBadge status="Processing" />);
    expect(screen.getByText("Reading…")).toBeInTheDocument();
    expect(container.querySelector("svg")).not.toBeNull();
  });
});
