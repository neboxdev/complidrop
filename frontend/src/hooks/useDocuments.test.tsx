/**
 * useDocuments — query state + polling-interval contract (#36).
 *
 * The dashboard's "automatic" promise hangs off this hook. Three things
 * must hold (and are pinned here):
 *
 *   1. Pending/Processing rows drive a 5-second refetchInterval so the
 *      list updates without a manual reload.
 *   2. Once every item is Completed (or Failed), the interval STOPS —
 *      the hook returns `false` for `refetchInterval`, so the dashboard
 *      doesn't hammer the API forever.
 *   3. Mutations (upload, delete) invalidate the cache so the next read
 *      sees the new list.
 *
 * Driven through MSW + the real api client. Fake timers advance the
 * 5-second poll window deterministically.
 */
import { describe, it, expect, vi, beforeEach, afterEach } from "vitest";
import { http } from "msw";
import { renderHook, waitFor } from "@testing-library/react";
import {
  useDocuments,
  useUploadDocument,
  useDeleteDocument,
  useUpdateDocument,
} from "./useDocuments";
import {
  createTestWrapper,
  server,
  url,
  jsonOk,
  jsonError,
  documentsAllStatuses,
  makeDocument,
  makeDocumentsResponse,
  sequencedJsonOk,
} from "@/test";
import { ApiError } from "@/lib/api";

// Analytics module is imported by useUploadDocument's onSuccess.
const { track } = vi.hoisted(() => ({ track: vi.fn() }));
vi.mock("@/lib/analytics", () => ({
  identify: vi.fn(),
  resetIdentity: vi.fn(),
  track,
}));

describe("useDocuments — basic query states (#36)", () => {
  it("isPending → isSuccess on 200, surfaces the items array", async () => {
    const response = makeDocumentsResponse({
      items: [{ ...documentsAllStatuses[2] }], // completed-only
      total: 1,
    });
    server.use(http.get(url("/api/documents"), () => jsonOk(response)));

    const { Wrapper } = createTestWrapper();
    const { result } = renderHook(() => useDocuments(), { wrapper: Wrapper });

    expect(result.current.isPending).toBe(true);
    expect(result.current.data).toBeUndefined();

    await waitFor(() => expect(result.current.isSuccess).toBe(true));
    expect(result.current.data?.items).toHaveLength(1);
    expect(result.current.data?.items[0].extractionStatus).toBe("Completed");
  });

  it("isError on 500 with the server's human message", async () => {
    server.use(
      http.get(url("/api/documents"), () =>
        jsonError("server.error", "DB is on fire.", { status: 500 }),
      ),
    );

    const { Wrapper } = createTestWrapper();
    const { result } = renderHook(() => useDocuments(), { wrapper: Wrapper });

    await waitFor(() => expect(result.current.isError).toBe(true));
    expect(result.current.error).toBeInstanceOf(ApiError);
    expect((result.current.error as ApiError).message).toBe("DB is on fire.");
  });
});

describe("useDocuments — list params → query string (#187)", () => {
  it("threads page/pageSize/search/status/type/expiresWithin into the query string", async () => {
    let requestedUrl = "";
    server.use(
      http.get(url("/api/documents"), ({ request }) => {
        requestedUrl = request.url;
        return jsonOk(makeDocumentsResponse({ items: [], total: 0 }));
      }),
    );

    const { Wrapper } = createTestWrapper();
    const { result } = renderHook(
      () =>
        useDocuments({
          page: 2,
          pageSize: 10,
          search: "acme",
          status: "Compliant",
          type: "coi",
          vendorId: "v_acme_01",
          expiresWithin: 30,
        }),
      { wrapper: Wrapper },
    );

    await waitFor(() => expect(result.current.isSuccess).toBe(true));
    const sp = new URL(requestedUrl).searchParams;
    expect(sp.get("page")).toBe("2");
    expect(sp.get("pageSize")).toBe("10");
    expect(sp.get("search")).toBe("acme");
    expect(sp.get("status")).toBe("Compliant");
    expect(sp.get("type")).toBe("coi");
    expect(sp.get("vendorId")).toBe("v_acme_01");
    expect(sp.get("expiresWithin")).toBe("30");
  });

  it("the no-arg call sends no query params (preserves the original behavior)", async () => {
    let requestedUrl = "";
    server.use(
      http.get(url("/api/documents"), ({ request }) => {
        requestedUrl = request.url;
        return jsonOk(makeDocumentsResponse({ items: [], total: 0 }));
      }),
    );

    const { Wrapper } = createTestWrapper();
    const { result } = renderHook(() => useDocuments(), { wrapper: Wrapper });

    await waitFor(() => expect(result.current.isSuccess).toBe(true));
    expect(new URL(requestedUrl).search).toBe("");
  });
});

