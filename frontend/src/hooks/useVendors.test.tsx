/**
 * useVendors — list / detail / mutation contract (#36).
 *
 * Pins:
 *   - useVendors list query: loading/data/error states.
 *   - useVendor detail query: `enabled` guard skips when id is null;
 *     fetches and resolves when id is provided.
 *   - useCreateVendor / useUpdateVendor / useDeleteVendor: success
 *     invalidates ['vendors'] (and the detail key, for update).
 *   - useGeneratePortalLink / useRevokePortalLink: invalidate the
 *     prefix ['vendors'] so BOTH the list summary's `activePortalLinks`
 *     count AND the detail's portalLinks list re-read with one
 *     invalidate (#113 — list-page stale-count gap surfaced by the
 *     #81 post-merge performance review).
 */
import { describe, it, expect, vi, beforeEach } from "vitest";
import { http } from "msw";
import { renderHook, waitFor } from "@testing-library/react";
import {
  useVendor,
  useVendors,
  useCreateVendor,
  useUpdateVendor,
  useDeleteVendor,
  useGeneratePortalLink,
  useRevokePortalLink,
  type VendorDetail,
  type VendorSummary,
} from "./useVendors";
import { useDashboardStats } from "./useDashboard";
import { createTestWrapper, server, url, jsonOk, jsonError } from "@/test";
import { ApiError } from "@/lib/api";

vi.mock("@/lib/analytics", () => ({
  identify: vi.fn(),
  resetIdentity: vi.fn(),
  track: vi.fn(),
}));

const VENDORS: VendorSummary[] = [
  {
    id: "v_acme_01",
    name: "Acme Subcontractor",
    contactEmail: "ops@acme.test",
    contactPhone: null,
    category: "electrical",
    complianceTemplateId: "t_default_01",
    complianceTemplateName: "Default COI",
    documentCount: 3,
    activePortalLinks: 1,
    isSample: false,
    coverage: { status: "Covered", missingTypes: [] },
  },
  {
    id: "v_beach_01",
    name: "Beachfront Janitorial",
    contactEmail: null,
    contactPhone: null,
    category: "janitorial",
    complianceTemplateId: null,
    complianceTemplateName: null,
    documentCount: 0,
    activePortalLinks: 0,
    isSample: false,
    coverage: { status: "NoRequirements", missingTypes: [] },
  },
];

const VENDOR_DETAIL: VendorDetail = {
  id: "v_acme_01",
  name: "Acme Subcontractor",
  contactEmail: "ops@acme.test",
  contactPhone: null,
  category: "electrical",
  complianceTemplateId: "t_default_01",
  complianceTemplateName: "Default COI",
  portalLinks: [
    {
      id: "pl_01",
      token: "abc",
      fullUrl: "http://example.test/portal/abc",
      isActive: true,
      uploadCount: 0,
      maxUploads: 5,
      expiresAt: "2026-12-31T00:00:00Z",
      createdAt: "2026-05-26T00:00:00Z",
    },
  ],
  createdAt: "2026-01-01T00:00:00Z",
  updatedAt: "2026-05-26T00:00:00Z",
  coverage: { status: "Covered", missingTypes: [] },
};

describe("useVendors — list query (#36)", () => {
  it("isPending → isSuccess populates the list", async () => {
    server.use(http.get(url("/api/vendors"), () => jsonOk(VENDORS)));
    const { Wrapper } = createTestWrapper();
    const { result } = renderHook(() => useVendors(), { wrapper: Wrapper });

    expect(result.current.isPending).toBe(true);
    await waitFor(() => expect(result.current.isSuccess).toBe(true));
    expect(result.current.data).toHaveLength(2);
    expect(result.current.data?.[0].name).toBe("Acme Subcontractor");
  });

  it("isError on 500 surfaces the server message", async () => {
    server.use(
      http.get(url("/api/vendors"), () =>
        jsonError("server.error", "vendor index down.", { status: 500 }),
      ),
    );
    const { Wrapper } = createTestWrapper();
    const { result } = renderHook(() => useVendors(), { wrapper: Wrapper });
    await waitFor(() => expect(result.current.isError).toBe(true));
    expect((result.current.error as ApiError).message).toBe(
      "vendor index down.",
    );
  });

  it("empty list (organization has no vendors yet) is a success path", async () => {
    server.use(http.get(url("/api/vendors"), () => jsonOk([])));
    const { Wrapper } = createTestWrapper();
    const { result } = renderHook(() => useVendors(), { wrapper: Wrapper });
    await waitFor(() => expect(result.current.isSuccess).toBe(true));
    expect(result.current.data).toEqual([]);
  });
});

