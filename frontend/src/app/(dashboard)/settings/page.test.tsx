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
 *   - Each tile labels itself with `role="group" aria-label="X plan"`
 *     for screen-reader navigation + stable test scoping.
 *   - Clicking an Upgrade button sends `{ plan: id }` on the wire
 *     (the cross-component round-trip the bare-component tests can
 *     verify without spinning up the backend).
 *   - The legacy "Monthly" tile label is gone.
 */
import { describe, it, expect, beforeEach } from "vitest";
import { http } from "msw";
import { fireEvent, screen, waitFor, within } from "@testing-library/react";
import SettingsPage from "./page";
import { ME_KEY } from "@/hooks/useAuth";
import { KNOWN_CHECKOUT_PLAN_IDS, PLANS } from "@/lib/plans";
import {
  renderWithProviders,
  server,
  url,
  jsonOk,
  authedMe,
  makeMe,
  toastSuccess,
  resetSonner,
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

    // Responsive (#181): the usage stat tiles (Documents / Vendor portal / LLM
    // spend) stack on a phone (grid-cols-1) and only go 3-up at sm — a fixed
    // grid-cols-3 would squeeze them to ~110px on a 390px screen. Class-presence
    // proxy (JSDOM applies no stylesheet).
    const usageGrid = document.querySelector(".sm\\:grid-cols-3");
    expect(usageGrid).not.toBeNull();
    expect(usageGrid?.className).toContain("grid-cols-1");
  });
});

