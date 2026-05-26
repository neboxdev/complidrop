/**
 * Settings page — tier-3 smoke (#36).
 *
 * The page is mostly billing UI driven off /api/billing/subscription;
 * loading + populated states get a render-without-crash assertion,
 * matching the AC carve-out for thin pages.
 */
import { describe, it, expect } from "vitest";
import { http } from "msw";
import { screen, waitFor } from "@testing-library/react";
import SettingsPage from "./page";
import {
  renderWithProviders,
  server,
  url,
  jsonOk,
  authedMe,
} from "@/test";

// sonner is mocked by the harness (vitest.setup.ts + src/test/sonner.ts). See #74.

describe("SettingsPage — smoke (#36)", () => {
  it("populated: renders the free-plan usage badge with the documents-used / limit pair", async () => {
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