describe("useVendor — detail query, `enabled` guard (#36)", () => {
  it("does NOT fetch when id is null (enabled: false)", async () => {
    let calls = 0;
    server.use(
      http.get(url("/api/vendors/:id"), () => {
        calls++;
        return jsonOk(VENDOR_DETAIL);
      }),
    );

    const { Wrapper } = createTestWrapper();
    const { result } = renderHook(() => useVendor(null), { wrapper: Wrapper });

    // With enabled: false, the query stays in idle — neither isPending
    // nor isSuccess. data is undefined and no network call fires.
    expect(result.current.fetchStatus).toBe("idle");
    expect(calls).toBe(0);
  });

  it("fetches when id is provided and surfaces the vendor with its portal links", async () => {
    server.use(
      http.get(url("/api/vendors/:id"), () => jsonOk(VENDOR_DETAIL)),
    );

    const { Wrapper } = createTestWrapper();
    const { result } = renderHook(() => useVendor("v_acme_01"), {
      wrapper: Wrapper,
    });

    await waitFor(() => expect(result.current.isSuccess).toBe(true));
    expect(result.current.data?.id).toBe("v_acme_01");
    expect(result.current.data?.portalLinks).toHaveLength(1);
    expect(result.current.data?.portalLinks[0].fullUrl).toBe(
      "http://example.test/portal/abc",
    );
  });
});

describe("useCreateVendor / useUpdateVendor / useDeleteVendor — cache invalidation (#36)", () => {
  let listCalls = 0;
  beforeEach(() => {
    listCalls = 0;
  });

  it("create invalidates ['vendors'] so the list re-reads", async () => {
    server.use(
      http.get(url("/api/vendors"), () => {
        listCalls++;
        return jsonOk(VENDORS);
      }),
      http.post(url("/api/vendors"), () => jsonOk({ id: "v_new_01" })),
    );

    const { Wrapper } = createTestWrapper();
    const list = renderHook(() => useVendors(), { wrapper: Wrapper });
    await waitFor(() => expect(list.result.current.isSuccess).toBe(true));
    expect(listCalls).toBe(1);

    const create = renderHook(() => useCreateVendor(), { wrapper: Wrapper });
    await create.result.current.mutateAsync({ name: "New Sub" });
    await waitFor(() => expect(listCalls).toBe(2));
  });

  it("update invalidates BOTH the list and the specific detail key", async () => {
    let detailCalls = 0;
    server.use(
      http.get(url("/api/vendors"), () => {
        listCalls++;
        return jsonOk(VENDORS);
      }),
      http.get(url("/api/vendors/:id"), () => {
        detailCalls++;
        return jsonOk(VENDOR_DETAIL);
      }),
      http.put(url("/api/vendors/:id"), () => jsonOk({ id: "v_acme_01" })),
    );

    const { Wrapper } = createTestWrapper();
    const list = renderHook(() => useVendors(), { wrapper: Wrapper });
    const detail = renderHook(() => useVendor("v_acme_01"), {
      wrapper: Wrapper,
    });
    await waitFor(() => {
      expect(list.result.current.isSuccess).toBe(true);
      expect(detail.result.current.isSuccess).toBe(true);
    });
    expect(listCalls).toBe(1);
    expect(detailCalls).toBe(1);

    const update = renderHook(() => useUpdateVendor("v_acme_01"), {
      wrapper: Wrapper,
    });
    await update.result.current.mutateAsync({ name: "Renamed" });

    // Pin both observers to EXACTLY 2 calls (initial fetch + one
    // refetch). detailCalls=2 is the #81 regression-detector: re-adding
    // an explicit invalidateQueries(['vendors', id]) on top of the
    // prefix invalidate would double-fire the detail refetch and break
    // this assertion. listCalls=2 is the structural cross-check — the
    // longer key ['vendors', id] doesn't prefix-match the list query
    // ['vendors'], so listCalls won't catch the #81 regression on its
    // own, but the exact pin keeps the contract symmetric.
    // (See useVendors.ts's useUpdateVendor for the TQ prefix-match
    // mechanics.)
    await waitFor(() => {
      expect(listCalls).toBe(2);
      expect(detailCalls).toBe(2);
    });
  });

  it("delete invalidates ['vendors']", async () => {
    server.use(
      http.get(url("/api/vendors"), () => {
        listCalls++;
        return jsonOk(VENDORS);
      }),
      http.delete(
        url("/api/vendors/:id"),
        () => new Response(null, { status: 204 }),
      ),
    );

    const { Wrapper } = createTestWrapper();
    const list = renderHook(() => useVendors(), { wrapper: Wrapper });
    await waitFor(() => expect(list.result.current.isSuccess).toBe(true));

    const del = renderHook(() => useDeleteVendor(), { wrapper: Wrapper });
    await del.result.current.mutateAsync("v_acme_01");
    await waitFor(() => expect(listCalls).toBe(2));
  });

  it("create also invalidates ['dashboard'] so the onboarding checklist ticks in place (#239)", async () => {
    // This is the wiring that makes the checklist tick WITHOUT a manual dismissal — a regression
    // dropping the dashboard invalidation must fail loudly here.
    let statsCalls = 0;
    server.use(
      http.get(url("/api/vendors"), () => jsonOk(VENDORS)),
      http.get(url("/api/dashboard/stats"), () => {
        statsCalls++;
        return jsonOk({ totalVendors: 0 });
      }),
      http.post(url("/api/vendors"), () => jsonOk({ id: "v_new_01" })),
    );

    const { Wrapper } = createTestWrapper();
    const stats = renderHook(() => useDashboardStats(), { wrapper: Wrapper });
    await waitFor(() => expect(stats.result.current.isSuccess).toBe(true));
    expect(statsCalls).toBe(1);

    const create = renderHook(() => useCreateVendor(), { wrapper: Wrapper });
    await create.result.current.mutateAsync({ name: "New Sub" });
    await waitFor(() => expect(statsCalls).toBe(2));
  });
});

