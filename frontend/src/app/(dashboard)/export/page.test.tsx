/**
 * Export page — tier-3 smoke (#36).
 *
 * The page is pure local-state + manual fetch (no TanStack Query); it
 * doesn't fan out to any /api/* endpoint until a button is clicked.
 * Smoke test: render-without-crash + the two from/to date inputs.
 */
import { describe, it, expect } from "vitest";
import { screen } from "@testing-library/react";
import ExportPage from "./page";
import { renderWithProviders, authedMe } from "@/test";

// sonner is mocked by the harness (vitest.setup.ts + src/test/sonner.ts). See #74.

describe("ExportPage — smoke (#36)", () => {
  it("renders without crashing and exposes a from/to date pair", () => {
    renderWithProviders(<ExportPage />, { auth: authedMe });

    expect(
      screen.getByRole("heading", { name: /^export$/i }),
    ).toBeInTheDocument();
    // Two date inputs (from + to).
    const dateInputs = document.querySelectorAll('input[type="date"]');
    expect(dateInputs.length).toBeGreaterThanOrEqual(2);

    // Responsive (#181): the from/to date pair stacks on a phone (grid-cols-1)
    // and only sits side-by-side at sm. Class-presence proxy (no CSS in JSDOM).
    const dateGrid = document.querySelector(".sm\\:grid-cols-2");
    expect(dateGrid).not.toBeNull();
    expect(dateGrid?.className).toContain("grid-cols-1");
  });
});
