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
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import type { ReactNode } from "react";
import {
  useDocuments,
  useUploadDocument,
  useDeleteDocument,
} from "./useDocuments";
import {
  server,
  url,
  jsonOk,
  jsonError,
  documentsAllStatuses,
  makeDocument,
  makeDocumentsResponse,
} from "@/test";
import { ApiError } from "@/lib/api";

// Analytics module is imported by useUploadDocument's onSuccess.
const { track } = vi.hoisted(() => ({ track: vi.fn() }));
vi.mock("@/lib/analytics", () => ({
  identify: vi.fn(),
  resetIdentity: vi.fn(),
  track,
}));

function makeWrapper() {
  // gcTime: Infinity so cache writes from mutations survive long enough
  // for assertions to read them (mirrors the harness's main client; see
  // src/test/render.tsx + README for the rationale).
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

describe("useDocuments — basic query states (#36)", () => {
  it("isPending → isSuccess on 200, surfaces the items array", async () => {
    const response = makeDocumentsResponse({
      items: [{ ...documentsAllStatuses[2] }], // completed-only
      total: 1,
    });
    server.use(http.get(url("/api/documents"), () => jsonOk(response)));

    const { Wrapper } = makeWrapper();
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

    const { Wrapper } = makeWrapper();
    const { result } = renderHook(() => useDocuments(), { wrapper: Wrapper });

    await waitFor(() => expect(result.current.isError).toBe(true));
    expect(result.current.error).toBeInstanceOf(ApiError);
    expect((result.current.error as ApiError).message).toBe("DB is on fire.");
  });
});

describe("useDocuments — 5-second polling while any row is Pending/Processing (#36)", () => {
  beforeEach(() => {
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
    server.use(
      http.get(url("/api/documents"), () => {
        calls++;
        const item = calls === 1 ? processingRow : completedRow;
        return jsonOk(makeDocumentsResponse({ items: [item], total: 1 }));
      }),
    );

    const { Wrapper } = makeWrapper();
    const { result } = renderHook(() => useDocuments(), { wrapper: Wrapper });

    // First fetch: Processing.
    await waitFor(() =>
      expect(result.current.data?.items[0].extractionStatus).toBe("Processing"),
    );
    expect(calls).toBe(1);

    // Advance 5s — the refetchInterval predicate sees Processing in the
    // CURRENT data and schedules another fetch.
    await vi.advanceTimersByTimeAsync(5000);

    await waitFor(() =>
      expect(result.current.data?.items[0].extractionStatus).toBe("Completed"),
    );
    expect(calls).toBe(2);

    // Now every row is Completed → predicate returns false → interval
    // stops. Advance 15 seconds to confirm no further fetches.
    await vi.advanceTimersByTimeAsync(15_000);
    expect(calls).toBe(2);
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
    server.use(
      http.get(url("/api/documents"), () => {
        calls++;
        return jsonOk(
          makeDocumentsResponse({
            items: [calls === 1 ? pending : failed],
            total: 1,
          }),
        );
      }),
    );

    const { Wrapper } = makeWrapper();
    const { result } = renderHook(() => useDocuments(), { wrapper: Wrapper });

    await waitFor(() =>
      expect(result.current.data?.items[0].extractionStatus).toBe("Pending"),
    );
    await vi.advanceTimersByTimeAsync(5000);
    await waitFor(() =>
      expect(result.current.data?.items[0].extractionStatus).toBe("Failed"),
    );

    // Failed is NOT Pending/Processing → predicate returns false → no
    // more polling. Confirm by advancing a long way without new calls.
    await vi.advanceTimersByTimeAsync(20_000);
    expect(calls).toBe(2);
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

    const { Wrapper } = makeWrapper();
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

    const { Wrapper } = makeWrapper();
    const docs = renderHook(() => useDocuments(), { wrapper: Wrapper });
    await waitFor(() => expect(docs.result.current.isSuccess).toBe(true));
    expect(listCalls).toBe(1);

    const del = renderHook(() => useDeleteDocument(), { wrapper: Wrapper });
    await del.result.current.mutateAsync("d_completed_01");

    await waitFor(() => expect(listCalls).toBe(2));
  });
});
