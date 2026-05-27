"use client";

import { useState, useCallback } from "react";
import Link from "next/link";
import { useDropzone } from "react-dropzone";
import { toast } from "sonner";
import { UploadCloud, FileText, Trash2, AlertTriangle, RotateCw } from "lucide-react";
import { Button } from "@/components/ui/button";
import { Card, CardContent } from "@/components/ui/card";
import { Badge } from "@/components/ui/badge";
import { StaleDataBanner } from "@/components/StaleDataBanner";
import { useDocuments, useUploadDocument, useDeleteDocument } from "@/hooks/useDocuments";
import { cn } from "@/lib/utils";
import { GENERIC_FALLBACK_MESSAGE } from "@/lib/api";

const STATUS_HUE: Record<string, string> = {
  Pending: "bg-slate-100 text-slate-700",
  Processing: "bg-sky-100 text-sky-700 animate-pulse",
  Completed: "bg-emerald-100 text-emerald-700",
  ManualRequired: "bg-amber-100 text-amber-700",
  Failed: "bg-rose-100 text-rose-700",
};

const COMPLIANCE_HUE: Record<string, string> = {
  Pending: "bg-slate-100 text-slate-700",
  Compliant: "bg-emerald-100 text-emerald-700",
  NonCompliant: "bg-rose-100 text-rose-700",
  ExpiringSoon: "bg-amber-100 text-amber-700",
  Expired: "bg-rose-100 text-rose-700",
};

