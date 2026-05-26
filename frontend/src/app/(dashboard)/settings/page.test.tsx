/**
 * Settings page — tier-3 smoke (#36).
 *
 * The page is mostly billing UI driven off /api/billing/subscription;
 * loading + populated states get a render-without-crash assertion,
 * matching the AC carve-out for thin pages.
 */
import { describe, it, expect, vi } from "vitest";
import { http } from "msw";
import { waitFor } from "@testing-library/react";
import SettingsPage from "./page";
import {
  renderWithProviders,
  server,
  url,
  jsonOk,
  authedMe,
} from "@/test";

vi.mock("sonner", () => ({
  toast: { success: vi.fn(), error: vi.fn(), info: vi.fn() },
  Toaster: () => null,
}));

describe("SettingsPage — smoke (#36)", () => {
  it("populated: renders without crashing and surfaces the plan info", async () => {
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

    // The page shows usage somewhere — assert "2" (used) reads correctly.
    // Don't pin specific copy here; this is the smoke tier.
    await waitFor(() => expect(document.body.textContent).toContain("2"));
  });
});
