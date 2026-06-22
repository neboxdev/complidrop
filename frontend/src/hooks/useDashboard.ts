"use client";

import { useQuery } from "@tanstack/react-query";
import { api } from "@/lib/api";

export type DashboardStats = {
  totalDocuments: number;
  compliant: number;
  nonCompliant: number;
  expiringSoon: number;
  expired: number;
  pendingExtraction: number;
  totalVendors: number;
  /** True when at least one vendor has a requirement checklist assigned (#191
   * "Get started" checklist step 2) — derived server-side so the dashboard
   * doesn't have to pull the full vendor list. */
  anyVendorWithRequirements: boolean;
  /** True when the org has at least one ACTIVE vendor portal upload link (#239 delta 3) —
   * drives the checklist's "Link sent — waiting for their upload" state so the funnel
   * doesn't go quiet while waiting on a vendor. */
  anyActivePortalLink: boolean;
  /** True when the org has live sample-demo data (#238) — drives the dashboard's
   * "Try a sample certificate" CTA vs. the "Clear sample data" banner. */
  hasSampleData: boolean;
  /** The sample document to deep-link to ("View sample"), or null when none exists. */
  sampleDocumentId: string | null;
  complianceRate: number;
};

export type ExpiryPipeline = {
  expired: number;
  bucket30: number;
  bucket60: number;
  bucket90: number;
  beyond: number;
};

export type ActivityEntry = {
  id: string;
  action: string;
  entityType: string;
  entityId: string | null;
  createdAt: string;
};

export function useDashboardStats() {
  return useQuery<DashboardStats>({
    queryKey: ["dashboard", "stats"],
    queryFn: ({ signal }) => api.get<DashboardStats>("/api/dashboard/stats", { signal }),
    staleTime: 30_000,
    // Poll ONLY while a document is still being read, so the "Still being read: N"
    // tile (and the headline counts behind it) unfreeze when extraction completes —
    // but an idle dashboard stays silent (#318 FP-049). One tick is a heavy ~14-count
    // query, so we gate on the last-seen pendingExtraction and never poll in error
    // (mirrors the conditional pattern in useDocuments).
    refetchInterval: (q) => {
      if (q.state.status === "error") return false;
      return (q.state.data?.pendingExtraction ?? 0) > 0 ? 15_000 : false;
    },
  });
}

export function useExpiryPipeline() {
  return useQuery<ExpiryPipeline>({
    queryKey: ["dashboard", "expiry"],
    queryFn: ({ signal }) => api.get<ExpiryPipeline>("/api/dashboard/expiry-pipeline", { signal }),
    staleTime: 30_000,
  });
}

export function useRecentActivity() {
  return useQuery<ActivityEntry[]>({
    queryKey: ["dashboard", "activity"],
    queryFn: ({ signal }) => api.get<ActivityEntry[]>("/api/dashboard/recent-activity", { signal }),
    staleTime: 30_000,
  });
}
