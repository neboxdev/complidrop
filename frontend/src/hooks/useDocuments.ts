"use client";

import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { api } from "@/lib/api";
import { track } from "@/lib/analytics";

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
  createdAt: string;
};

export type DocumentListResponse = {
  items: DocumentListItem[];
  total: number;
  page: number;
  pageSize: number;
};

export function useDocuments() {
  return useQuery<DocumentListResponse>({
    queryKey: ["documents", "list"],
    queryFn: () => api.get<DocumentListResponse>("/api/documents"),
    refetchInterval: (q) => {
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

export function useDeleteDocument() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (id: string) => api.delete<void>(`/api/documents/${id}`),
    onSuccess: () => qc.invalidateQueries({ queryKey: ["documents"] }),
  });
}
