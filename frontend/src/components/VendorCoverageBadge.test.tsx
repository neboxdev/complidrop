/**
 * VendorCoverageBadge (#319 FP-074) — the user-facing payoff of the server-side
 * coverage rollup. Pins each rendered state so a regression that dropped the
 * missingTypes label or mislabeled a state would fail here.
 */
import { describe, it, expect } from "vitest";
import { screen } from "@testing-library/react";
import { renderWithProviders } from "@/test";
import { VendorCoverageBadge } from "./VendorCoverageBadge";

describe("VendorCoverageBadge", () => {
  it("renders a Covered badge", () => {
    renderWithProviders(<VendorCoverageBadge coverage={{ status: "Covered", missingTypes: [] }} />, { auth: null });
    expect(screen.getByText("Covered")).toBeInTheDocument();
  });

  it("renders an Action needed badge", () => {
    renderWithProviders(<VendorCoverageBadge coverage={{ status: "ActionNeeded", missingTypes: [] }} />, { auth: null });
    expect(screen.getByText(/action needed/i)).toBeInTheDocument();
  });

  it("names the missing document types", () => {
    renderWithProviders(
      <VendorCoverageBadge coverage={{ status: "Missing", missingTypes: ["insurance", "license"] }} />,
      { auth: null },
    );
    expect(screen.getByText(/missing: insurance, license/i)).toBeInTheDocument();
  });

  it("links to set requirements when none are set (given an href)", () => {
    renderWithProviders(
      <VendorCoverageBadge coverage={{ status: "NoRequirements", missingTypes: [] }} noRequirementsHref="/vendors/v1" />,
      { auth: null },
    );
    expect(screen.getByRole("link", { name: /set requirements/i })).toHaveAttribute("href", "/vendors/v1");
  });

  it("renders muted text for no requirements without an href", () => {
    renderWithProviders(<VendorCoverageBadge coverage={{ status: "NoRequirements", missingTypes: [] }} />, { auth: null });
    expect(screen.getByText(/no requirements set/i)).toBeInTheDocument();
  });
});
