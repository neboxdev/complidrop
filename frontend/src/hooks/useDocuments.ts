"use client";

import {
  keepPreviousData,
  useMutation,
  useQuery,
  useQueryClient,
} from "@tanstack/react-query";
import { api, ApiError } from "@/lib/api";
import { track } from "@/lib/analytics";

/**
 * Server-side list controls for the documents query. Every field is optional so
 * `useDocuments()` (no args) keeps its original "first page, no filters" behavior
 * — the backend defaults page=1/pageSize=25. (#187)
 */
export type DocumentListParams = {
  page?: number;
  pageSize?: number;
  search?: string;
  status?: string;
  type?: string;
  vendorId?: string;
  expiresWithin?: number;
};

function buildDocumentsQuery(params: DocumentListParams): string {
  const sp = new URLSearchParams();
  if (params.page != null) sp.set("page", String(params.page));
  if (params.pageSize != null) sp.set("pageSize", String(params.pageSize));
  if (params.search?.trim()) sp.set("search", params.search.trim());
  if (params.status) sp.set("status", params.status);
  if (params.type) sp.set("type", params.type);
  if (params.vendorId) sp.set("vendorId", params.vendorId);
  if (params.expiresWithin != null) sp.set("expiresWithin", String(params.expiresWithin));
  const qs = sp.toString();
  return qs ? `?${qs}` : "";
}

export type DocumentListItem = {
  id: string;
  originalFileName: string;
  documentType: string;
  vendorName: string | null;
  vendorId: string | null;
  extractionStatus: string;
  extractionConfidence: number | null;
  complianceStatus: string;
  effectiveDate: string | null;
  expirationDate: string | null;
  daysUntilExpiry: number | null;
  /** True for the one-click sample-demo document (#238) so the list can badge it "Sample". */
  isSample: boolean;
  createdAt: string;
};

export type DocumentListResponse = {
  // `readonly` so the response shape is immutable at the type level —
  // consumers (and test fixtures) can't accidentally `items.push(...)` or
  // mutate an entry. The page-level code only reads from items, so this
  // doesn't constrain any real call site.
  readonly items: readonly DocumentListItem[];
  total: number;
  page: number;
  pageSize: number;
};

export function useDocuments(params: DocumentListParams = {}) {
  // Tightened TError to ApiError so consumers (page error UIs, the
  // StaleDataBanner) can access `.status` / `.code` / `.correlationId`
  // off the error object without an `instanceof ApiError` guard. The
  // api.ts request() funnel ALWAYS throws ApiError on a non-ok response
  // (envelope-parsed or generic-fallback), so the runtime type matches.
  // `Error | null` is the TanStack default; the narrower type catches a
  // future regression that swapped a request through a path that throws
  // a bare Error. (#97 review — correctness reviewer)
  return useQuery<DocumentListResponse, ApiError>({
    // params is part of the key so each page/filter combination caches
    // independently; mutations invalidate the ["documents"] prefix, which
    // still covers every combination. (#187)
    queryKey: ["documents", "list", params],
    queryFn: ({ signal }) => api.get<DocumentListResponse>(`/api/documents${buildDocumentsQuery(params)}`, { signal }),
    // Keep the current page visible while the next page / a filter change is
    // fetching, instead of flashing the loading row. Initial load has no
    // previous data, so the loading state still shows then. (#187)
    placeholderData: keepPreviousData,
    refetchInterval: (q) => {
      // Short-circuit polling when the query is in an error state, even
      // if the cached `data?.items` still has Pending/Processing rows —
      // a brown-out where the API tier is failing should not keep
      // hammering it every 5s × N active dashboards. The Retry button
      // on the page is the manual recovery affordance; polling resumes
      // automatically once a refetch succeeds and status flips back to
      // 'success'. (#80 followup review — performance reviewer)
      if (q.state.status === "error") return false;
      const items = q.state.data?.items ?? [];
      const pending = items.some((d) => d.extractionStatus === "Pending" || d.extractionStatus === "Processing");
      return pending ? 5_000 : false;
    },
  });
}

export function useUploadDocument() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: async ({ file, vendorId, documentType }: { file: File; vendorId?: string; documentType?: string }) => {
      const form = new FormData();
      form.append("file", file);
      if (vendorId) form.append("vendorId", vendorId);
      if (documentType) form.append("documentType", documentType);
      const idempotencyKey = crypto.randomUUID();
      return api.postForm<{ id: string; originalFileName: string; extractionStatus: string }>(
        "/api/documents/upload",
        form,
        { idempotencyKey },
      );
    },
    onSuccess: (res) => {
      track("document.uploaded", { documentId: res.id, extractionStatus: res.extractionStatus });
      qc.invalidateQueries({ queryKey: ["documents"] });
    },
  });
}

export function useUpdateDocument() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: ({
      id,
      vendorId,
      documentType,
    }: {
      id: string;
      vendorId?: string;
      documentType?: string;
    }) => {
      // Only include the keys the caller actually wants to change so the PATCH
      // stays a true partial update — omitting vendorId leaves the assignment
      // untouched server-side rather than nulling it.
      const payload: { vendorId?: string; documentType?: string } = {};
      if (vendorId !== undefined) payload.vendorId = vendorId;
      if (documentType !== undefined) payload.documentType = documentType;
      return api.patch<void>(`/api/documents/${id}`, payload);
    },
    // Prefix-match invalidates BOTH the list (['documents','list']) and any open
    // detail observer (['documents', id]) so the assigned vendor / changed type
    // AND the recomputed compliance verdict refresh together. Call sites own
    // their own success/error toasts (no meta.errorToast) for precise copy.
    onSuccess: () => qc.invalidateQueries({ queryKey: ["documents"] }),
  });
}

export function useDeleteDocument() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (id: string) => api.delete<void>(`/api/documents/${id}`),
    onSuccess: () => qc.invalidateQueries({ queryKey: ["documents"] }),
  });
}
