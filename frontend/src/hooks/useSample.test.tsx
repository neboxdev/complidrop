/**
 * useSample — the sample-certificate demo mutations (#238):
 *   - useSeedSample  → POST   /api/sample  (returns the seeded document id)
 *   - useClearSample → DELETE /api/sample
 *
 * Both invalidate the documents / dashboard / vendors caches so the sample's
 * flags appear (and disappear) everywhere at once.
 */
import { describe, it, expect, vi } from "vitest";
import { http } from "msw";
import { renderHook, waitFor } from "@testing-library/react";
import { createTestWrapper, server, url, jsonOk, jsonError } from "@/test";
import { ApiError } from "@/lib/api";
import { useSeedSample, useClearSample } from "./useSample";

vi.mock("@/lib/analytics", () => ({
  identify: vi.fn(),
  resetIdentity: vi.fn(),
  track: vi.fn(),
}));

function invalidatedPrefixes(spy: ReturnType<typeof vi.spyOn>): unknown[] {
  return spy.mock.calls.map((c) => (c[0] as { queryKey: unknown[] }).queryKey[0]);
}

describe("useSeedSample (#238)", () => {
  it("posts to /api/sample and returns the seeded document id", async () => {
    server.use(http.post(url("/api/sample"), () => jsonOk({ documentId: "d_sample_01", vendorId: "v_sample_01" })));
    const { qc, Wrapper } = createTestWrapper();
    const invalidate = vi.spyOn(qc, "invalidateQueries");

    const { result } = renderHook(() => useSeedSample(), { wrapper: Wrapper });
    result.current.mutate();

    await waitFor(() => expect(result.current.isSuccess).toBe(true));
    expect(result.current.data).toEqual({ documentId: "d_sample_01", vendorId: "v_sample_01" });
    expect(invalidatedPrefixes(invalidate)).toEqual(
      expect.arrayContaining(["documents", "dashboard", "vendors"]),
    );
  });

  it("surfaces the server's friendly message on a storage outage", async () => {
    server.use(
      http.post(url("/api/sample"), () =>
        jsonError("storage.unavailable", "We couldn't set up the sample just now.", { status: 503 }),
      ),
    );
    const { Wrapper } = createTestWrapper();
    const { result } = renderHook(() => useSeedSample(), { wrapper: Wrapper });
    result.current.mutate();

    await waitFor(() => expect(result.current.isError).toBe(true));
    expect((result.current.error as ApiError).message).toBe("We couldn't set up the sample just now.");
  });
});

describe("useClearSample (#238)", () => {
  it("deletes /api/sample and invalidates documents/dashboard/vendors", async () => {
    server.use(
      http.delete(url("/api/sample"), () =>
        jsonOk({ message: "Sample data cleared.", clearedDocuments: 1, clearedVendors: 1 }),
      ),
    );
    const { qc, Wrapper } = createTestWrapper();
    const invalidate = vi.spyOn(qc, "invalidateQueries");

    const { result } = renderHook(() => useClearSample(), { wrapper: Wrapper });
    result.current.mutate();

    await waitFor(() => expect(result.current.isSuccess).toBe(true));
    expect(invalidatedPrefixes(invalidate)).toEqual(
      expect.arrayContaining(["documents", "dashboard", "vendors"]),
    );
  });
});
