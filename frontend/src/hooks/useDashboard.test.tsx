/**
 * useDashboard — three independent queries against the dashboard API
 * surface (#36):
 *
 *   - useDashboardStats  → /api/dashboard/stats
 *   - useExpiryPipeline  → /api/dashboard/expiry-pipeline
 *   - useRecentActivity  → /api/dashboard/recent-activity
 *
 * Each hook owns its own queryKey and a 30-second staleTime. The hooks
 * are independent — no shared cache invalidation, no derived state —
 * so the tests pin loading / data / error states for each in isolation,
 * plus one combined test that exercises all three simultaneously the
 * way DashboardPage does.
 */
import { describe, it, expect, vi } from "vitest";
import { http } from "msw";
import { renderHook, waitFor } from "@testing-library/react";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import type { ReactNode } from "react";
import {
  useDashboardStats,
  useExpiryPipeline,
  useRecentActivity,
  type DashboardStats,
  type ExpiryPipeline,
  type ActivityEntry,
} from "./useDashboard";
import { server, url, jsonOk, jsonError } from "@/test";
import { ApiError } from "@/lib/api";

vi.mock("@/lib/analytics", () => ({
  identify: vi.fn(),
  resetIdentity: vi.fn(),
  track: vi.fn(),
}));

function makeWrapper() {
  const qc = new QueryClient({
    defaultOptions: {
      queries: { retry: false, gcTime: Infinity },
      mutations: { retry: false },
    },
  });
  function Wrapper({ children }: { children: ReactNode }) {
    return <QueryClientProvider client={qc}>{children}</QueryClientProvider>;
  }
  return { qc, Wrapper };
}

const STATS: DashboardStats = {
  totalDocuments: 12,
  compliant: 8,
  nonCompliant: 1,
  expiringSoon: 2,
  expired: 1,
  pendingExtraction: 3,
  totalVendors: 4,
  complianceRate: 67,
};

const PIPELINE: ExpiryPipeline = {
  expired: 1,
  bucket30: 2,
  bucket60: 1,
  bucket90: 3,
  beyond: 5,
};

const ACTIVITY: ActivityEntry[] = [
  {
    id: "a_01",
    action: "document.uploaded",
    entityType: "Document",
    entityId: "d_completed_01",
    createdAt: "2026-05-26T12:00:00Z",
  },
  {
    id: "a_02",
    action: "vendor.created",
    entityType: "Vendor",
    entityId: "v_acme_01",
    createdAt: "2026-05-26T11:00:00Z",
  },
];

describe("useDashboardStats (#36)", () => {
  it("isPending → isSuccess populates the stats record", async () => {
    server.use(http.get(url("/api/dashboard/stats"), () => jsonOk(STATS)));
    const { Wrapper } = makeWrapper();
    const { result } = renderHook(() => useDashboardStats(), { wrapper: Wrapper });

    expect(result.current.isPending).toBe(true);
    await waitFor(() => expect(result.current.isSuccess).toBe(true));
    expect(result.current.data).toEqual(STATS);
  });

  it("isError on 500 — error carries the server message", async () => {
    server.use(
      http.get(url("/api/dashboard/stats"), () =>
        jsonError("server.error", "Stats down.", { status: 500 }),
      ),
    );
    const { Wrapper } = makeWrapper();
    const { result } = renderHook(() => useDashboardStats(), { wrapper: Wrapper });
    await waitFor(() => expect(result.current.isError).toBe(true));
    expect((result.current.error as ApiError).message).toBe("Stats down.");
  });
});

describe("useExpiryPipeline (#36)", () => {
  it("isPending → isSuccess populates the bucket totals", async () => {
    server.use(
      http.get(url("/api/dashboard/expiry-pipeline"), () => jsonOk(PIPELINE)),
    );
    const { Wrapper } = makeWrapper();
    const { result } = renderHook(() => useExpiryPipeline(), { wrapper: Wrapper });
    await waitFor(() => expect(result.current.isSuccess).toBe(true));
    expect(result.current.data).toEqual(PIPELINE);
  });
});

describe("useRecentActivity (#36)", () => {
  it("isPending → isSuccess populates a sorted activity list", async () => {
    server.use(
      http.get(url("/api/dashboard/recent-activity"), () => jsonOk(ACTIVITY)),
    );
    const { Wrapper } = makeWrapper();
    const { result } = renderHook(() => useRecentActivity(), { wrapper: Wrapper });
    await waitFor(() => expect(result.current.isSuccess).toBe(true));
    expect(result.current.data).toHaveLength(2);
    expect(result.current.data?.[0].action).toBe("document.uploaded");
  });

  it("empty payload (no activity) is a success, not an error", async () => {
    server.use(
      http.get(url("/api/dashboard/recent-activity"), () => jsonOk([])),
    );
    const { Wrapper } = makeWrapper();
    const { result } = renderHook(() => useRecentActivity(), { wrapper: Wrapper });
    await waitFor(() => expect(result.current.isSuccess).toBe(true));
    expect(result.current.data).toEqual([]);
    expect(result.current.isError).toBe(false);
  });
});

describe("Combined: dashboard page fans out three independent queries (#36)", () => {
  it("all three hooks resolve in parallel against their own endpoints", async () => {
    server.use(
      http.get(url("/api/dashboard/stats"), () => jsonOk(STATS)),
      http.get(url("/api/dashboard/expiry-pipeline"), () => jsonOk(PIPELINE)),
      http.get(url("/api/dashboard/recent-activity"), () => jsonOk(ACTIVITY)),
    );

    const { Wrapper } = makeWrapper();
    const { result } = renderHook(
      () => ({
        stats: useDashboardStats(),
        pipeline: useExpiryPipeline(),
        activity: useRecentActivity(),
      }),
      { wrapper: Wrapper },
    );

    await waitFor(() => {
      expect(result.current.stats.isSuccess).toBe(true);
      expect(result.current.pipeline.isSuccess).toBe(true);
      expect(result.current.activity.isSuccess).toBe(true);
    });
    expect(result.current.stats.data?.totalDocuments).toBe(12);
    expect(result.current.pipeline.data?.expired).toBe(1);
    expect(result.current.activity.data?.length).toBe(2);
  });

  it("one hook failing doesn't poison the others — partial success is observable", async () => {
    // Page UX: if /stats is down but /expiry-pipeline + /recent-activity
    // resolve, the page should still render the buckets and activity.
    server.use(
      http.get(url("/api/dashboard/stats"), () =>
        jsonError("server.error", "Stats down.", { status: 500 }),
      ),
      http.get(url("/api/dashboard/expiry-pipeline"), () => jsonOk(PIPELINE)),
      http.get(url("/api/dashboard/recent-activity"), () => jsonOk(ACTIVITY)),
    );

    const { Wrapper } = makeWrapper();
    const { result } = renderHook(
      () => ({
        stats: useDashboardStats(),
        pipeline: useExpiryPipeline(),
        activity: useRecentActivity(),
      }),
      { wrapper: Wrapper },
    );

    await waitFor(() => {
      expect(result.current.stats.isError).toBe(true);
      expect(result.current.pipeline.isSuccess).toBe(true);
      expect(result.current.activity.isSuccess).toBe(true);
    });
  });
});
