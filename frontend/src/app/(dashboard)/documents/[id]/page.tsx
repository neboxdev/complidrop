"use client";

import Link from "next/link";
import { useParams } from "next/navigation";
import { useQuery, useMutation, useQueryClient } from "@tanstack/react-query";
import { toast } from "sonner";
import { AlertTriangle, ArrowLeft, RefreshCw, RotateCw, ShieldCheck } from "lucide-react";
import { api, ApiError, GENERIC_FALLBACK_MESSAGE } from "@/lib/api";
import { Card, CardContent } from "@/components/ui/card";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { StaleDataBanner } from "@/components/StaleDataBanner";
import { DocumentTypeSelect } from "@/components/DocumentTypeSelect";
import { useUpdateDocument } from "@/hooks/useDocuments";
import {
  complianceStatusLabel,
  extractionStatusLabel,
  fieldLabel,
} from "@/lib/display-labels";
import { cn } from "@/lib/utils";
import { useId, useState } from "react";

type DocField = {
  id: string;
  fieldName: string;
  fieldValue: string | null;
  fieldType: string | null;
  confidence: number;
  isManuallyEdited: boolean;
  originalValue: string | null;
};

type DocDetail = {
  id: string;
  originalFileName: string;
  documentType: string;
  documentSubType: string | null;
  vendorName: string | null;
  extractionStatus: string;
  extractionConfidence: number | null;
  complianceStatus: string;
  effectiveDate: string | null;
  expirationDate: string | null;
  daysUntilExpiry: number | null;
  isManuallyVerified: boolean;
  uploadedBy: string | null;
  blobStorageUrl: string | null;
  generalLiabilityLimit: number | null;
  fields: DocField[];
  extractionFields: unknown;
  extractionPromptVersion: string | null;
  processingError: string | null;
  createdAt: string;
  updatedAt: string;
};

// A tiered "you may want to look at this" hint instead of a raw confidence
// percentage — "87% confident" means nothing to a venue manager, but "Double-
// check this" / "Please verify" tells them what to DO. High-confidence fields
// get no hint at all (no clutter). (#188)
function confidenceHint(c: number): { text: string; className: string } | null {
  if (c >= 0.9) return null;
  if (c >= 0.7) return { text: "Double-check this", className: "text-amber-700 bg-amber-50" };
  return { text: "Please verify", className: "text-rose-700 bg-rose-50" };
}

// Module-scope per the static-components rule (#73).
function ConfidenceHint({ confidence }: { confidence: number }) {
  const hint = confidenceHint(confidence);
  if (!hint) return null;
  return (
    <span className={cn("px-2 py-0.5 rounded font-medium", hint.className)}>{hint.text}</span>
  );
}