describe("useGeneratePortalLink / useRevokePortalLink — prefix-invalidate refreshes both list + detail (#113)", () => {
  it("generate invalidates ['vendors'] so BOTH the list summary AND the detail observer re-read exactly once", async () => {
    // Mirrors the useUpdateVendor test shape (#81 prefix-match pattern):
    // the prefix `['vendors']` invalidate must hit BOTH the list query
    // (carries VendorSummary.activePortalLinks — the "X active" badge
    // on the vendors page) AND every detail observer (carries the
    // portalLinks array) with ONE invalidate call. Exact-2 pin on
    // BOTH observer counts catches:
    //   - the original #113 regression (narrow ['vendors', id]
    //     invalidate would leave listCalls at 1 = stale count),
    //   - and the symmetric over-invalidation regression (an extra
    //     explicit ['vendors', vendorId] invalidate on top would
    //     double-fire the detail refetch, breaking detailCalls=2).
    let listCalls = 0;
    let detailCalls = 0;
    server.use(
      http.get(url("/api/vendors"), () => {
        listCalls++;
        return jsonOk(VENDORS);
      }),
      http.get(url("/api/vendors/:id"), () => {
        detailCalls++;
        return jsonOk(VENDOR_DETAIL);
      }),
      http.post(url("/api/vendors/:id/portal-link"), () =>
        jsonOk({
          id: "pl_new",
          token: "newtoken",
          url: "http://example.test/portal/newtoken",
          maxUploads: 5,
        }),
      ),
    );

    const { Wrapper } = createTestWrapper();
    const list = renderHook(() => useVendors(), { wrapper: Wrapper });
    const detail = renderHook(() => useVendor("v_acme_01"), {
      wrapper: Wrapper,
    });
    await waitFor(() => {
      expect(list.result.current.isSuccess).toBe(true);
      expect(detail.result.current.isSuccess).toBe(true);
    });
    expect(listCalls).toBe(1);
    expect(detailCalls).toBe(1);

    const generate = renderHook(() => useGeneratePortalLink("v_acme_01"), {
      wrapper: Wrapper,
    });
    await generate.result.current.mutateAsync();

    await waitFor(() => {
      expect(listCalls).toBe(2);
      expect(detailCalls).toBe(2);
    });
  });

  it("revoke invalidates ['vendors'] so BOTH the list summary AND the detail observer re-read exactly once", async () => {
    // Symmetric assertion with the generate test above — same exact-2
    // pins on both observers. A regression that reverted the
    // useRevokePortalLink fix to the narrower ['vendors', vendorId]
    // would leave listCalls at 1 (stale activePortalLinks count) and
    // fail this test loudly.
    let listCalls = 0;
    let detailCalls = 0;
    server.use(
      http.get(url("/api/vendors"), () => {
        listCalls++;
        return jsonOk(VENDORS);
      }),
      http.get(url("/api/vendors/:id"), () => {
        detailCalls++;
        return jsonOk(VENDOR_DETAIL);
      }),
      http.delete(
        url("/api/vendors/:id/portal-link/:linkId"),
        () => new Response(null, { status: 204 }),
      ),
    );

    const { Wrapper } = createTestWrapper();
    const list = renderHook(() => useVendors(), { wrapper: Wrapper });
    const detail = renderHook(() => useVendor("v_acme_01"), {
      wrapper: Wrapper,
    });
    await waitFor(() => {
      expect(list.result.current.isSuccess).toBe(true);
      expect(detail.result.current.isSuccess).toBe(true);
    });
    expect(listCalls).toBe(1);
    expect(detailCalls).toBe(1);

    const revoke = renderHook(() => useRevokePortalLink("v_acme_01"), {
      wrapper: Wrapper,
    });
    await revoke.result.current.mutateAsync("pl_01");

    await waitFor(() => {
      expect(listCalls).toBe(2);
      expect(detailCalls).toBe(2);
    });
  });
});
