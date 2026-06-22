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
import { describe, it, expect, vi, beforeEach, afterEach } from "vitest";
import { http } from "msw";
import { renderHook, waitFor } from "@testing-library/react";
import {
  useDashboardStats,
  useExpiryPipeline,
  useRecentActivity,
  type DashboardStats,
  type ExpiryPipeline,
  type ActivityEntry,
} from "./useDashboard";
import { createTestWrapper, server, url, jsonOk, jsonError, sequencedJsonOk } from "@/test";
import { ApiError } from "@/lib/api";

vi.mock("@/lib/analytics", () => ({
  identify: vi.fn(),
  resetIdentity: vi.fn(),
  track: vi.fn(),
}));

const STATS: DashboardStats = {
  totalDocuments: 12,
  compliant: 8,
  nonCompliant: 1,
  expiringSoon: 2,
  expired: 1,
  pendingExtraction: 3,
  totalVendors: 4,
  anyVendorWithRequirements: true,
  anyActivePortalLink: false,
  hasSampleData: false,
  sampleDocumentId: null,
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
    const { Wrapper } = createTestWrapper();
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
    const { Wrapper } = createTestWrapper();
    const { result } = renderHook(() => useDashboardStats(), { wrapper: Wrapper });
    await waitFor(() => expect(result.current.isError).toBe(true));
    expect((result.current.error as ApiError).message).toBe("Stats down.");
  });
});

describe("useDashboardStats — 15s polling gated on pendingExtraction (#318 FP-049)", () => {
  // shouldAdvanceTime: true so RTL's waitFor (real setTimeout) doesn't hang under
  // fake timers; delta-based assertions absorb any auto-fire during waitFor.
  beforeEach(() => vi.useFakeTimers({ shouldAdvanceTime: true }));
  afterEach(() => vi.useRealTimers());

  it("polls every 15s while pendingExtraction>0, STOPS once it reaches 0", async () => {
    let calls = 0;
    const seq = sequencedJsonOk(
      { ...STATS, pendingExtraction: 3 },
      { ...STATS, pendingExtraction: 0 },
    );
    server.use(http.get(url("/api/dashboard/stats"), () => { calls++; return seq(); }));

    const { Wrapper } = createTestWrapper();
    const { result } = renderHook(() => useDashboardStats(), { wrapper: Wrapper });

    await waitFor(() => expect(result.current.data?.pendingExtraction).toBe(3));
    const before = calls;
    await vi.advanceTimersByTimeAsync(15_000);
    await waitFor(() => expect(result.current.data?.pendingExtraction).toBe(0));
    expect(calls).toBeGreaterThanOrEqual(before + 1);

    // Now nothing is pending → the interval stops. Advance well past 3 windows.
    const after = calls;
    await vi.advanceTimersByTimeAsync(45_000);
    expect(calls).toBe(after);
  });

  it("does NOT poll an idle dashboard (pendingExtraction=0 on first read) — catches a flipped predicate", async () => {
    let calls = 0;
    server.use(http.get(url("/api/dashboard/stats"), () => { calls++; return jsonOk({ ...STATS, pendingExtraction: 0 }); }));

    const { Wrapper } = createTestWrapper();
    const { result } = renderHook(() => useDashboardStats(), { wrapper: Wrapper });

    await waitFor(() => expect(result.current.isSuccess).toBe(true));
    expect(calls).toBe(1);
    await vi.advanceTimersByTimeAsync(60_000);
    expect(calls).toBe(1);
  });

  it("does NOT poll a dead endpoint (error state short-circuits)", async () => {
    let calls = 0;
    server.use(http.get(url("/api/dashboard/stats"), () => { calls++; return jsonError("server.error", "down", { status: 500 }); }));

    const { Wrapper } = createTestWrapper();
    const { result } = renderHook(() => useDashboardStats(), { wrapper: Wrapper });

    await waitFor(() => expect(result.current.isError).toBe(true));
    const after = calls;
    await vi.advanceTimersByTimeAsync(60_000);
    expect(calls).toBe(after);
  });
});

describe("useExpiryPipeline (#36)", () => {
  it("isPending → isSuccess populates the bucket totals", async () => {
    server.use(
      http.get(url("/api/dashboard/expiry-pipeline"), () => jsonOk(PIPELINE)),
    );
    const { Wrapper } = createTestWrapper();
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
    const { Wrapper } = createTestWrapper();
    const { result } = renderHook(() => useRecentActivity(), { wrapper: Wrapper });
    await waitFor(() => expect(result.current.isSuccess).toBe(true));
    expect(result.current.data).toHaveLength(2);
    expect(result.current.data?.[0].action).toBe("document.uploaded");
  });

  it("empty payload (no activity) is a success, not an error", async () => {
    server.use(
      http.get(url("/api/dashboard/recent-activity"), () => jsonOk([])),
    );
    const { Wrapper } = createTestWrapper();
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

    const { Wrapper } = createTestWrapper();
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

    const { Wrapper } = createTestWrapper();
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
