/**
 * Export page — tier-3 smoke (#36).
 *
 * The page is pure local-state + manual fetch (no TanStack Query); it
 * doesn't fan out to any /api/* endpoint until a button is clicked.
 * Smoke test: render-without-crash + the two from/to date inputs.
 */
import { describe, it, expect, vi } from "vitest";
import { screen } from "@testing-library/react";
import ExportPage from "./page";
import { renderWithProviders, authedMe } from "@/test";

vi.mock("sonner", () => ({
  toast: { success: vi.fn(), error: vi.fn() },
  Toaster: () => null,
}));

describe("ExportPage — smoke (#36)", () => {
  it("renders without crashing and exposes a from/to date pair", () => {
    renderWithProviders(<ExportPage />, { auth: authedMe });

    expect(
      screen.getByRole("heading", { name: /^export$/i }),
    ).toBeInTheDocument();
    // Two date inputs (from + to).
    const dateInputs = document.querySelectorAll('input[type="date"]');
    expect(dateInputs.length).toBeGreaterThanOrEqual(2);
  });
});
