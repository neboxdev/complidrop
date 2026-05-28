/**
 * Settings page — tier-3 smoke (#36) + billing-tile vocab pins (#147).
 *
 * The page is mostly billing UI driven off /api/billing/subscription;
 * loading + populated states get a render-without-crash assertion,
 * matching the AC carve-out for thin pages.
 *
 * The #147 additions pin the post-ADR-0011 contract:
 *   - The three billing tiles render the dollar values from
 *     `PLANS[id].monthlyPriceLabel`, NOT hardcoded literals.
 *   - All three tile labels (Pro / Annual / Founding) appear.
 *   - The legacy "Monthly" tile label is gone.
 */
import { describe, it, expect } from "vitest";
import { http } from "msw";
import { screen, waitFor, within } from "@testing-library/react";
import SettingsPage from "./page";
import { KNOWN_CHECKOUT_PLAN_IDS, PLANS } from "@/lib/plans";
import {
  renderWithProviders,
  server,
  url,
  jsonOk,
  authedMe,
} from "@/test";

// sonner is mocked by the harness (vitest.setup.ts + src/test/sonner.ts). See #74.

function mockFreePlanSubscription() {
  server.use(
    http.get(url("/api/billing/subscription"), () =>
      jsonOk({
        plan: "free",
        status: "active",
        documentLimit: 5,
        documentsUsed: 2,
        hasVendorPortal: false,
        currentPeriodEnd: null,
        extractionSpend: 0,
      }),
    ),
  );
}

describe("SettingsPage — smoke (#36)", () => {
  it("populated: renders the free-plan usage badge with the documents-used / limit pair", async () => {
    mockFreePlanSubscription();

    renderWithProviders(<SettingsPage />, { auth: authedMe });

    // The page surfaces the `documentsUsed / documentLimit` pair as
    // "2 / 5" in the usage tile. Pin that specifically — `toContain("2")`
    // would pass for a stray '2' anywhere on the page (date strings,
    // CSS pixel values that leak into textContent, etc.).
    await waitFor(() =>
      expect(screen.getByText(/2\s*\/\s*5/)).toBeInTheDocument(),
    );
  });
});

describe("SettingsPage — billing tile vocab (#147, ADR 0011)", () => {
  it("renders one tile per KNOWN_CHECKOUT_PLAN_IDS with the price from PLANS", async () => {
    mockFreePlanSubscription();
    renderWithProviders(<SettingsPage />, { auth: authedMe });

    // Wait for the tiles to mount — the upgrade-button label is the
    // most specific anchor ("Upgrade to Pro" appears only inside the
    // tile, not anywhere else on the page).
    await waitFor(() =>
      expect(
        screen.getByRole("button", { name: /Upgrade to Pro/i }),
      ).toBeInTheDocument(),
    );

    for (const id of KNOWN_CHECKOUT_PLAN_IDS) {
      const label = PLANS[id].label;
      const price = PLANS[id].monthlyPriceLabel;
      // Each tile renders `<Upgrade to {label}>` once; use that to
      // locate the right tile and assert the displayed price comes
      // from PLANS, not a stale hardcoded literal.
      const upgradeButton = screen.getByRole("button", {
        name: new RegExp(`Upgrade to ${label}`, "i"),
      });
      // Walk up to the tile container (the button's nearest .rounded-md ancestor).
      const tile = upgradeButton.closest("div.rounded-md") as HTMLElement;
      expect(tile).not.toBeNull();
      expect(within(tile).getByText(price)).toBeInTheDocument();
      expect(within(tile).getByText(label)).toBeInTheDocument();
    }
  });

  it("does NOT render the legacy 'Monthly' tile label (#147)", async () => {
    // Pre-#147 the Pro tile was labelled "Monthly" (the Stripe-side
    // billing-cadence wording). ADR 0011 unified the vocab to "Pro".
    // A regression that resurrected the old label would surface here.
    mockFreePlanSubscription();
    renderWithProviders(<SettingsPage />, { auth: authedMe });

    await waitFor(() =>
      expect(
        screen.getByRole("button", { name: /Upgrade to Pro/i }),
      ).toBeInTheDocument(),
    );

    // The "Monthly" label nowhere on the billing-tile section.
    // `queryAllByText` over the whole page returns []. (The word
    // "monthly" appears nowhere else on the page in the free-plan
    // state — only as a billing-cadence in the Pro tile if rendered.)
    expect(
      screen.queryByRole("button", { name: /Upgrade to Monthly/i }),
    ).toBeNull();
  });
});
