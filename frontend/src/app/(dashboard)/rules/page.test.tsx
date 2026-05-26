/**
 * Rules page — tier-3 smoke (#36).
 *
 * The page lists compliance templates and lets the user inspect their
 * rules. Smoke: render-without-crash + populated state surfaces a
 * template by name.
 */
import { describe, it, expect } from "vitest";
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

// sonner is mocked by the harness (vitest.setup.ts + src/test/sonner.ts). See #74.

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

  it("empty-state: renders the page chrome and the create-template input", async () => {
    server.use(
      http.get(url("/api/compliance/templates"), () => jsonOk([])),
    );

    renderWithProviders(<RulesPage />, { auth: authedMe });

    // The new-template input is unconditional — a regression that
    // hides the template editor would drop this placeholder.
    expect(
      screen.getByPlaceholderText(/template name/i) ??
        screen.getByPlaceholderText(/new template/i),
    ).toBeInTheDocument();
  });
});
