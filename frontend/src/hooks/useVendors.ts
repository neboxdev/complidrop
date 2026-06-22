"use client";

import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { api } from "@/lib/api";

/** Per-vendor coverage rollup (#319 FP-074) — computed server-side in ListVendors. */
export type VendorCoverage = {
  /** "NoRequirements" | "Missing" | "ActionNeeded" | "Covered". */
  status: "NoRequirements" | "Missing" | "ActionNeeded" | "Covered";
  /** Short type nouns ("insurance", "license") for the "Missing: …" label; only set when Missing. */
  missingTypes: string[];
};

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
  /** True for the demo's sample vendor (#238) so the vendors list can badge it "Sample". */
  isSample: boolean;
  coverage: VendorCoverage;
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
  coverage: VendorCoverage;
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
    queryFn: ({ signal }) => api.get<VendorSummary[]>("/api/vendors", { signal }),
  });
}

export function useVendor(id: string | null) {
  return useQuery<VendorDetail>({
    queryKey: ["vendors", id],
    queryFn: ({ signal }) => api.get<VendorDetail>(`/api/vendors/${id}`, { signal }),
    enabled: !!id,
  });
}

export function useCreateVendor() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (payload: VendorUpsert) => api.post<{ id: string }>("/api/vendors", payload),
    onSuccess: () => {
      qc.invalidateQueries({ queryKey: ["vendors"] });
      qc.invalidateQueries({ queryKey: ["dashboard"] }); // tick the onboarding checklist in place (#239)
    },
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
    onSuccess: () => {
      qc.invalidateQueries({ queryKey: ["vendors"] });
      qc.invalidateQueries({ queryKey: ["dashboard"] }); // tick the onboarding checklist in place (#239)
    },
  });
}

export function useDeleteVendor() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (id: string) => api.delete<void>(`/api/vendors/${id}`),
    onSuccess: () => {
      qc.invalidateQueries({ queryKey: ["vendors"] });
      qc.invalidateQueries({ queryKey: ["dashboard"] }); // tick the onboarding checklist in place (#239)
    },
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
    onSuccess: () => {
      qc.invalidateQueries({ queryKey: ["vendors"] });
      qc.invalidateQueries({ queryKey: ["dashboard"] }); // tick the onboarding checklist in place (#239)
    },
  });
}

export function useEmailPortalLink(vendorId: string) {
  // Emails an existing portal link to the vendor's captured contact email (#190).
  // No cache invalidation: sending a link doesn't change any list/detail state
  // (it neither creates nor revokes a link). Callers chain this after
  // useGeneratePortalLink to "generate + email in one click".
  return useMutation({
    mutationFn: (linkId: string) =>
      api.post<{ sentTo: string }>(`/api/vendors/${vendorId}/portal-link/${linkId}/email`),
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
    onSuccess: () => {
      qc.invalidateQueries({ queryKey: ["vendors"] });
      qc.invalidateQueries({ queryKey: ["dashboard"] }); // tick the onboarding checklist in place (#239)
    },
    // The revoke button (vendors/[id]) calls `revoke.mutate(...)` with no
    // local error handler — opt into the global mutation-error toast so a
    // failed revoke isn't silently lost. See lib/query-client.ts.
    meta: { errorToast: true },
  });
}