export default function DocumentDetailPage() {
  const params = useParams<{ id: string }>();
  const qc = useQueryClient();
  const [edits, setEdits] = useState<Record<string, string>>({});
  const updateDoc = useUpdateDocument();
  const typeSelectId = useId();

  const detail = useQuery<DocDetail, ApiError>({
    queryKey: ["documents", params.id],
    queryFn: () => api.get<DocDetail>(`/api/documents/${params.id}`),
    refetchInterval: (q) => {
      // Short-circuit polling when the query is in an error state, even
      // if `state.data?.extractionStatus` is still Pending/Processing on
      // the LAST successful payload. A brown-out where the document
      // endpoint is failing should NOT keep firing every 3 s — the
      // Try-again button on the StaleDataBanner (rendered below) is the
      // manual recovery affordance, and a successful refetch flips
      // status back to 'success' so polling resumes naturally.
      // Symmetric with `useDocuments`'s refetchInterval (#80 followup);
      // pinned by the "stops polling on error" test below. (#97)
      if (q.state.status === "error") return false;
      const s = q.state.data?.extractionStatus;
      return s === "Pending" || s === "Processing" ? 3000 : false;
    },
  });

  const reextract = useMutation({
    mutationFn: () => api.post<void>(`/api/documents/${params.id}/reextract`),
    onSuccess: () => {
      qc.invalidateQueries({ queryKey: ["documents", params.id] });
      toast.success("Reading the file again…");
    },
    onError: (err) => {
      // Mirror the established mutation-error pattern across this
      // app (documents/page.tsx remove, vendors/* updates, settings/*
      // billing actions): pull the server message off the ApiError
      // when present (every `api.*` reject is an ApiError per
      // lib/api.ts's `throw new ApiError(...)`) and fall back to
      // GENERIC_FALLBACK_MESSAGE for the unreachable
      // not-an-Error branch. The CLAUDE.md error-message policy
      // requires server-message-first, GENERIC_FALLBACK_MESSAGE
      // second — and explicitly forbids raw `res.statusText` or
      // browser TypeErrors here. (#122)
      const message =
        err instanceof Error && err.message?.trim()
          ? err.message
          : GENERIC_FALLBACK_MESSAGE;
      toast.error(message);
    },
  });

  const saveFields = useMutation({
    mutationFn: (fields: { fieldName: string; fieldValue: string }[]) =>
      api.put<void>(`/api/documents/${params.id}/fields`, { fields }),
    onSuccess: () => {
      setEdits({});
      qc.invalidateQueries({ queryKey: ["documents", params.id] });
      toast.success("Fields updated");
    },
    onError: (err) => {
      // Same shape as reextract above — see that comment for the
      // rationale. (#122)
      const message =
        err instanceof Error && err.message?.trim()
          ? err.message
          : GENERIC_FALLBACK_MESSAGE;
      toast.error(message);
    },
  });

  if (detail.isLoading) {
    return <div className="p-8 text-sm text-slate-400">Loading document…</div>;
  }
  if (!detail.data) {
    // Initial-load failed with no cached data. Split 404 (the document
    // really doesn't exist for this org) from 5xx / network failure so
    // a backend outage doesn't render the same "Document not found"
    // copy as a genuinely missing record — symmetric with the list
    // page's no-data error card pattern from #80. Without this split,
    // a brown-out makes every doc detail look like it was deleted,
    // hostile to users trying to diagnose a transient problem. (#97
    // review — test-quality reviewer flagged the gap during review.)
    const isNotFound =
      detail.error instanceof ApiError && detail.error.status === 404;
    if (isNotFound || !detail.isError) {
      // Either an explicit 404 from the server, OR the unreachable
      // success-with-no-data branch (api.ts always throws on a non-ok
      // response, so this latter case can only happen on a future
      // refactor that introduced an undefined-data path). Default
      // copy is unchanged from before #97 — keeps the regression
      // surface for the 404 path identical.
      return (
        <div className="p-8 text-sm text-slate-500">
          Document not found. <Link href="/documents" className="text-sky-700">Back to documents</Link>
        </div>
      );
    }
    // 5xx / 0 (network unreachable) / non-404 ApiError: surface the
    // server message + Retry affordance, role=alert so assistive tech
    // announces the failure (mirrors the list-page full-page error
    // card pattern at documents/page.tsx:140-165).
    const message = detail.error?.message?.trim() || GENERIC_FALLBACK_MESSAGE;
    return (
      <div className="max-w-5xl mx-auto px-6 py-8">
        <Link
          href="/documents"
          className="inline-flex items-center gap-1 text-sm text-sky-700 hover:text-sky-800 mb-6"
        >
          <ArrowLeft className="w-4 h-4" /> All documents
        </Link>
        <Card>
          <CardContent className="p-12 text-center" role="alert">
            <AlertTriangle className="w-8 h-8 mx-auto text-rose-500" />
            <p className="mt-2 text-sm font-medium text-slate-800">
              Couldn&apos;t load document.
            </p>
            <p className="text-xs text-slate-500">{message}</p>
            <Button
              variant="outline"
              size="sm"
              className="mt-3"
              onClick={() => detail.refetch()}
              disabled={detail.isFetching}
            >
              <RotateCw
                className={cn(
                  "w-3.5 h-3.5 mr-1",
                  detail.isFetching && "animate-spin",
                )}
              />
              Retry
            </Button>
          </CardContent>
        </Card>
      </div>
    );
  }

  const doc = detail.data;
  const hasEdits = Object.keys(edits).length > 0;
  const isProcessing =
    doc.extractionStatus === "Pending" || doc.extractionStatus === "Processing";

  return (
    <div className="max-w-5xl mx-auto px-6 py-8 space-y-6">
      <Link href="/documents" className="inline-flex items-center gap-1 text-sm text-sky-700 hover:text-sky-800">
        <ArrowLeft className="w-4 h-4" /> All documents
      </Link>

      <header className="flex flex-col gap-3 sm:flex-row sm:items-start sm:justify-between">
        <div className="space-y-1.5">
          <h1 className="text-xl font-semibold text-sky-900 break-words">{doc.originalFileName}</h1>
          <div className="flex items-center gap-2">
            <label htmlFor={typeSelectId} className="text-xs font-medium text-slate-500">
              Type
            </label>
            <DocumentTypeSelect
              id={typeSelectId}
              value={doc.documentType}
              disabled={updateDoc.isPending}
              onChange={(type) =>
                updateDoc.mutate(
                  { id: doc.id, documentType: type },
                  {
                    onSuccess: () => toast.success("Document type updated"),
                    onError: (err) =>
                      toast.error(
                        err instanceof Error ? err.message : "Couldn't update the type.",
                      ),
                  },
                )
              }
            />
          </div>
        </div>
        <div className="flex items-center gap-2 shrink-0">
          <Button
            variant="outline"
            size="sm"
            title="Reads the file again from scratch — this replaces any edits you've made."
            onClick={() => reextract.mutate()}
            disabled={reextract.isPending || isProcessing}
          >
            <RefreshCw className={cn("w-4 h-4 mr-1", reextract.isPending && "animate-spin")} /> Read again
          </Button>
          {doc.blobStorageUrl && (
            <a
              href={doc.blobStorageUrl}
              target="_blank"
              rel="noreferrer"
              className="text-sm text-sky-700 hover:underline"
            >
              View file
            </a>
          )}
        </div>
      </header>

      {detail.isError && (
        // Polling failed while cached detail is still rendered (we're
        // past the `!detail.data` early-return above, so data is
        // present). Surface the failure as a discreet banner so the
        // user knows the field values / status badges below may not
        // reflect the latest extraction state — the polling
        // short-circuits on error above so the backend isn't
        // hammered while the banner is visible. Symmetric with the
        // documents-list treatment. (#97)
        <StaleDataBanner
          message={detail.error?.message}
          onRetry={() => detail.refetch()}
          isRetrying={detail.isFetching}
          noun="document"
        />
      )}

      <section className="grid grid-cols-2 md:grid-cols-4 gap-3 text-sm">
        <SummaryCell label="Reading" value={
          <Badge
            data-testid="extraction-status"
            className={cn("border-transparent",
              doc.extractionStatus === "Completed" ? "bg-emerald-100 text-emerald-700"
                : doc.extractionStatus === "Failed" ? "bg-rose-100 text-rose-700"
                : "bg-sky-100 text-sky-700")}
          >
            {extractionStatusLabel(doc.extractionStatus)}
          </Badge>
        } />
        <SummaryCell label="Compliance" value={
          <Badge
            data-testid="compliance-status"
            className={cn("border-transparent",
              doc.complianceStatus === "Compliant" ? "bg-emerald-100 text-emerald-700"
                : doc.complianceStatus === "NonCompliant" ? "bg-rose-100 text-rose-700"
                : "bg-slate-100 text-slate-700")}
          >
            {complianceStatusLabel(doc.complianceStatus)}
          </Badge>
        } />
        <SummaryCell label="Expires" value={doc.expirationDate ? new Date(doc.expirationDate).toLocaleDateString() : "—"} />
        <SummaryCell label="Verified" value={doc.isManuallyVerified ? <span className="inline-flex items-center gap-1 text-emerald-700"><ShieldCheck className="w-3.5 h-3.5" /> Yes</span> : "—"} />
      </section>

      {doc.processingError && (
        <Card className="border-rose-200">
          <CardContent className="p-4 text-sm text-rose-700">
            <p className="font-medium">We couldn&apos;t read this document</p>
            <p>{doc.processingError}</p>
          </CardContent>
        </Card>
      )}

      <Card>
        <CardContent className="p-6 space-y-4">
          <div className="flex items-center justify-between">
            <h2 className="font-semibold text-slate-800">Extracted fields</h2>
            <Button
              size="sm"
              disabled={!hasEdits || saveFields.isPending}
              onClick={() => {
                const fields = Object.entries(edits).map(([fieldName, fieldValue]) => ({ fieldName, fieldValue }));
                saveFields.mutate(fields);
              }}
            >
              Save changes
            </Button>
          </div>
          {doc.fields.length === 0 ? (
            <p className="text-sm text-slate-500">
              {doc.extractionStatus === "Pending" || doc.extractionStatus === "Processing"
                ? "Reading the document…"
                : "No details read yet."}
            </p>
          ) : (
            <div className="grid grid-cols-1 md:grid-cols-2 gap-4">
              {doc.fields.map((f) => (
                <div key={f.id} className="space-y-1">
                  {/* a11y: scope id to the field row so screen readers
                      announce each input with its field-name context (#76). */}
                  <label htmlFor={`docfield-${f.id}`} className="text-xs font-medium tracking-wide text-slate-500">{fieldLabel(f.fieldName)}</label>
                  <Input
                    id={`docfield-${f.id}`}
                    defaultValue={f.fieldValue ?? ""}
                    onChange={(e) => setEdits((prev) => ({ ...prev, [f.fieldName]: e.target.value }))}
                  />
                  <div className="flex items-center gap-2 text-xs">
                    <ConfidenceHint confidence={f.confidence} />
                    {f.isManuallyEdited && <span className="text-sky-700">✎ Manually edited</span>}
                    {f.originalValue && f.originalValue !== f.fieldValue && (
                      <span className="text-slate-400">was: {f.originalValue}</span>
                    )}
                  </div>
                </div>
              ))}
            </div>
          )}
        </CardContent>
      </Card>
    </div>
  );
}

function SummaryCell({ label, value }: { label: string; value: React.ReactNode }) {
  return (
    <Card>
      <CardContent className="p-4 space-y-1">
        <p className="text-xs uppercase tracking-wide text-slate-500">{label}</p>
        <div className="text-sm font-medium text-slate-900">{value}</div>
      </CardContent>
    </Card>
  );
}