describe("SettingsPage — billing tile vocab (#147, ADR 0011)", () => {
  it("renders one tile per KNOWN_CHECKOUT_PLAN_IDS with the price + label from PLANS", async () => {
    mockFreePlanSubscription();
    renderWithProviders(<SettingsPage />, { auth: authedMe });

    // Wait for the tiles to mount — the upgrade-button label is the
    // most specific anchor ("Upgrade to Pro" appears only inside the
    // Pro tile, not anywhere else on the page).
    await waitFor(() =>
      expect(
        screen.getByRole("button", { name: /Upgrade to Pro/i }),
      ).toBeInTheDocument(),
    );

    for (const id of KNOWN_CHECKOUT_PLAN_IDS) {
      const label = PLANS[id].label;
      const price = PLANS[id].monthlyPriceLabel;
      // Locate the tile via its role="group" + aria-label landmark —
      // stable across className refactors and matches CLAUDE.md's
      // "prefer accessible-text selectors" guidance.
      const tile = screen.getByRole("group", {
        name: new RegExp(`${label} plan`, "i"),
      });
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

    expect(
      screen.queryByRole("button", { name: /Upgrade to Monthly/i }),
    ).toBeNull();
  });

  it.each(KNOWN_CHECKOUT_PLAN_IDS.map((id) => [id]))(
    "POSTs { plan: '%s' } to /api/billing/checkout when the corresponding Upgrade button is clicked (#147 round-trip)",
    async (id) => {
      // The single highest-value coverage gap pointed out by the #147
      // test-quality review: AC #5 asks for round-trip vocabulary
      // coverage from settings → checkout endpoint. This binds the
      // tile (rendered via PLANS[id].label) to the wire body sent on
      // the request — a regression where the mutationFn hardcoded
      // `plan: "monthly"` (the pre-#147 bug) would fail every
      // iteration of this `it.each` block.
      mockFreePlanSubscription();
      let captured: { plan?: string } | null = null;
      const stubSessionUrl = `https://stub.test/cs_stub_${id}`;
      server.use(
        http.post(url("/api/billing/checkout"), async ({ request }) => {
          captured = (await request.json()) as { plan?: string };
          return jsonOk({ sessionUrl: stubSessionUrl });
        }),
      );
      // Stub window.location.href so the onSuccess redirect doesn't
      // actually navigate in jsdom (which would throw). The waitFor
      // below blocks until the redirect side-effect has fired, so the
      // restoration step is race-free.
      const originalLocation = window.location;
      Object.defineProperty(window, "location", {
        writable: true,
        value: { ...originalLocation, href: "" },
      });

      try {
        renderWithProviders(<SettingsPage />, { auth: authedMe });

        const label = PLANS[id].label;
        const upgradeButton = await screen.findByRole("button", {
          name: new RegExp(`Upgrade to ${label}`, "i"),
        });
        fireEvent.click(upgradeButton);

        // Wait for the full success path to complete: request captured
        // AND onSuccess's `window.location.href = res.sessionUrl`
        // assignment has fired. The href assertion doubles as
        // coverage that onSuccess routes the response sessionUrl
        // through rather than the wire plan.
        await waitFor(() => {
          expect(captured).not.toBeNull();
          expect(window.location.href).toBe(stubSessionUrl);
        });
        expect(captured!.plan).toBe(id);
      } finally {
        // Restore window.location for downstream tests, even if an
        // assertion threw above.
        Object.defineProperty(window, "location", {
          writable: true,
          value: originalLocation,
        });
      }
    },
  );
});

describe("SettingsPage — friendly billing copy (#188)", () => {
  it("renders the annual billed label + savings under the Annual tile", async () => {
    mockFreePlanSubscription();
    renderWithProviders(<SettingsPage />, { auth: authedMe });

    const annual = await screen.findByRole("group", { name: /annual plan/i });
    expect(within(annual).getByText(/Billed \$468\/year/)).toBeInTheDocument();
    expect(within(annual).getByText(/save \$120/)).toBeInTheDocument();
  });

  it("maps a past_due Stripe status to a friendly banner — never the raw code", async () => {
    server.use(
      http.get(url("/api/billing/subscription"), () =>
        jsonOk({
          plan: "pro",
          status: "past_due",
          documentLimit: null,
          documentsUsed: 3,
          hasVendorPortal: true,
          currentPeriodEnd: null,
          extractionSpend: 1.2,
        }),
      ),
    );
    renderWithProviders(<SettingsPage />, { auth: authedMe });

    await waitFor(() =>
      expect(
        screen.getByText(/your last payment didn't go through/i),
      ).toBeInTheDocument(),
    );
    // The raw Stripe status string must never reach the UI.
    expect(document.body).not.toHaveTextContent(/past_due/i);
  });

  it("maps an UNKNOWN Stripe status to the generic friendly notice (no raw leak)", async () => {
    server.use(
      http.get(url("/api/billing/subscription"), () =>
        jsonOk({
          plan: "pro",
          status: "some_brand_new_stripe_state",
          documentLimit: null,
          documentsUsed: 0,
          hasVendorPortal: true,
          currentPeriodEnd: null,
          extractionSpend: 0,
        }),
      ),
    );
    renderWithProviders(<SettingsPage />, { auth: authedMe });

    await waitFor(() =>
      expect(
        screen.getByText(/there's a problem with your billing/i),
      ).toBeInTheDocument(),
    );
    expect(document.body).not.toHaveTextContent(/some_brand_new_stripe_state/i);
  });
});

describe("SettingsPage — editable organization (#185)", () => {
  beforeEach(() => resetSonner());

  it("renders org name + time zone as editable controls, pre-filled from the session", () => {
    mockFreePlanSubscription();
    renderWithProviders(<SettingsPage />, { auth: authedMe });

    const name = screen.getByLabelText(/organization name/i) as HTMLInputElement;
    expect(name.value).toBe("Acme Inc");
    const tz = screen.getByLabelText(/^time zone$/i) as HTMLSelectElement;
    expect(tz.value).toBe("UTC");
    // The next-send preview makes the zone's effect visible.
    expect(screen.getByText(/reminders send at 8:00 AM/i)).toBeInTheDocument();
  });

  it("saves the new name + time zone, toasts, and writes the SERVER response into the Me cache", async () => {
    mockFreePlanSubscription();
    let captured: { name: string; timeZone: string } | null = null;
    // Server canonicalizes the name (distinct from the typed input) so the cache
    // assertion proves the RESPONSE drove the update, not the local form state.
    server.use(
      http.put(url("/api/auth/organization"), async ({ request }) => {
        captured = (await request.json()) as { name: string; timeZone: string };
        return jsonOk(makeMe({ organizationName: "Acme Compliance LLC (canonical)", timeZone: captured.timeZone }));
      }),
    );
    const { queryClient } = renderWithProviders(<SettingsPage />, { auth: authedMe });

    fireEvent.change(screen.getByLabelText(/organization name/i), {
      target: { value: "Acme Compliance LLC" },
    });
    fireEvent.change(screen.getByLabelText(/^time zone$/i), {
      target: { value: "America/Chicago" },
    });
    fireEvent.click(screen.getByRole("button", { name: /save changes/i }));

    await waitFor(() => expect(captured).not.toBeNull());
    expect(captured!).toEqual({ name: "Acme Compliance LLC", timeZone: "America/Chicago" });
    await waitFor(() =>
      expect(toastSuccess).toHaveBeenCalledWith("Organization settings saved."),
    );
    // The hook's onSuccess setMeCache wrote the server's canonical value into the
    // Me cache (which the sidebar org name reads) — pinned via the query cache.
    await waitFor(() =>
      expect((queryClient.getQueryData([...ME_KEY]) as { organizationName: string }).organizationName)
        .toBe("Acme Compliance LLC (canonical)"),
    );
  });

  it("keeps Save disabled until a field changes", () => {
    mockFreePlanSubscription();
    renderWithProviders(<SettingsPage />, { auth: authedMe });

    const save = screen.getByRole("button", { name: /save changes/i });
    expect(save).toBeDisabled();
    fireEvent.change(screen.getByLabelText(/organization name/i), {
      target: { value: "Acme Inc 2" },
    });
    expect(save).not.toBeDisabled();
  });
});
