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
    queryFn: () => api.get<DashboardStats>("/api/dashboard/stats"),
    staleTime: 30_000,
  });
}

export function useExpiryPipeline() {
  return useQuery<ExpiryPipeline>({
    queryKey: ["dashboard", "expiry"],
    queryFn: () => api.get<ExpiryPipeline>("/api/dashboard/expiry-pipeline"),
    staleTime: 30_000,
  });
}

export function useRecentActivity() {
  return useQuery<ActivityEntry[]>({
    queryKey: ["dashboard", "activity"],
    queryFn: () => api.get<ActivityEntry[]>("/api/dashboard/recent-activity"),
    staleTime: 30_000,
  });
}