describe("useDocuments — 5-second polling while any row is Pending/Processing (#36)", () => {
  beforeEach(() => {
    // `shouldAdvanceTime: true` is REQUIRED because RTL's `waitFor`
    // polls via real `setTimeout`, which is itself faked here — pure
    // fake timers cause `waitFor` to hang. The race risk (real ms
    // elapsed during waitFor potentially crossing the 5-second
    // boundary) is absorbed by delta-based assertions on `calls`.
    vi.useFakeTimers({ shouldAdvanceTime: true });
  });
  afterEach(() => {
    vi.useRealTimers();
  });

  it("polls every 5s while a Processing row exists, STOPS once everything is Completed", async () => {
    // Sequence the responses by call count: first read returns a
    // Processing row, second returns Completed. The refetchInterval
    // option polls every 5s while any row matches the predicate.
    let calls = 0;
    const processingRow = makeDocument({
      id: "d_p_01",
      extractionStatus: "Processing",
      complianceStatus: "Pending",
      expirationDate: null,
      effectiveDate: null,
      daysUntilExpiry: null,
      extractionConfidence: null,
    });
    const completedRow = makeDocument({
      id: "d_p_01",
      extractionStatus: "Completed",
      complianceStatus: "Compliant",
    });
    const seq = sequencedJsonOk(
      makeDocumentsResponse({ items: [processingRow], total: 1 }),
      makeDocumentsResponse({ items: [completedRow], total: 1 }),
    );
    server.use(
      http.get(url("/api/documents"), () => {
        calls++;
        return seq();
      }),
    );

    const { Wrapper } = createTestWrapper();
    const { result } = renderHook(() => useDocuments(), { wrapper: Wrapper });

    // First fetch: Processing.
    await waitFor(() =>
      expect(result.current.data?.items[0].extractionStatus).toBe("Processing"),
    );
    expect(calls).toBeGreaterThanOrEqual(1);

    // Snapshot pre-advance count. The explicit 5-second advance must
    // trigger at least one new refetch; delta-based so an auto-fire
    // absorbed during `waitFor` (from `shouldAdvanceTime: true`) doesn't
    // break the assertion on slower CIs.
    const beforeAdvance = calls;
    await vi.advanceTimersByTimeAsync(5000);

    await waitFor(() =>
      expect(result.current.data?.items[0].extractionStatus).toBe("Completed"),
    );
    expect(calls).toBeGreaterThanOrEqual(beforeAdvance + 1);

    // Now every row is Completed → predicate returns false → interval
    // stops. Advance 15 seconds to confirm no further fetches.
    const afterCompleted = calls;
    await vi.advanceTimersByTimeAsync(15_000);
    expect(calls).toBe(afterCompleted);
  });

  it("terminal-only initial state: NO polling fires (steady-state for any healthy account)", async () => {
    // Production's most common case: every document is already Completed.
    // refetchInterval predicate evaluates `items.some(...)` to false on
    // first read, so no interval is ever scheduled. A regression that
    // flipped the predicate polarity would still pass the Pending →
    // Completed test because that test STARTS in Pending; only this
    // test catches "polls when nothing is pending".
    let calls = 0;
    server.use(
      http.get(url("/api/documents"), () => {
        calls++;
        return jsonOk(
          makeDocumentsResponse({
            items: [{ ...documentsAllStatuses[2] }], // Completed only
            total: 1,
          }),
        );
      }),
    );

    const { Wrapper } = createTestWrapper();
    const { result } = renderHook(() => useDocuments(), { wrapper: Wrapper });

    await waitFor(() => expect(result.current.isSuccess).toBe(true));
    expect(calls).toBe(1);

    // Advance 20 seconds — four polling windows would have fired if the
    // predicate were inverted. The terminal-only branch returns false,
    // so no interval is ever scheduled and calls stays at 1.
    const afterSettle = calls;
    await vi.advanceTimersByTimeAsync(20_000);
    expect(calls).toBe(afterSettle);
  });

  it("Pending → Failed transition still stops the interval (Failed is terminal)", async () => {
    let calls = 0;
    const pending = makeDocument({
      id: "d_p_02",
      extractionStatus: "Pending",
      complianceStatus: "Pending",
      effectiveDate: null,
      expirationDate: null,
      daysUntilExpiry: null,
      extractionConfidence: null,
    });
    const failed = makeDocument({
      id: "d_p_02",
      extractionStatus: "Failed",
      complianceStatus: "Pending",
      effectiveDate: null,
      expirationDate: null,
      daysUntilExpiry: null,
      extractionConfidence: null,
    });
    const seq = sequencedJsonOk(
      makeDocumentsResponse({ items: [pending], total: 1 }),
      makeDocumentsResponse({ items: [failed], total: 1 }),
    );
    server.use(
      http.get(url("/api/documents"), () => {
        calls++;
        return seq();
      }),
    );

    const { Wrapper } = createTestWrapper();
    const { result } = renderHook(() => useDocuments(), { wrapper: Wrapper });

    await waitFor(() =>
      expect(result.current.data?.items[0].extractionStatus).toBe("Pending"),
    );
    const beforeAdvance = calls;
    await vi.advanceTimersByTimeAsync(5000);
    await waitFor(() =>
      expect(result.current.data?.items[0].extractionStatus).toBe("Failed"),
    );
    expect(calls).toBeGreaterThanOrEqual(beforeAdvance + 1);

    // Failed is NOT Pending/Processing → predicate returns false → no
    // more polling. Confirm by advancing a long way without new calls.
    const afterFailed = calls;
    await vi.advanceTimersByTimeAsync(20_000);
    expect(calls).toBe(afterFailed);
  });
});

