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
