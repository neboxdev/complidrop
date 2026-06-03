"use client";

import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { api } from "@/lib/api";

export type VendorSummary = {
  id: string;
  name: string;
  contactEmail: string | null;
  contactPhone: string | null;
  category: string | null;
  complianceTemplateId: string | null;
  complianceTemplateName: string | null;
  documentCount: number;
  activePortalLinks: number;
};

export type PortalLink = {
  id: string;
  token: string;
  fullUrl: string;
  isActive: boolean;
  uploadCount: number;
  maxUploads: number;
  expiresAt: string | null;
  createdAt: string;
};

export type VendorDetail = {
  id: string;
  name: string;
  contactEmail: string | null;
  contactPhone: string | null;
  category: string | null;
  complianceTemplateId: string | null;
  complianceTemplateName: string | null;
  portalLinks: PortalLink[];
  createdAt: string;
  updatedAt: string;
};

export type VendorUpsert = {
  name: string;
  contactEmail?: string | null;
  contactPhone?: string | null;
  category?: string | null;
  complianceTemplateId?: string | null;
};

export function useVendors() {
  return useQuery<VendorSummary[]>({
    queryKey: ["vendors"],
    queryFn: () => api.get<VendorSummary[]>("/api/vendors"),
  });
}

export function useVendor(id: string | null) {
  return useQuery<VendorDetail>({
    queryKey: ["vendors", id],
    queryFn: () => api.get<VendorDetail>(`/api/vendors/${id}`),
    enabled: !!id,
  });
}

export function useCreateVendor() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (payload: VendorUpsert) => api.post<{ id: string }>("/api/vendors", payload),
    onSuccess: () => qc.invalidateQueries({ queryKey: ["vendors"] }),
  });
}

export function useUpdateVendor(id: string) {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (payload: VendorUpsert) => api.put<{ id: string }>(`/api/vendors/${id}`, payload),
    // invalidateQueries(['vendors']) prefix-matches BOTH the list query
    // (['vendors']) AND every detail observer (['vendors', :id]) —
    // TanStack Query's default filter uses prefix matching (exact: false),
    // so a single invalidate fans out to every key starting with
    // ['vendors']. Adding an explicit invalidateQueries(['vendors', id])
    // on top would cause the detail observer to refetch twice per save
    // (#81).
    onSuccess: () => qc.invalidateQueries({ queryKey: ["vendors"] }),
  });
}

export function useDeleteVendor() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (id: string) => api.delete<void>(`/api/vendors/${id}`),
    onSuccess: () => qc.invalidateQueries({ queryKey: ["vendors"] }),
    // Callers don't wrap this in a local error handler — opt into the global
    // mutation-error toast (lib/query-client.ts) so a failed delete surfaces
    // instead of silently leaving the vendor in the list.
    meta: { errorToast: true },
  });
}

export function useGeneratePortalLink(vendorId: string) {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: () => api.post<{ id: string; token: string; url: string; maxUploads: number }>(
      `/api/vendors/${vendorId}/portal-link`,
    ),
    // Prefix-match `['vendors']` so BOTH the list query (the
    // VendorSummary.activePortalLinks count rendered as the "X active"
    // badge on vendors/page.tsx) AND every detail observer refetch
    // with one invalidate. The narrower `['vendors', vendorId]` only
    // hits the detail observer and leaves the list count stale until
    // a manual refresh — the freshness gap #113 was filed against.
    // See useUpdateVendor above for the TQ prefix-match mechanics.
    onSuccess: () => qc.invalidateQueries({ queryKey: ["vendors"] }),
  });
}

export function useRevokePortalLink(vendorId: string) {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (linkId: string) => api.delete<void>(`/api/vendors/${vendorId}/portal-link/${linkId}`),
    // Symmetric with useGeneratePortalLink above — one prefix
    // invalidate refreshes BOTH the list-summary activePortalLinks
    // count and the detail-page portal-link list (#113). See
    // useUpdateVendor above for the TQ prefix-match mechanics.
    onSuccess: () => qc.invalidateQueries({ queryKey: ["vendors"] }),
    // The revoke button (vendors/[id]) calls `revoke.mutate(...)` with no
    // local error handler — opt into the global mutation-error toast so a
    // failed revoke isn't silently lost. See lib/query-client.ts.
    meta: { errorToast: true },
  });
}