describe("useUploadDocument / useDeleteDocument — cache invalidation (#36)", () => {
  it("upload onSuccess invalidates ['documents'] so the next list read refetches", async () => {
    // Seed the cache with a stable initial fetch, then fire upload, then
    // confirm the next list read re-fetches (call count goes up).
    let listCalls = 0;
    server.use(
      http.get(url("/api/documents"), () => {
        listCalls++;
        return jsonOk(
          makeDocumentsResponse({
            items: [{ ...documentsAllStatuses[2] }],
            total: 1,
          }),
        );
      }),
      http.post(url("/api/documents/upload"), () =>
        jsonOk({
          id: "d_new_01",
          originalFileName: "new.pdf",
          extractionStatus: "Pending",
        }),
      ),
    );

    const { Wrapper } = createTestWrapper();
    const docs = renderHook(() => useDocuments(), { wrapper: Wrapper });
    await waitFor(() => expect(docs.result.current.isSuccess).toBe(true));
    expect(listCalls).toBe(1);

    // Mount the upload hook in the SAME wrapper so they share a QueryClient.
    const upload = renderHook(() => useUploadDocument(), { wrapper: Wrapper });
    await upload.result.current.mutateAsync({
      file: new File(["x"], "new.pdf", { type: "application/pdf" }),
    });

    // Invalidation re-triggers the list query — call count increments.
    await waitFor(() => expect(listCalls).toBe(2));
    expect(track).toHaveBeenCalledWith(
      "document.uploaded",
      expect.objectContaining({ documentId: "d_new_01" }),
    );
  });

  it("delete onSuccess invalidates ['documents'] so the next list read refetches", async () => {
    let listCalls = 0;
    server.use(
      http.get(url("/api/documents"), () => {
        listCalls++;
        return jsonOk(
          makeDocumentsResponse({
            items: [{ ...documentsAllStatuses[2] }],
            total: 1,
          }),
        );
      }),
      http.delete(url("/api/documents/:id"), () => new Response(null, { status: 204 })),
    );

    const { Wrapper } = createTestWrapper();
    const docs = renderHook(() => useDocuments(), { wrapper: Wrapper });
    await waitFor(() => expect(docs.result.current.isSuccess).toBe(true));
    expect(listCalls).toBe(1);

    const del = renderHook(() => useDeleteDocument(), { wrapper: Wrapper });
    await del.result.current.mutateAsync("d_completed_01");

    await waitFor(() => expect(listCalls).toBe(2));
  });

  it("update onSuccess invalidates ['documents'] and sends ONLY the provided keys (partial PATCH)", async () => {
    let listCalls = 0;
    let patchedBody: unknown = null;
    server.use(
      http.get(url("/api/documents"), () => {
        listCalls++;
        return jsonOk(
          makeDocumentsResponse({
            items: [{ ...documentsAllStatuses[2] }],
            total: 1,
          }),
        );
      }),
      http.patch(url("/api/documents/:id"), async ({ request }) => {
        patchedBody = await request.json();
        return jsonOk({ message: "Document updated." });
      }),
    );

    const { Wrapper } = createTestWrapper();
    const docs = renderHook(() => useDocuments(), { wrapper: Wrapper });
    await waitFor(() => expect(docs.result.current.isSuccess).toBe(true));
    expect(listCalls).toBe(1);

    const update = renderHook(() => useUpdateDocument(), { wrapper: Wrapper });
    // Assigning a vendor must NOT also send documentType — the partial payload
    // is what keeps the PATCH from nulling the type server-side.
    await update.result.current.mutateAsync({ id: "d_completed_01", vendorId: "v_x" });

    await waitFor(() => expect(listCalls).toBe(2));
    expect(patchedBody).toEqual({ vendorId: "v_x" });
  });
});
