import { describe, it, expect } from "vitest";
import { render, screen } from "@testing-library/react";
import { Logo } from "./Logo";

describe("Logo", () => {
  describe("accessibility", () => {
    it("exposes a CompliDrop label on lockup variants by default", () => {
      render(<Logo variant="primary" />);
      expect(screen.getByRole("img", { name: "CompliDrop" })).toBeTruthy();
    });

    it("exposes a CompliDrop label on the mark variant by default", () => {
      render(<Logo variant="mark" />);
      expect(screen.getByRole("img", { name: "CompliDrop" })).toBeTruthy();
    });

    it("renders decoratively (no role, aria-hidden) when title is empty", () => {
      // Used inside a <Link aria-label="..."> so the wordmark isn't double-announced.
      const { container } = render(<Logo variant="primary" title="" />);
      expect(screen.queryByRole("img")).toBeNull();
      const wrapper = container.firstElementChild;
      expect(wrapper?.getAttribute("aria-hidden")).toBe("true");
      expect(wrapper?.hasAttribute("aria-label")).toBe(false);
    });

    it("renders the mark variant decoratively when title is empty", () => {
      const { container } = render(<Logo variant="mark" title="" />);
      expect(screen.queryByRole("img")).toBeNull();
      expect(container.firstElementChild?.getAttribute("aria-hidden")).toBe("true");
    });

    it("accepts a custom accessible name", () => {
      render(<Logo variant="primary" title="CompliDrop — home" />);
      expect(screen.getByRole("img", { name: "CompliDrop — home" })).toBeTruthy();
    });
  });

  describe("variants", () => {
    it("primary renders the full CompliDrop wordmark in one span (navy)", () => {
      const { container } = render(<Logo variant="primary" />);
      const wordmarkSpan = container.querySelectorAll("span > span");
      // Outermost lockup span → mark <svg> + wordmark <span> → wordmark contains
      // a single text node "CompliDrop" (not split for primary).
      const wordmark = Array.from(wordmarkSpan).find(
        (el) => el.textContent === "CompliDrop",
      );
      expect(wordmark).toBeTruthy();
      expect((wordmark as HTMLElement).style.color).toBe("rgb(12, 74, 110)"); // navy #0C4A6E
    });

    it("twotone splits the wordmark into Compli (navy) + Drop (sky)", () => {
      const { container } = render(<Logo variant="twotone" />);
      // The outer wordmark span has navy color and contains an inner span with sky color
      // wrapping "Drop". The literal "Compli" text sits in the outer span as a sibling.
      const dropSpan = Array.from(container.querySelectorAll("span")).find(
        (el) => el.textContent === "Drop" && el.children.length === 0,
      );
      expect(dropSpan).toBeTruthy();
      expect((dropSpan as HTMLElement).style.color).toBe("rgb(14, 165, 233)"); // sky #0EA5E9
      // The full wordmark still reads "CompliDrop"
      expect(container.textContent).toContain("CompliDrop");
    });

    it("reverse renders the full CompliDrop wordmark in white", () => {
      const { container } = render(<Logo variant="reverse" />);
      const wordmark = Array.from(container.querySelectorAll("span > span")).find(
        (el) => el.textContent === "CompliDrop",
      );
      expect(wordmark).toBeTruthy();
      expect((wordmark as HTMLElement).style.color).toBe("rgb(255, 255, 255)"); // white
    });

    it("mark renders only the SVG droplet (no wordmark text)", () => {
      const { container } = render(<Logo variant="mark" />);
      expect(container.querySelectorAll("svg").length).toBe(1);
      // No span containing the wordmark text
      expect(container.textContent).toBe("");
    });
  });

  describe("sizing", () => {
    it("renders the SVG at the height prop", () => {
      const { container } = render(<Logo variant="primary" height={48} />);
      const svg = container.querySelector("svg");
      expect(svg?.getAttribute("width")).toBe("48");
      expect(svg?.getAttribute("height")).toBe("48");
    });

    it("defaults to height=36", () => {
      const { container } = render(<Logo variant="primary" />);
      const svg = container.querySelector("svg");
      expect(svg?.getAttribute("width")).toBe("36");
    });

    it("scales the wordmark font-size to 1.3x the height", () => {
      const { container } = render(<Logo variant="primary" height={40} />);
      const wordmark = Array.from(container.querySelectorAll("span > span")).find(
        (el) => el.textContent === "CompliDrop",
      ) as HTMLElement | undefined;
      expect(wordmark?.style.fontSize).toBe("52px"); // 40 * 1.3
    });

    it("uses the height as size for the mark variant", () => {
      const { container } = render(<Logo variant="mark" height={64} />);
      const svg = container.querySelector("svg");
      expect(svg?.getAttribute("width")).toBe("64");
      expect(svg?.getAttribute("height")).toBe("64");
    });
  });

  describe("brand rules", () => {
    it("contains no orange (#F97316) anywhere in the rendered markup", () => {
      // Render every variant and assert the orange UI-accent color never leaks
      // into the logo's inline styles or fills.
      const variants = ["primary", "twotone", "reverse", "mark"] as const;
      for (const variant of variants) {
        const { container } = render(<Logo variant={variant} />);
        const html = container.innerHTML.toLowerCase();
        expect(html).not.toContain("#f97316");
        expect(html).not.toContain("rgb(249, 115, 22)");
        container.remove();
      }
    });

    it("droplet uses sky #0EA5E9", () => {
      const { container } = render(<Logo variant="primary" />);
      const droplet = container.querySelector("svg path[fill]");
      expect(droplet?.getAttribute("fill")).toBe("#0EA5E9");
    });
  });
});
