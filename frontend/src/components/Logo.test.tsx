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

    it("renders decoratively (no role, aria-hidden) when decorative=true", () => {
      // Used inside a <Link aria-label="..."> so the wordmark isn't double-announced.
      const { container } = render(<Logo variant="primary" decorative />);
      expect(screen.queryByRole("img")).toBeNull();
      const wrapper = container.firstElementChild;
      expect(wrapper?.getAttribute("aria-hidden")).toBe("true");
      expect(wrapper?.hasAttribute("aria-label")).toBe(false);
    });

    it("renders the mark variant decoratively when decorative=true", () => {
      const { container } = render(<Logo variant="mark" decorative />);
      expect(screen.queryByRole("img")).toBeNull();
      expect(container.firstElementChild?.getAttribute("aria-hidden")).toBe("true");
    });

    it("inner droplet SVG is always aria-hidden so the wrapper label isn't double-announced", () => {
      // The outer span carries role="img" + aria-label="CompliDrop"; the inner
      // SVG must remain aria-hidden so screen readers treat the lockup as one
      // labelled image. A regression that drops aria-hidden from <Mark /> would
      // not be caught by the wrapper-level tests above.
      for (const variant of ["primary", "twotone", "reverse", "mark"] as const) {
        const { container, unmount } = render(<Logo variant={variant} />);
        const svg = container.querySelector("svg");
        expect(svg?.getAttribute("aria-hidden"), `variant=${variant}`).toBe("true");
        unmount();
      }
    });

    it("accepts a custom accessible name", () => {
      render(<Logo variant="primary" title="CompliDrop — home" />);
      expect(screen.getByRole("img", { name: "CompliDrop — home" })).toBeTruthy();
    });
  });

  describe("variants", () => {
    it("primary renders the full CompliDrop wordmark in one span (navy)", () => {
      const { container } = render(<Logo variant="primary" />);
      const wordmark = Array.from(container.querySelectorAll("span > span")).find(
        (el) => el.textContent === "CompliDrop",
      );
      expect(wordmark).toBeTruthy();
      expect((wordmark as HTMLElement).style.color).toBe("rgb(12, 74, 110)"); // navy #0C4A6E
    });

    it("twotone splits the wordmark into Compli (navy) + Drop (sky)", () => {
      const { container } = render(<Logo variant="twotone" />);
      const dropSpan = Array.from(container.querySelectorAll("span")).find(
        (el) => el.textContent === "Drop" && el.children.length === 0,
      );
      expect(dropSpan).toBeTruthy();
      expect((dropSpan as HTMLElement).style.color).toBe("rgb(14, 165, 233)"); // sky #0EA5E9
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

    it("scales the wordmark font-size to ~0.81x the height (icon-dominant lockup)", () => {
      // Matches the canonical SVG ratio of 52/64 from
      // `docs/brand/logo-refresh-2026/svg/complidrop-logo-horizontal.svg`.
      // The icon stays at full `height`; the wordmark cap-height ends up
      // ~58 % of the icon height, so the icon visually dominates the lockup.
      const { container } = render(<Logo variant="primary" height={40} />);
      const wordmark = Array.from(container.querySelectorAll("span > span")).find(
        (el) => el.textContent === "CompliDrop",
      ) as HTMLElement | undefined;
      expect(wordmark?.style.fontSize).toBe("32px"); // round(40 * 0.81)
    });

    it("uses the height as size for the mark variant", () => {
      const { container } = render(<Logo variant="mark" height={64} />);
      const svg = container.querySelector("svg");
      expect(svg?.getAttribute("width")).toBe("64");
      expect(svg?.getAttribute("height")).toBe("64");
    });

    it("falls back to the default size when height is NaN / 0 / negative", () => {
      // Invalid heights would otherwise emit `width="NaN"`, `fontSize: "NaNpx"`,
      // etc. The guard normalises them to the 36-px default.
      for (const bad of [Number.NaN, 0, -10, Number.POSITIVE_INFINITY]) {
        const { container, unmount } = render(
          <Logo variant="primary" height={bad as number} />,
        );
        const svg = container.querySelector("svg");
        expect(svg?.getAttribute("width"), `height=${bad}`).toBe("36");
        unmount();
      }
    });
  });

  describe("prop forwarding", () => {
    it("forwards className to the outer wrapper span (lockup variants)", () => {
      const { container } = render(<Logo variant="primary" className="custom-class" />);
      expect(container.firstElementChild?.className).toContain("custom-class");
    });

    it("forwards className to the outer wrapper span (mark variant)", () => {
      const { container } = render(<Logo variant="mark" className="mark-class" />);
      expect(container.firstElementChild?.className).toContain("mark-class");
    });
  });

  describe("brand rules", () => {
    it("contains no orange (#F97316) anywhere in the rendered markup", () => {
      // Render every variant and assert the orange UI-accent color never leaks
      // into the logo's inline styles or fills.
      for (const variant of ["primary", "twotone", "reverse", "mark"] as const) {
        const { container, unmount } = render(<Logo variant={variant} />);
        const html = container.innerHTML.toLowerCase();
        expect(html, `variant=${variant}`).not.toContain("#f97316");
        expect(html, `variant=${variant}`).not.toContain("rgb(249, 115, 22)");
        unmount();
      }
    });

    it("droplet always uses sky #0EA5E9 — across every variant", () => {
      // The droplet color is invariant across all four variants per the
      // design handoff. A regression that flipped the droplet on `reverse`
      // (e.g. to navy) would only be caught here.
      for (const variant of ["primary", "twotone", "reverse", "mark"] as const) {
        const { container, unmount } = render(<Logo variant={variant} />);
        const droplet = container.querySelector("svg path[fill]");
        expect(droplet?.getAttribute("fill"), `variant=${variant}`).toBe("#0EA5E9");
        unmount();
      }
    });
  });
});