export default function DocumentsPage() {
  const docs = useDocuments();
  const upload = useUploadDocument();
  const del = useDeleteDocument();
  const [isUploading, setIsUploading] = useState(false);

  const onDrop = useCallback(
    async (accepted: File[]) => {
      if (accepted.length === 0) return;
      setIsUploading(true);
      try {
        for (const file of accepted) {
          await upload.mutateAsync({ file });
          toast.success(`Uploaded ${file.name}`);
        }
      } catch (err) {
        const message = err instanceof Error ? err.message : "Upload failed.";
        toast.error(message);
      } finally {
        setIsUploading(false);
      }
    },
    [upload],
  );

  const { getRootProps, getInputProps, isDragActive } = useDropzone({
    onDrop,
    accept: {
      "application/pdf": [".pdf"],
      "image/jpeg": [".jpg", ".jpeg"],
      "image/png": [".png"],
    },
    maxSize: 10 * 1024 * 1024,
  });

  const items = docs.data?.items ?? [];

  return (
    <div className="max-w-6xl mx-auto px-6 py-8 space-y-6">
      <header className="flex items-center justify-between">
        <div>
          <h1 className="text-2xl font-semibold text-sky-900">Documents</h1>
          <p className="text-slate-500">COIs, licenses, permits — dropped once, tracked forever.</p>
        </div>
        <p className="text-xs text-slate-500">{docs.data?.total ?? 0} total</p>
      </header>

      <Card>
        <CardContent className="p-6">
          <div
            {...getRootProps()}
            className={cn(
              "border-2 border-dashed rounded-lg p-10 text-center transition cursor-pointer",
              isDragActive ? "border-sky-500 bg-sky-50" : "border-slate-200 hover:border-sky-300 hover:bg-sky-50/50",
            )}
          >
            <input {...getInputProps()} />
            <UploadCloud className="w-10 h-10 mx-auto text-sky-500" />
            <p className="mt-3 text-sm font-medium text-slate-700">
              {isDragActive ? "Drop to upload…" : "Drag a file here or click to browse"}
            </p>
            <p className="text-xs text-slate-500">PDF, JPEG, PNG · 10 MB max</p>
            {isUploading && <p className="text-xs text-sky-600 mt-2">Uploading…</p>}
          </div>
        </CardContent>
      </Card>

      {docs.isError && items.length > 0 && (
        // Discreet "couldn't refresh" indicator when the polling
        // refetch failed but the cached list is still rendered below.
        // Gated on `items.length > 0` (symmetric with the full-page
        // error card's `length === 0` gate) — a populated list keeps
        // the user reading the stale data; the banner signals it may
        // not reflect the latest state. `useDocuments`'s
        // refetchInterval short-circuits on error so the backend isn't
        // hammered while the banner is visible — once the user clicks
        // Try again (or any subsequent refetch lands a 200), isError
        // flips back to false and the banner disappears. (#97)
        <StaleDataBanner
          message={docs.error?.message}
          onRetry={() => docs.refetch()}
          isRetrying={docs.isFetching}
          noun="documents"
        />
      )}

      <Card>
        <CardContent className="p-0 overflow-hidden">
          <table className="w-full text-sm">
            <thead className="bg-slate-50 text-xs uppercase text-slate-500">
              <tr>
                <th className="px-4 py-3 text-left">File</th>
                <th className="px-4 py-3 text-left">Type</th>
                <th className="px-4 py-3 text-left">Vendor</th>
                <th className="px-4 py-3 text-left">Extraction</th>
                <th className="px-4 py-3 text-left">Compliance</th>
                <th className="px-4 py-3 text-left">Expires</th>
                <th className="px-4 py-3" />
              </tr>
            </thead>
            <tbody>
              {docs.isLoading ? (
                <tr>
                  <td colSpan={7} className="px-4 py-8 text-center text-sm text-slate-400">
                    Loading documents…
                  </td>
                </tr>
              ) : docs.isError && items.length === 0 ? (
                // Error state distinct from empty so a backend outage is
                // not mistaken for a brand-new org with zero documents
                // (#80). Gate on `items.length === 0` so a polling
                // failure on a populated list does NOT clobber the
                // rows the user is reading — the cached data stays
                // visible and `useDocuments.refetchInterval` short-
                // circuits on error so the backend isn't hammered.
                // The polling-failure-UX-banner is deferred to #97;
                // this gate is the minimum that prevents the
                // regression from shipping per the #80 followup review.
                //
                // `err.message` is the human server message from the
                // ApiError envelope; api.ts's GENERIC_FALLBACK_MESSAGE
                // kicks in when the body is non-JSON or fetch rejected
                // (#77), so `?? GENERIC_FALLBACK_MESSAGE` here only
                // covers the unreachable `error: null` defensive path.
                //
                // role="alert" gets the error announced by assistive
                // tech the moment isError flips true, matching the
                // convention in frontend/src/test/example.test.tsx.
                <tr>
                  <td colSpan={7} className="px-4 py-12 text-center" role="alert">
                    <AlertTriangle className="w-8 h-8 mx-auto text-rose-500" />
                    <p className="mt-2 text-sm font-medium text-slate-800">
                      Couldn&apos;t load documents.
                    </p>
                    <p className="text-xs text-slate-500">
                      {docs.error?.message?.trim() || GENERIC_FALLBACK_MESSAGE}
                    </p>
                    <Button
                      variant="outline"
                      size="sm"
                      className="mt-3"
                      onClick={() => docs.refetch()}
                      disabled={docs.isFetching}
                    >
                      <RotateCw
                        className={cn(
                          "w-3.5 h-3.5 mr-1",
                          docs.isFetching && "animate-spin",
                        )}
                      />
                      Retry
                    </Button>
                  </td>
                </tr>
              ) : items.length === 0 ? (
                <tr>
                  <td colSpan={7} className="px-4 py-12 text-center">
                    <FileText className="w-8 h-8 mx-auto text-slate-300" />
                    <p className="mt-2 text-sm text-slate-500">No documents yet. Drop one above to get started.</p>
                  </td>
                </tr>
              ) : (
                items.map((d) => (
                  <tr key={d.id} className="border-t border-slate-100 hover:bg-slate-50/80">
                    <td className="px-4 py-3">
                      <Link href={`/documents/${d.id}`} className="text-sky-700 hover:underline font-medium">
                        {d.originalFileName}
                      </Link>
                    </td>
                    <td className="px-4 py-3 text-slate-600 uppercase text-xs">{d.documentType}</td>
                    <td className="px-4 py-3 text-slate-600">{d.vendorName ?? "—"}</td>
                    <td className="px-4 py-3">
                      <Badge className={cn("border-transparent font-medium", STATUS_HUE[d.extractionStatus] ?? STATUS_HUE.Pending)}>
                        {d.extractionStatus}
                        {d.extractionConfidence != null && ` · ${Math.round(d.extractionConfidence * 100)}%`}
                      </Badge>
                    </td>
                    <td className="px-4 py-3">
                      <Badge className={cn("border-transparent", COMPLIANCE_HUE[d.complianceStatus] ?? COMPLIANCE_HUE.Pending)}>
                        {d.complianceStatus}
                      </Badge>
                    </td>
                    <td className="px-4 py-3 text-slate-600">
                      {d.expirationDate ? new Date(d.expirationDate).toLocaleDateString() : "—"}
                      {d.daysUntilExpiry != null && (
                        <span className={cn("ml-2 text-xs", d.daysUntilExpiry < 30 ? "text-rose-600" : "text-slate-400")}>
                          {d.daysUntilExpiry < 0 ? `${-d.daysUntilExpiry}d ago` : `in ${d.daysUntilExpiry}d`}
                        </span>
                      )}
                    </td>
                    <td className="px-4 py-3 text-right">
                      <Button
                        variant="ghost"
                        size="sm"
                        disabled={del.isPending}
                        onClick={() => {
                          if (confirm(`Remove ${d.originalFileName}?`)) {
                            del.mutate(d.id, {
                              onSuccess: () => toast.success("Document removed"),
                              onError: (err) => toast.error(err instanceof Error ? err.message : "Remove failed"),
                            });
                          }
                        }}
                      >
                        <Trash2 className="w-4 h-4 text-slate-400 hover:text-rose-600" />
                      </Button>
                    </td>
                  </tr>
                ))
              )}
            </tbody>
          </table>
        </CardContent>
      </Card>
    </div>
  );
}
