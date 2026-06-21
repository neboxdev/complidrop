/**
 * Export page — smoke (#36) + the zero-documents teaching empty state (#239 delta 5).
 *
 * The download cards are pure local-state + manual fetch; the page now also reads
 * /api/dashboard/stats to decide whether to teach (no documents yet) or show the
 * download cards.
 */
import { describe, it, expect } from "vitest";
import { http } from "msw";
import { screen, fireEvent, waitFor } from "@testing-library/react";
import ExportPage from "./page";
import { renderWithProviders, server, url, jsonOk, authedMe } from "@/test";

// sonner is mocked by the harness (vitest.setup.ts + src/test/sonner.ts). See #74.

function stats(totalDocuments: number) {
  return http.get(url("/api/dashboard/stats"), () => jsonOk({ totalDocuments }));
}

describe("ExportPage — smoke (#36)", () => {
  it("renders without crashing and exposes a from/to date pair", async () => {
    server.use(stats(3)); // has documents → the download cards show
    renderWithProviders(<ExportPage />, { auth: authedMe });

    expect(await screen.findByRole("heading", { name: /pdf audit report/i })).toBeInTheDocument();
    // Two date inputs (from + to).
    const dateInputs = document.querySelectorAll('input[type="date"]');
    expect(dateInputs.length).toBeGreaterThanOrEqual(2);

    // Responsive (#181): the from/to date pair stacks on a phone (grid-cols-1)
    // and only sits side-by-side at sm. Class-presence proxy (no CSS in JSDOM).
    const dateGrid = document.querySelector(".sm\\:grid-cols-2");
    expect(dateGrid).not.toBeNull();
    expect(dateGrid?.className).toContain("grid-cols-1");

    // Scope clarification (#197): the date range bounds the activity log only.
    expect(
      screen.getByText(/the documents table always lists all of your active documents/i),
    ).toBeInTheDocument();
  });

  it("blocks an inverted date range before any request (#262)", async () => {
    server.use(stats(3));
    renderWithProviders(<ExportPage />, { auth: authedMe });
    await screen.findByRole("button", { name: /download pdf/i });
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

describe("ExportPage — empty state (#239)", () => {
  it("teaches when the org has no documents yet (no dead-end download buttons)", async () => {
    server.use(stats(0));
    renderWithProviders(<ExportPage />, { auth: authedMe });

    expect(await screen.findByText(/your audit report will appear here/i)).toBeInTheDocument();
    expect(screen.getByRole("link", { name: /collect your first document/i })).toHaveAttribute(
      "href",
      "/documents",
    );
    // The download buttons are NOT offered until there's something to export.
    expect(screen.queryByRole("button", { name: /download pdf/i })).toBeNull();
    expect(screen.queryByRole("button", { name: /download csv/i })).toBeNull();
  });

  it("shows the download cards once at least one document is in", async () => {
    server.use(stats(2));
    renderWithProviders(<ExportPage />, { auth: authedMe });

    expect(await screen.findByRole("button", { name: /download pdf/i })).toBeInTheDocument();
    expect(screen.getByRole("button", { name: /download csv/i })).toBeInTheDocument();
    await waitFor(() =>
      expect(screen.queryByText(/your audit report will appear here/i)).not.toBeInTheDocument(),
    );
  });
});
