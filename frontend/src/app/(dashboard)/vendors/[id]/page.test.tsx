/**
 * Vendor detail page — smoke (#36).
 *
 * Tier-1 page but most contract lives at the useVendors hook level
 * (see useVendors.test.tsx for the portal-link generate/revoke
 * invalidation contract). Here we pin:
 *   - Loading copy while the detail fetch is in flight.
 *   - Populated render: name, contact, portal link list.
 */
import { describe, it, expect } from "vitest";
import { http } from "msw";
import { screen, waitFor } from "@testing-library/react";
import VendorDetailPage from "./page";
import {
  renderWithProviders,
  server,
  url,
  jsonOk,
  authedMe,
  toastSuccess,
  toastError,
} from "@/test";

// sonner mock + spies are provided by the harness; afterEach in the
// setup file resets all toast spies between tests (#74). These smoke
// renders don't drive any mutation path, so no toast should fire —
// the negative assertions in each test pin that contract.

const VENDOR_DETAIL = {
  id: "v_acme_01",
  name: "Acme Subcontractor",
  contactEmail: "ops@acme.test",
  contactPhone: null,
  category: "electrical",
  complianceTemplateId: null,
  complianceTemplateName: null,
  portalLinks: [
    {
      id: "pl_01",
      token: "abc",
      fullUrl: "http://example.test/portal/abc",
      isActive: true,
      uploadCount: 0,
      maxUploads: 5,
      expiresAt: null,
      createdAt: "2026-05-26T00:00:00Z",
    },
  ],
  createdAt: "2026-01-01T00:00:00Z",
  updatedAt: "2026-05-26T00:00:00Z",
};

describe("VendorDetailPage — smoke (#36)", () => {
  it("loading: renders the loading copy while fetch is in flight", () => {
    const settled = new Promise<void>(() => {});
    server.use(
      http.get(url("/api/vendors/:id"), async () => {
        await settled;
        return jsonOk(VENDOR_DETAIL);
      }),
      http.get(url("/api/compliance/templates"), () => jsonOk([])),
    );

    renderWithProviders(<VendorDetailPage />, {
      auth: authedMe,
      params: { id: "v_acme_01" },
    });

    expect(screen.getByText(/loading vendor/i)).toBeInTheDocument();
    // Loading-state smoke renders no mutation paths, so toasts must
    // not fire (#74 review).
    expect(toastSuccess).not.toHaveBeenCalled();
    expect(toastError).not.toHaveBeenCalled();
  });

  it("populated: renders the vendor name + contact + portal link", async () => {
    server.use(
      http.get(url("/api/vendors/:id"), () => jsonOk(VENDOR_DETAIL)),
      http.get(url("/api/compliance/templates"), () => jsonOk([])),
    );

    renderWithProviders(<VendorDetailPage />, {
      auth: authedMe,
      params: { id: "v_acme_01" },
    });

    await waitFor(() =>
      expect(
        screen.getByRole("heading", { name: /acme subcontractor/i }),
      ).toBeInTheDocument(),
    );
    // Portal link URL surfaces somewhere — readable input field or
    // anchor; assert via the URL text.
    expect(
      screen.getByDisplayValue("http://example.test/portal/abc"),
    ).toBeInTheDocument();
    // Populated smoke renders no mutation path, so toasts must not
    // fire (#74 review). A regression that auto-fired a toast on
    // mount would trip this.
    expect(toastSuccess).not.toHaveBeenCalled();
    expect(toastError).not.toHaveBeenCalled();
  });
});
