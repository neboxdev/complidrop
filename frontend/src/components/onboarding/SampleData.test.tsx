/**
 * Sample-certificate demo UI (#238): the "Try it with a sample certificate" CTA, the
 * dashboard sample banner, and the "Clear sample data" action.
 */
import { describe, it, expect, vi } from "vitest";
import { http } from "msw";
import { fireEvent, screen, waitFor } from "@testing-library/react";
import {
  renderWithProviders,
  server,
  url,
  jsonOk,
  jsonError,
  authedMe,
  toastSuccess,
  toastError,
} from "@/test";
import type { DashboardStats } from "@/hooks/useDashboard";
import { TrySampleButton, ClearSampleButton, SampleDataBanner } from "./SampleData";

vi.mock("@/lib/analytics", () => ({
  identify: vi.fn(),
  resetIdentity: vi.fn(),
  track: vi.fn(),
}));

const baseStats: DashboardStats = {
  totalDocuments: 0,
  compliant: 0,
  nonCompliant: 0,
  expiringSoon: 0,
  expired: 0,
  pendingExtraction: 0,
  totalVendors: 0,
  anyVendorWithRequirements: false,
  anyActivePortalLink: false,
  hasSampleData: false,
  sampleDocumentId: null,
  complianceRate: 0,
};
const statsWithSample: DashboardStats = { ...baseStats, hasSampleData: true, sampleDocumentId: "d_sample_01" };

describe("TrySampleButton (#238)", () => {
  it("seeds the sample and navigates to the new document's verdict", async () => {
    const push = vi.fn();
    server.use(http.post(url("/api/sample"), () => jsonOk({ documentId: "d_sample_01", vendorId: "v_sample_01" })));
    renderWithProviders(<TrySampleButton />, { auth: authedMe, router: { push } });

    fireEvent.click(screen.getByRole("button", { name: /sample certificate/i }));

    await waitFor(() => expect(push).toHaveBeenCalledWith("/documents/d_sample_01"));
    expect(toastSuccess).toHaveBeenCalled();
    expect(toastError).not.toHaveBeenCalled();
  });

  it("shows the server's friendly error and does not navigate when seeding fails", async () => {
    const push = vi.fn();
    server.use(
      http.post(url("/api/sample"), () =>
        jsonError("storage.unavailable", "We couldn't set up the sample just now.", { status: 503 }),
      ),
    );
    renderWithProviders(<TrySampleButton />, { auth: authedMe, router: { push } });

    fireEvent.click(screen.getByRole("button", { name: /sample certificate/i }));

    await waitFor(() => expect(toastError).toHaveBeenCalledWith("We couldn't set up the sample just now."));
    expect(push).not.toHaveBeenCalled();
  });
});

describe("SampleDataBanner (#238)", () => {
  it("shows the view + clear affordances when the org has sample data", async () => {
    server.use(http.get(url("/api/dashboard/stats"), () => jsonOk(statsWithSample)));
    renderWithProviders(<SampleDataBanner />, { auth: authedMe });

    expect(await screen.findByText(/exploring with sample data/i)).toBeInTheDocument();
    expect(screen.getByRole("button", { name: /clear sample data/i })).toBeInTheDocument();
    expect(screen.getByRole("link", { name: /view sample/i })).toHaveAttribute("href", "/documents/d_sample_01");
  });

  it("renders nothing when there is no sample data", async () => {
    let statsCalls = 0;
    server.use(
      http.get(url("/api/dashboard/stats"), () => {
        statsCalls++;
        return jsonOk(baseStats);
      }),
    );
    renderWithProviders(<SampleDataBanner />, { auth: authedMe });

    await waitFor(() => expect(statsCalls).toBeGreaterThan(0));
    await waitFor(() =>
      expect(screen.queryByText(/exploring with sample data/i)).not.toBeInTheDocument(),
    );
  });
});

describe("ClearSampleButton (#238)", () => {
  it("clears the sample, toasts success, and fires onCleared", async () => {
    const onCleared = vi.fn();
    server.use(
      http.delete(url("/api/sample"), () =>
        jsonOk({ message: "Sample data cleared.", clearedDocuments: 1, clearedVendors: 1 }),
      ),
    );
    renderWithProviders(<ClearSampleButton onCleared={onCleared} />, { auth: authedMe });

    fireEvent.click(screen.getByRole("button", { name: /clear sample data/i }));

    await waitFor(() => expect(onCleared).toHaveBeenCalled());
    expect(toastSuccess).toHaveBeenCalledWith("Sample data cleared.");
  });
});
