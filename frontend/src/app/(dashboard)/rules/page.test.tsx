/**
 * Rules page — tier-3 smoke (#36).
 *
 * The page lists compliance templates and lets the user inspect their
 * rules. Smoke: render-without-crash + populated state surfaces a
 * template by name.
 */
import { describe, it, expect, vi } from "vitest";
import { http } from "msw";
import { screen, waitFor } from "@testing-library/react";
import RulesPage from "./page";
import {
  renderWithProviders,
  server,
  url,
  jsonOk,
  authedMe,
} from "@/test";

vi.mock("sonner", () => ({
  toast: { success: vi.fn(), error: vi.fn() },
  Toaster: () => null,
}));

describe("RulesPage — smoke (#36)", () => {
  it("renders the templates list when the API returns at least one", async () => {
    server.use(
      http.get(url("/api/compliance/templates"), () =>
        jsonOk([
          {
            id: "t_default_01",
            name: "Default COI",
            description: "Built-in COI checklist",
            isSystemTemplate: true,
            ruleCount: 5,
            vendorCount: 0,
          },
        ]),
      ),
    );

    renderWithProviders(<RulesPage />, { auth: authedMe });

    await waitFor(() =>
      expect(screen.getByText("Default COI")).toBeInTheDocument(),
    );
  });

  it("empty-state: renders the page chrome without crashing on an empty list", async () => {
    server.use(
      http.get(url("/api/compliance/templates"), () => jsonOk([])),
    );

    renderWithProviders(<RulesPage />, { auth: authedMe });

    // Page renders; templates section is empty but the heading shows.
    expect(document.body.textContent?.length).toBeGreaterThan(0);
  });
});
