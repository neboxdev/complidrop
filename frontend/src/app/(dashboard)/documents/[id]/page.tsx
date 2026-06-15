"use client";

import Link from "next/link";
import { useParams } from "next/navigation";
import { useQuery, useMutation, useQueryClient } from "@tanstack/react-query";
import { toast } from "sonner";
import { AlertTriangle, ArrowLeft, ExternalLink, Mail, RefreshCw, RotateCw, ShieldCheck } from "lucide-react";
import { api, ApiError, GENERIC_FALLBACK_MESSAGE } from "@/lib/api";
import { Card, CardContent } from "@/components/ui/card";
import { ComplianceBadge, ExtractionBadge } from "@/components/StatusBadges";
import { Button, buttonVariants } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { StaleDataBanner } from "@/components/StaleDataBanner";
import { DocumentTypeSelect } from "@/components/DocumentTypeSelect";
import { useUpdateDocument } from "@/hooks/useDocuments";
import {
  complianceFailureReason,
  fieldLabel,
  processingErrorMessage,
} from "@/lib/display-labels";
import { SUPPORT_EMAIL } from "@/lib/site";
import { formatCalendarDate } from "@/lib/dates";
import { cn } from "@/lib/utils";
import { useEffect, useId, useRef, useState } from "react";

type DocField = {
  id: string;
  fieldName: string;
  fieldValue: string | null;
  fieldType: string | null;
  confidence: number;
  isManuallyEdited: boolean;
  originalValue: string | null;
};

// Mirror of the backend ComplianceCheckDto (camelCased over JSON). Carries both
// the machine fields and the owner-authored ruleErrorMessage so the page can
// explain a failure in plain English. (#193)
type ComplianceCheck = {
  id: string;
  complianceRuleId: string;
  ruleFieldName: string | null;
  ruleOperator: string | null;
  ruleExpectedValue: string | null;
  ruleErrorMessage: string | null;
  actualValue: string | null;
  isPassed: boolean;
  notes: string | null;
  checkedAt: string;
};

