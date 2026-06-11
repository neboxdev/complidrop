/**
 * Export page — tier-3 smoke (#36).
 *
 * The page is pure local-state + manual fetch (no TanStack Query); it
 * doesn't fan out to any /api/* endpoint until a button is clicked.
 * Smoke test: render-without-crash + the two from/to date inputs.
 */
import { describe, it, expect } from "vitest";
import { screen, fireEvent } from "@testing-library/react";
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

    // Scope clarification (#197): the date range bounds the activity log only,
    // not the always-complete documents table. ("activity log" sits in a <strong>,
    // so match the phrase that lives in a single text node.)
    expect(
      screen.getByText(/the documents table always lists all of your active documents/i),
    ).toBeInTheDocument();
  });

  it("blocks an inverted date range before any request (#262)", () => {
    // Catch the inversion client-side for instant feedback (inline message +
    // disabled button) — no round-trip needed, even though api.getBlob (#254)
    // now surfaces the API's friendly 400 message on the toast.
    renderWithProviders(<ExportPage />, { auth: authedMe });
    const [fromInput, toInput] = Array.from(document.querySelectorAll('input[type="date"]'));

    fireEvent.change(fromInput, { target: { value: "2026-06-10" } });
    fireEvent.change(toInput, { target: { value: "2026-05-11" } });

    expect(screen.getByRole("alert")).toHaveTextContent(/start date must be on or before the end date/i);
    expect(screen.getByRole("button", { name: /download pdf/i })).toBeDisabled();

    // Fixing the range clears the guard.
    fireEvent.change(toInput, { target: { value: "2026-06-11" } });
    expect(screen.queryByRole("alert")).toBeNull();
    expect(screen.getByRole("button", { name: /download pdf/i })).toBeEnabled();
  });
});
