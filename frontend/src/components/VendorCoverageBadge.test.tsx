/**
 * VendorCoverageBadge (#319 FP-074) — the user-facing payoff of the server-side
 * coverage rollup. Pins each rendered state so a regression that dropped the
 * missingTypes label or mislabeled a state would fail here.
 *
 * #399: the Covered badge also surfaces "Covered through {date}" so a venue
 * manager can eyeball coverage against an event date — "Covered" is current as
 * of today, not a promise about a future date.
 */
import { describe, it, expect } from "vitest";
import { screen } from "@testing-library/react";
import { renderWithProviders } from "@/test";
import { formatCalendarDate } from "@/lib/dates";
import { VendorCoverageBadge } from "./VendorCoverageBadge";

describe("VendorCoverageBadge", () => {
  it("renders a Covered badge (no horizon when there is no dated doc)", () => {
    renderWithProviders(
      <VendorCoverageBadge coverage={{ status: "Covered", missingTypes: [], coveredThrough: null }} />,
      { auth: null },
    );
    expect(screen.getByText("Covered")).toBeInTheDocument();
    // Without a coveredThrough date there is nothing to promise — no "through" line.
    expect(screen.queryByText(/covered through/i)).toBeNull();
  });

  it("renders 'Covered through {date}' when a coverage horizon is known (#399)", () => {
    const iso = "2026-10-07T00:00:00Z";
    renderWithProviders(
      <VendorCoverageBadge coverage={{ status: "Covered", missingTypes: [], coveredThrough: iso }} />,
      { auth: null },
    );
    // The date renders through the app's calendar formatter (UTC-pinned), never as raw ISO.
    expect(screen.getByText(`Covered through ${formatCalendarDate(iso)}`)).toBeInTheDocument();
    expect(screen.queryByText(iso)).toBeNull();
  });

  it("renders an Action needed badge", () => {
    renderWithProviders(
      <VendorCoverageBadge coverage={{ status: "ActionNeeded", missingTypes: [], coveredThrough: null }} />,
      { auth: null },
    );
    expect(screen.getByText(/action needed/i)).toBeInTheDocument();
  });

  it("names the missing document types", () => {
    renderWithProviders(
      <VendorCoverageBadge coverage={{ status: "Missing", missingTypes: ["insurance", "license"], coveredThrough: null }} />,
      { auth: null },
    );
    expect(screen.getByText(/missing: insurance, license/i)).toBeInTheDocument();
  });

  it("links to set requirements when none are set (given an href)", () => {
    renderWithProviders(
      <VendorCoverageBadge
        coverage={{ status: "NoRequirements", missingTypes: [], coveredThrough: null }}
        noRequirementsHref="/vendors/v1"
      />,
      { auth: null },
    );
    expect(screen.getByRole("link", { name: /set requirements/i })).toHaveAttribute("href", "/vendors/v1");
  });

  it("renders muted text for no requirements without an href", () => {
    renderWithProviders(
      <VendorCoverageBadge coverage={{ status: "NoRequirements", missingTypes: [], coveredThrough: null }} />,
      { auth: null },
    );
    expect(screen.getByText(/no requirements set/i)).toBeInTheDocument();
  });
});
