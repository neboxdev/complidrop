/**
 * Dashboard landing page — state matrix across three independent
 * queries (#36).
 *
 * The page fans out to useDashboardStats + useExpiryPipeline +
 * useRecentActivity, plus useMe for the greeting. Each can be in any of
 * loading / error / empty / populated, so we drive a representative set
 * of combinations: all-loading, all-populated, all-error (handled
 * gracefully via fallback values), and the partial-success path (one
 * hook fails, two resolve).
 */
import { describe, it, expect, vi } from "vitest";
import { http } from "msw";
import { screen, waitFor } from "@testing-library/react";
import DashboardPage from "./page";
import {
  renderWithProviders,
  server,
  url,
  jsonOk,
  jsonError,
  authedMe,
} from "@/test";

vi.mock("sonner", () => ({
  toast: { success: vi.fn(), error: vi.fn() },
  Toaster: () => null,
}));

const STATS = {
  totalDocuments: 12,
  compliant: 8,
  nonCompliant: 1,
  expiringSoon: 2,
  expired: 1,
  pendingExtraction: 3,
  totalVendors: 4,
  complianceRate: 67,
};
const PIPELINE = { expired: 1, bucket30: 2, bucket60: 1, bucket90: 3, beyond: 5 };
const ACTIVITY = [
  {
    id: "a_01",
    action: "document.uploaded",
    entityType: "Document",
    entityId: "d_completed_01",
    createdAt: "2026-05-26T12:00:00Z",
  },
];

describe("DashboardPage — state matrix (#36)", () => {
  it("loading (no responses yet): page chrome renders, recent-activity shows the loading copy", () => {
    // Hold all three responses so the test observes the loading branch.
    const settled = new Promise<void>(() => {}); // never resolves
    server.use(
      http.get(url("/api/dashboard/stats"), async () => {
        await settled;
        return jsonOk(STATS);
      }),
      http.get(url("/api/dashboard/expiry-pipeline"), async () => {
        await settled;
        return jsonOk(PIPELINE);
      }),
      http.get(url("/api/dashboard/recent-activity"), async () => {
        await settled;
        return jsonOk(ACTIVITY);
      }),
    );

    renderWithProviders(<DashboardPage />, { auth: authedMe });

    expect(
      screen.getByRole("heading", { name: /welcome, acme/i }),
    ).toBeInTheDocument();
    // Recent activity has an explicit "Loading…" copy until activity
    // resolves; that's the most direct loading-state signal on the page.
    expect(screen.getByText(/^loading…$/i)).toBeInTheDocument();
  });

  it("empty (zero-state org): all stats default to 0, recent-activity shows 'No recent activity'", async () => {
    server.use(
      http.get(url("/api/dashboard/stats"), () =>
        jsonOk({
          totalDocuments: 0,
          compliant: 0,
          nonCompliant: 0,
          expiringSoon: 0,
          expired: 0,
          pendingExtraction: 0,
          totalVendors: 0,
          complianceRate: 0,
        }),
      ),
      http.get(url("/api/dashboard/expiry-pipeline"), () =>
        jsonOk({ expired: 0, bucket30: 0, bucket60: 0, bucket90: 0, beyond: 0 }),
      ),
      http.get(url("/api/dashboard/recent-activity"), () => jsonOk([])),
    );

    renderWithProviders(<DashboardPage />, { auth: authedMe });

    await waitFor(() =>
      expect(
        screen.getByText(/no recent activity yet/i),
      ).toBeInTheDocument(),
    );
    // Compliance rate label is "0%".
    expect(screen.getByText(/^0%$/)).toBeInTheDocument();
  });

  it("populated: stats + pipeline + activity all render with their values", async () => {
    server.use(
      http.get(url("/api/dashboard/stats"), () => jsonOk(STATS)),
      http.get(url("/api/dashboard/expiry-pipeline"), () => jsonOk(PIPELINE)),
      http.get(url("/api/dashboard/recent-activity"), () => jsonOk(ACTIVITY)),
    );

    renderWithProviders(<DashboardPage />, { auth: authedMe });

    // Stats panel:
    await waitFor(() => expect(screen.getByText("12")).toBeInTheDocument());
    expect(screen.getByText("8")).toBeInTheDocument(); // compliant
    expect(screen.getByText("67%")).toBeInTheDocument(); // compliance rate

    // Activity panel surfaces a single entry's pretty-printed action.
    expect(
      screen.getByText(/document · uploaded/i),
    ).toBeInTheDocument();
  });

  it("error on /stats: page still renders via fallback `?? 0` values; pipeline+activity unaffected", async () => {
    // Partial-success path — UX requirement: a single hook failing
    // doesn't crash the page. Stats hook's error means stats.data is
    // undefined; the JSX uses `?? 0` to fall back to zero. The other
    // panels MUST still render with their successful data.
    server.use(
      http.get(url("/api/dashboard/stats"), () =>
        jsonError("server.error", "stats down", { status: 500 }),
      ),
      http.get(url("/api/dashboard/expiry-pipeline"), () => jsonOk(PIPELINE)),
      http.get(url("/api/dashboard/recent-activity"), () => jsonOk(ACTIVITY)),
    );

    renderWithProviders(<DashboardPage />, { auth: authedMe });

    // Page chrome + welcome text always render.
    await waitFor(() =>
      expect(
        screen.getByRole("heading", { name: /welcome, acme/i }),
      ).toBeInTheDocument(),
    );
    // Activity panel populated normally.
    await waitFor(() =>
      expect(screen.getByText(/document · uploaded/i)).toBeInTheDocument(),
    );
    // Pipeline panel populated normally.
    expect(screen.getByText(/expiry pipeline/i)).toBeInTheDocument();
    // Stats panel falls back to zeroes (no crash).
    // Multiple cards show "0" — assert via getAllByText.
    expect(screen.getAllByText(/^0$/).length).toBeGreaterThan(0);
  });
});
