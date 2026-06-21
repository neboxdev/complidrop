"use client";

import { useMutation, useQueryClient } from "@tanstack/react-query";
import { api } from "@/lib/api";
import { track } from "@/lib/analytics";

/** Response of POST /api/sample — the seeded (or already-existing) sample document. */
export type SeedSampleResult = {
  documentId: string;
  vendorId: string | null;
};

/**
 * Seeds the one-click sample-certificate demo (#238): a sample vendor + a generated COI run
 * through the real extraction pipeline. Idempotent server-side (one sample per org), but each
 * click still mints a fresh Idempotency-Key so a network retry replays rather than re-seeds.
 * Invalidates documents / dashboard / vendors so the new sample + its flags appear everywhere.
 */
export function useSeedSample() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: () =>
      api.post<SeedSampleResult>("/api/sample", undefined, { idempotencyKey: crypto.randomUUID() }),
    onSuccess: (res) => {
      track("sample.seeded", { documentId: res.documentId });
      qc.invalidateQueries({ queryKey: ["documents"] });
      qc.invalidateQueries({ queryKey: ["dashboard"] });
      qc.invalidateQueries({ queryKey: ["vendors"] });
    },
  });
}

/** Removes all sample-demo artifacts for the org (#238) — soft-deletes the sample doc + vendor and
 * deletes the blob. Idempotent (a no-op when nothing is seeded). */
export function useClearSample() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: () =>
      api.delete<{ message: string; clearedDocuments: number; clearedVendors: number }>("/api/sample"),
    onSuccess: () => {
      track("sample.cleared");
      qc.invalidateQueries({ queryKey: ["documents"] });
      qc.invalidateQueries({ queryKey: ["dashboard"] });
      qc.invalidateQueries({ queryKey: ["vendors"] });
    },
  });
}