type DocDetail = {
  id: string;
  originalFileName: string;
  documentType: string;
  documentSubType: string | null;
  vendorName: string | null;
  vendorContactEmail: string | null;
  vendorId: string | null;
  extractionStatus: string;
  extractionConfidence: number | null;
  complianceStatus: string;
  effectiveDate: string | null;
  expirationDate: string | null;
  daysUntilExpiry: number | null;
  isManuallyVerified: boolean;
  uploadedBy: string | null;
  generalLiabilityLimit: number | null;
  fields: DocField[];
  complianceChecks: ComplianceCheck[];
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

// Tiers a field's input border by confidence so the eye lands on the values the
// extractor was least sure of — amber for "double-check", rose for "please
// verify". High-confidence fields keep the neutral default. Mirrors the
// confidenceHint thresholds. (#193)
function fieldBorderClass(confidence: number): string {
  if (confidence >= 0.9) return "";
  if (confidence >= 0.7) return "border-amber-400 focus-visible:ring-amber-400/40";
  return "border-rose-400 focus-visible:ring-rose-400/40";
}

// Build a `mailto:` that opens the user's mail client pre-filled with the vendor
// (when we have their email), a subject, and the failed-requirement reasons as a
// bulleted body. Encoded with encodeURIComponent so spaces are %20 and newlines
// %0A (URLSearchParams would emit "+" for spaces, which some mail clients show
// literally). A missing email yields `mailto:?…` so the user just fills in the
// recipient. (#193)
function buildVendorMailto(doc: DocDetail, reasons: string[]): string {
  const subject = `Action needed on your document: ${doc.originalFileName}`;
  const body = [
    doc.vendorName ? `Hi ${doc.vendorName},` : "Hi,",
    "",
    "We can't mark the document you sent as compliant yet. Here's what needs fixing:",
    "",
    ...reasons.map((r) => `• ${r}`),
    "",
    "Please send an updated copy when you can. Thank you!",
  ].join("\n");
  const to = doc.vendorContactEmail?.trim() ?? "";
  return `mailto:${to}?subject=${encodeURIComponent(subject)}&body=${encodeURIComponent(body)}`;
}

// Support mailto carrying the document context (file name, id, raw error) so the
// support team can triage without a round-trip. The raw error rides in the body,
// NOT in any user-facing copy. (#193)
function buildSupportMailto(doc: DocDetail): string {
  const subject = `Help reading my document: ${doc.originalFileName}`;
  const body = [
    `I'm having trouble with a document in CompliDrop.`,
    "",
    `Document: ${doc.originalFileName}`,
    `Reference: ${doc.id}`,
    doc.processingError ? `Technical detail: ${doc.processingError}` : "",
  ]
    .filter(Boolean)
    .join("\n");
  return `mailto:${SUPPORT_EMAIL}?subject=${encodeURIComponent(subject)}&body=${encodeURIComponent(body)}`;
}

// "Why isn't this compliant?" — lists each failed requirement in plain English
// with a primary "Email {vendor} to fix this" CTA pre-filled with the reasons.
// Renders only when there's at least one failed check, so a compliant document
// shows nothing. (#193)
function NonComplianceExplainer({ doc }: { doc: DocDetail }) {
  // Defensive `?? []`: the API always sends complianceChecks, but a payload that
  // omits it (older cache, a partial mock) must not white-screen the whole detail
  // page on `.filter`. (#198)
  const checks = doc.complianceChecks ?? [];
  const failed = checks.filter((c) => !c.isPassed);
  if (failed.length === 0) return null;
  const reasons = failed.map(complianceFailureReason);
  const passedCount = checks.length - failed.length;
  return (
    <Card className="border-rose-200 bg-rose-50/50">
      <CardContent className="p-4 sm:p-6 space-y-3">
        <div className="flex items-center gap-2">
          <AlertTriangle className="w-4 h-4 text-rose-600 shrink-0" aria-hidden />
          <h2 className="font-semibold text-rose-900">Why isn&apos;t this compliant?</h2>
        </div>
        <ul className="space-y-1.5 text-sm text-slate-700">
          {failed.map((c, i) => (
            <li key={c.id} className="flex gap-2">
              <span className="mt-1.5 h-1.5 w-1.5 shrink-0 rounded-full bg-rose-500" aria-hidden />
              <span>{reasons[i]}</span>
            </li>
          ))}
        </ul>
        {passedCount > 0 && (
          <p className="text-xs text-emerald-700">
            {passedCount} other requirement{passedCount === 1 ? "" : "s"} met.
          </p>
        )}
        <div className="space-y-1.5 pt-1">
          <a href={buildVendorMailto(doc, reasons)} className={cn(buttonVariants({ size: "sm" }))}>
            <Mail className="w-4 h-4" aria-hidden />
            {doc.vendorName ? `Email ${doc.vendorName} to fix this` : "Email the vendor to fix this"}
          </a>
          {!doc.vendorContactEmail && (
            <p className="text-xs text-slate-500">
              {doc.vendorName ? (
                <>
                  Tip: add an email for {doc.vendorName} on{" "}
                  {doc.vendorId ? (
                    <Link
                      href={`/vendors/${doc.vendorId}`}
                      className="text-sky-700 hover:underline"
                    >
                      the vendor&apos;s page
                    </Link>
                  ) : (
                    "the vendor's page"
                  )}{" "}
                  to send in one click.
                </>
              ) : (
                "Tip: assign a vendor so we can fill in their email for you."
              )}
            </p>
          )}
        </div>
      </CardContent>
    </Card>
  );
}

// Amber call-to-action for the ManualRequired state: tells the user exactly what
// to do (review the amber-bordered fields, fix, Save). (#193)
function ManualReviewCard() {
  return (
    <Card className="border-amber-300 bg-amber-50">
      <CardContent className="p-4 sm:p-6 space-y-1">
        <div className="flex items-center gap-2">
          <AlertTriangle className="w-4 h-4 text-amber-600 shrink-0" aria-hidden />
          <h2 className="font-semibold text-amber-900">Please double-check these details</h2>
        </div>
        <p className="text-sm text-amber-800">
          We weren&apos;t fully confident reading this document. Check the values below —
          the ones outlined in amber are the least certain — fix anything that looks
          wrong, then click <span className="font-medium">Save changes</span>.
        </p>
      </CardContent>
    </Card>
  );
}

// Humanized processing-error card: friendly headline + mapped plain-English copy
// + a support link, with the raw error tucked behind a "Details for support"
// disclosure (never shown inline). (#193)
function ProcessingErrorCard({ doc }: { doc: DocDetail }) {
  return (
    <Card className="border-rose-200">
      <CardContent className="p-4 text-sm space-y-2">
        <p className="font-medium text-rose-700">We couldn&apos;t read this document</p>
        <p className="text-slate-700">{processingErrorMessage(doc.processingError)}</p>
        <p>
          <a href={buildSupportMailto(doc)} className="text-sky-700 hover:underline">
            Contact support
          </a>
        </p>
        <details className="text-xs text-slate-500">
          <summary className="cursor-pointer select-none">Details for support</summary>
          <p className="mt-1 font-mono break-words text-slate-500">{doc.processingError}</p>
        </details>
      </CardContent>
    </Card>
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
    queryFn: ({ signal }) => api.get<DocDetail>(`/api/documents/${params.id}`, { signal }),
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
      const message =
        err instanceof Error && err.message?.trim()
          ? err.message
          : GENERIC_FALLBACK_MESSAGE;
      toast.error(message);
    },
  });

  // "Check again" re-runs ONLY the compliance evaluation against the current rules
  // (POST /api/compliance/check/{id}) — no re-extraction, no LLM cost. The manual escape hatch
  // for #257: assignment/rule changes already fan out automatically, but this lets a user force a
  // fresh verdict on demand (e.g. right after editing a checklist) without re-reading the file.
  const recheck = useMutation({
    mutationFn: () => api.post<void>(`/api/compliance/check/${params.id}`),
    onSuccess: () => {
      qc.invalidateQueries({ queryKey: ["documents", params.id] });
      toast.success("Re-checking compliance…");
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

  // "View file" streams the original through the authenticated proxy
  // (GET /api/documents/{id}/file, #254) — the raw blob URL is private and was
  // never viewable. api.getBlob carries the silent 401-refresh, so a stale
  // session recovers instead of dumping a JSON error in the new tab. The tab
  // handle is opened in the CLICK HANDLER (synchronous with the user gesture —
  // popup blockers allow window.open only there, and TanStack defers
  // mutationFn to a microtask) and passed in as the mutation variable.
  const viewFile = useMutation({
    mutationFn: async (tab: Window | null) => {
      try {
        if (tab === null) {
          // Aggressively-configured blocker: no tab handle. Plain English, no
          // browser jargon beyond the word users see in their own browser UI.
          throw new ApiError(
            "browser.popup_blocked",
            "Your browser blocked the new tab. Allow pop-ups for this site and try again.",
            0,
          );
        }
        const blob = await api.getBlob(`/api/documents/${params.id}/file`);
        const url = URL.createObjectURL(blob);
        tab.location.href = url;
        // Revoke when the viewer TAB closes, not on a fixed timer: the whole
        // point of the button is keeping the tab open to compare against the
        // fields, and a timed revoke would break F5 / save-as in that tab
        // after it fired. Polling tab.closed is the only portable signal —
        // there is no cross-window close event for the opener.
        const revokeWhenClosed = setInterval(() => {
          if (tab.closed) {
            clearInterval(revokeWhenClosed);
            URL.revokeObjectURL(url);
          }
        }, 5_000);
      } catch (err) {
        tab?.close();
        throw err;
      }
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

  // Announce the terminal transition (Pending/Processing → Read/Failed/Needs
  // review) to screen-reader users via a polite live region — written by
  // textContent rather than state to keep it out of the render path. (#189)
  const liveRef = useRef<HTMLDivElement>(null);
  const prevExtraction = useRef<string | null>(null);
  const liveStatus = detail.data?.extractionStatus;
  const liveName = detail.data?.originalFileName;
  useEffect(() => {
    if (!liveStatus) return;
    const prev = prevExtraction.current;
    const wasInFlight = prev === "Pending" || prev === "Processing";
    const isTerminal =
      liveStatus === "Completed" || liveStatus === "Failed" || liveStatus === "ManualRequired";
    if (wasInFlight && isTerminal && liveRef.current) {
      liveRef.current.textContent = `${liveName ?? "Document"} finished processing.`;
    }
    prevExtraction.current = liveStatus;
  }, [liveStatus, liveName]);

  if (detail.isLoading) {
    return <div className="p-8 text-sm text-slate-500">Loading document…</div>;
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
      {/* aria-live (not role="status") so it announces without colliding with
          the StaleDataBanner's role="status" on the same page. */}
      <div ref={liveRef} aria-live="polite" aria-atomic="true" className="sr-only" />
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
          <Button
            variant="outline"
            size="sm"
            title="Re-checks this document against the current requirements — use after changing a checklist or rule. Doesn't re-read the file."
            onClick={() => recheck.mutate()}
            disabled={recheck.isPending || isProcessing}
          >
            <ShieldCheck className={cn("w-4 h-4 mr-1", recheck.isPending && "animate-pulse")} /> Check again
          </Button>
          <Button
            variant="outline"
            size="sm"
            title="Opens the original file in a new tab so you can compare it against what we read."
            onClick={() => viewFile.mutate(window.open("about:blank", "_blank"))}
            disabled={viewFile.isPending}
          >
            <ExternalLink className={cn("w-4 h-4 mr-1", viewFile.isPending && "animate-pulse")} /> View file
          </Button>
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
        <SummaryCell
          label="Reading"
          value={<ExtractionBadge status={doc.extractionStatus} data-testid="extraction-status" />}
        />
        <SummaryCell
          label="Compliance"
          value={<ComplianceBadge status={doc.complianceStatus} data-testid="compliance-status" />}
        />
        <SummaryCell label="Expires" value={formatCalendarDate(doc.expirationDate)} />
        <SummaryCell label="Verified" value={doc.isManuallyVerified ? <span className="inline-flex items-center gap-1 text-emerald-700"><ShieldCheck className="w-3.5 h-3.5" /> Yes</span> : "—"} />
      </section>

      <NonComplianceExplainer doc={doc} />

      {doc.extractionStatus === "ManualRequired" && <ManualReviewCard />}

      {doc.processingError && <ProcessingErrorCard doc={doc} />}

      <Card>
        <CardContent className="p-6 space-y-4">
          <div className="flex items-center justify-between">
            <h2 className="font-semibold text-slate-800">Extracted fields</h2>
            <Button
              size="sm"
              // In ManualRequired, allow Save even with no edits so the user can
              // confirm "these look right" — the backend flips the doc to
              // Completed on save, clearing the review state. Other states keep
              // the edits-required gate. (#193)
              disabled={
                saveFields.isPending ||
                (!hasEdits && doc.extractionStatus !== "ManualRequired")
              }
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
                    className={fieldBorderClass(f.confidence)}
                    onChange={(e) => setEdits((prev) => ({ ...prev, [f.fieldName]: e.target.value }))}
                  />
                  <div className="flex items-center gap-2 text-xs">
                    <ConfidenceHint confidence={f.confidence} />
                    {f.isManuallyEdited && <span className="text-sky-700">✎ Manually edited</span>}
                    {f.originalValue && f.originalValue !== f.fieldValue && (
                      <span className="text-slate-500">was: {f.originalValue}</span>
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
