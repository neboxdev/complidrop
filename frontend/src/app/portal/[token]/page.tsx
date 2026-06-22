"use client";

import { useParams } from "next/navigation";
import { useState, useCallback, useEffect } from "react";
import { useDropzone, type FileRejection } from "react-dropzone";
import { UploadCloud, CheckCircle2, ShieldCheck, RefreshCw } from "lucide-react";
import { ApiEnvelope } from "@/lib/api";
import {
  rejectionCopy,
  UPLOAD_ACCEPT,
  UPLOAD_MAX_BYTES,
  UPLOAD_PICKER_ACCEPT,
} from "@/lib/upload-policy";

const API_BASE = process.env.NEXT_PUBLIC_API_URL ?? "http://localhost:5292";

type PortalInfo = {
  vendorName: string;
  orgName: string;
  instructions: string;
  isActive: boolean;
  uploadCount: number;
  maxUploads: number;
};

type UploadResponse = {
  uploadId: string;
  extractionStatus: string;
  message: string;
};

// Discriminator codes the upload endpoint can emit that the page treats
// specially. Both surface as HTTP 429 today (see Program.cs rate-limit
// hook + VendorPortalEndpoints.cs quota path) but they want very
// different recovery affordances:
//
//   - rate_limit.exceeded → transient. The vendor hit the per-token or
//     per-ip throttle (10/hr per token, 30/hr per ip). The link itself
//     is healthy; the right next step is "wait an hour and retry".
//   - vendor.portal_quota_exceeded → permanent. The link burned its
//     MaxUploads. No amount of waiting recovers it; the vendor has to
//     ask the org owner for a fresh link.
//
// The whole point of the #45 discriminator-pair is that the client can
// tell these apart instead of guessing from body-shape. See
// [#145](https://github.com/neboxdev/complidrop/issues/145) for the
// follow-up that wired the branching here.
type UploadErrorKind = "rate_limit" | "quota_exhausted" | "other";

function classifyUploadError(code: string | undefined): UploadErrorKind {
  if (code === "rate_limit.exceeded") return "rate_limit";
  if (code === "vendor.portal_quota_exceeded") return "quota_exhausted";
  return "other";
}

type UploadError = {
  kind: UploadErrorKind;
  message: string;
  // The file that triggered the error, captured so the retry button on
  // the rate-limit branch can resubmit the SAME file instead of asking
  // the vendor to drag it again. Null for non-upload errors (e.g.
  // file-rejection copy from react-dropzone) where retry doesn't apply.
  retryFile: File | null;
};

// rejectionCopy moved to @/lib/upload-policy (#265) — the dashboard documents
// dropzone now shares the same code→copy mapping and accept list.

// Branded loading state that mirrors the portal shell (secure-upload brand +
// a skeleton dropzone) instead of a bare unstyled "Loading…". The portal is a
// one-shot, high-empathy surface often hit on a slow mobile connection — a
// blank "Loading…" reads as broken. role="status" + aria-label keeps it
// announced and detectable. (#196)
function PortalLoadingSkeleton() {
  return (
    <main
      role="status"
      aria-label="Loading your upload page"
      className="min-h-screen bg-sky-50/60 flex items-start justify-center px-4 py-12"
    >
      <div className="w-full max-w-xl space-y-6">
        <div className="text-center space-y-3">
          <div className="inline-flex items-center gap-2 text-sky-700 text-sm font-medium">
            <ShieldCheck className="w-5 h-5" /> Secure upload
          </div>
          <div className="mx-auto h-8 w-48 rounded bg-slate-200/70 motion-safe:animate-pulse" />
          <div className="mx-auto h-4 w-64 rounded bg-slate-200/60 motion-safe:animate-pulse" />
        </div>
        <div className="bg-white border-2 border-dashed border-slate-200 rounded-xl p-10 text-center shadow-sm">
          <div className="mx-auto h-12 w-12 rounded-full bg-slate-200/70 motion-safe:animate-pulse" />
          <div className="mx-auto mt-4 h-4 w-56 rounded bg-slate-200/60 motion-safe:animate-pulse" />
          <div className="mx-auto mt-2 h-3 w-40 rounded bg-slate-200/50 motion-safe:animate-pulse" />
        </div>
        <p className="text-center text-xs text-slate-500">
          Powered by <span className="font-semibold text-sky-700">CompliDrop</span>
        </p>
      </div>
    </main>
  );
}

export default function PortalPage() {
  const params = useParams<{ token: string }>();
  const [info, setInfo] = useState<PortalInfo | null>(null);
  const [loading, setLoading] = useState(true);
  const [uploading, setUploading] = useState(false);
  const [uploaded, setUploaded] = useState<{ name: string; id: string }[]>([]);
  // Discriminated upload-error state. `null` = no error; otherwise carries
  // the kind (rate-limit vs quota-exhausted vs other) so the UI can pick
  // the right recovery affordance instead of relying on string sniffing.
  const [error, setError] = useState<UploadError | null>(null);
  // FP-120: the GET /info failure mode, kept SEPARATE from upload errors. A real dead link
  // (404/410 — the only codes the backend uses for an invalid/revoked/exhausted token) gets the
  // "ask for a fresh link" copy; a network blip / 5xx / timeout is TRANSIENT and gets a "Try again"
  // affordance instead — telling a vendor a healthy link is dead (and to go bother Pat) was the P0.
  const [loadError, setLoadError] = useState<{ kind: "deadlink" | "transient"; message?: string } | null>(null);

  const fetchInfo = useCallback(async () => {
    setLoading(true);
    setLoadError(null);
    // Fetch timeout so a black-hole network doesn't spin on the skeleton forever (FP-120).
    const controller = new AbortController();
    const timeout = setTimeout(() => controller.abort(), 15_000);
    try {
      const res = await fetch(`${API_BASE}/api/portal/${params.token}`, { signal: controller.signal });
      // Reserve the dead-link copy for the codes the backend actually uses for a gone link.
      if (res.status === 404 || res.status === 410) {
        let message: string | undefined;
        try {
          message = ((await res.json()) as ApiEnvelope<PortalInfo>).error?.message;
        } catch {
          /* non-JSON body — fall back to the static dead-link copy */
        }
        setLoadError({ kind: "deadlink", message });
        return;
      }
      if (!res.ok) {
        // 5xx (or any other non-2xx that isn't a dead-link code) → the link may well be fine; transient.
        setLoadError({ kind: "transient" });
        return;
      }
      const body = (await res.json()) as ApiEnvelope<PortalInfo>;
      if (body.error || !body.data) {
        setLoadError({ kind: "transient", message: body.error?.message });
        return;
      }
      setInfo(body.data);
    } catch {
      // Network failure, the timeout abort, or a parse error — TRANSIENT, not a dead link. The raw
      // message ("Failed to fetch", "AbortError") is browser jargon, so it's never surfaced.
      setLoadError({ kind: "transient" });
    } finally {
      clearTimeout(timeout);
      setLoading(false);
    }
  }, [params.token]);

  // uploadFile is hoisted out of onDrop so the rate-limit retry button
  // can replay the failing file without recomputing the file-rejection
  // and client-quota guards (already cleared by the time we know the
  // server returned 429). Returns the discriminated UploadError on
  // failure, or null on success.
  const uploadFile = useCallback(
    async (file: File): Promise<UploadError | null> => {
      const form = new FormData();
      form.append("file", file);
      try {
        const res = await fetch(`${API_BASE}/api/portal/${params.token}/upload`, {
          method: "POST",
          body: form,
        });
        const body = (await res.json()) as ApiEnvelope<UploadResponse>;
        if (body.error) {
          return {
            kind: classifyUploadError(body.error.code),
            message: body.error.message,
            retryFile: file,
          };
        }
        if (body.data) {
          setUploaded((prev) => [...prev, { name: file.name, id: body.data!.uploadId }]);
        }
        return null;
      } catch {
        // Network/parse failure — `err.message` would leak browser
        // internals ("Failed to fetch", "TypeError", etc.). Tag as
        // "other" with a jargon-free static copy, and capture the file:
        // a network blip IS retryable, so FP-124 renders a file-preserving
        // Retry on this branch too (the gate excludes only quota_exhausted).
        return {
          kind: "other",
          message: "Upload failed. Please try again.",
          retryFile: file,
        };
      }
    },
    [params.token],
  );

  const onDrop = useCallback(
    async (accepted: File[], rejected: FileRejection[]) => {
      // react-dropzone returns rejected files separately from accepted
      // ones. Surface the rejection reason so the vendor knows why
      // nothing happened — silent rejection is hostile UX on a
      // one-shot upload surface.
      const rejectionMessage = rejectionCopy(rejected);
      if (rejectionMessage) {
        setError({ kind: "other", message: rejectionMessage, retryFile: null });
        // Don't return: still process any ACCEPTED files alongside.
      }
      if (accepted.length === 0) return;

      // Client-side quota guard: the backend will also enforce this via
      // a 409 on /upload, but blocking it here saves the vendor the
      // wasted POST and gives a clearer error. Tag as quota_exhausted
      // so the escalation UI branch lights up consistently with the
      // server-side 429 path.
      if (info && info.uploadCount + uploaded.length >= info.maxUploads) {
        setError({
          kind: "quota_exhausted",
          message:
            "You've used every upload on this link. Ask your customer for a fresh link if you need to send more.",
          retryFile: null,
        });
        return;
      }

      setUploading(true);
      if (!rejectionMessage) setError(null);
      try {
        for (const file of accepted) {
          const err = await uploadFile(file);
          if (err) {
            setError(err);
            // Match the previous for-loop semantics: stop the batch on
            // the first failure so a partial-batch failure test stays
            // green and the vendor isn't bombarded with N copies of the
            // same rate-limit toast on a 3-file drop.
            return;
          }
        }
      } finally {
        setUploading(false);
      }
    },
    [info, uploaded.length, uploadFile],
  );

  // Rate-limit retry handler. Replays the captured file once, surfacing
  // any fresh error via the same UploadError discriminator. Bound onto
  // the retry button on the rate-limit branch only — the quota branch
  // never renders it (retrying a dead link is hostile UX).
  //
  // Dep narrowed to `error?.retryFile` (not the whole `error` object) so
  // the callback identity only changes when the retry target actually
  // changes — avoids re-binding on incidental error-message tweaks and
  // makes the data dependency self-documenting. (#145 review)
  const retryFile = error?.retryFile;
  const onRetry = useCallback(async () => {
    if (!retryFile) return;
    setUploading(true);
    setError(null);
    try {
      const err = await uploadFile(retryFile);
      if (err) setError(err);
    } finally {
      setUploading(false);
    }
  }, [retryFile, uploadFile]);

  // Quota-exhausted disables the dropzone client-side. The backend still enforces via 409 — this is a
  // UX guard, not a security one. Counts in-session uploads (FP-121) so the dropzone disables the
  // moment the link is spent, not only after a reload re-reads the server count.
  const usedUploads = info ? info.uploadCount + uploaded.length : 0;
  const atQuota = info ? usedUploads >= info.maxUploads : false;

  const { getRootProps, getInputProps, isDragActive } = useDropzone({
    onDrop,
    // Shared accept list + size cap (@/lib/upload-policy): PDF + the photo formats
    // the backend admits, incl. HEIC/HEIF (iPhone "High Efficiency" photos, transcoded
    // to JPEG server-side on ingest, #220).
    accept: UPLOAD_ACCEPT,
    maxSize: UPLOAD_MAX_BYTES,
    // Reject a 0-byte pick client-side (FP-123) so it gets the clear "That file is empty." copy
    // instead of the backend's wrong-shaped message after a wasted round-trip.
    minSize: 1,
    // Disabled mid-upload too (FP-123): a double-tap while a POST is in flight would otherwise fire a
    // second upload — a duplicate document + a burned permit on the link.
    disabled: atQuota || uploading,
  });

  useEffect(() => {
    fetchInfo();
  }, [fetchInfo]);

  // FP-123: the portal is a client component, so a static `metadata` export can't set the tab title —
  // it would otherwise show the marketing-default title. Name the tab for what this page IS (and for
  // whom, once info loads) so a vendor juggling tabs can find it.
  useEffect(() => {
    document.title = info ? `Upload for ${info.orgName} · CompliDrop` : "Secure upload · CompliDrop";
  }, [info]);

  if (loading) {
    return <PortalLoadingSkeleton />;
  }

  // FP-120: a TRANSIENT failure (network / 5xx / timeout) is NOT a dead link — offer Try again, not
  // "ask your customer for a fresh link". `!info && !deadlink` defensively falls here too (retryable).
  if (!info && loadError?.kind !== "deadlink") {
    return (
      <main className="min-h-screen flex items-center justify-center px-4">
        <div className="max-w-md text-center p-6" role="alert">
          <p className="font-medium text-slate-800">We couldn&apos;t load this page.</p>
          <p className="mt-2 text-sm text-slate-500">
            Check your connection and try again — your upload link is probably fine.
          </p>
          <button
            type="button"
            onClick={() => fetchInfo()}
            className="mt-4 inline-flex items-center gap-1.5 rounded-md border border-sky-300 bg-white px-4 py-2 text-sm font-medium text-sky-700 hover:bg-sky-50 pointer-coarse:min-h-11"
          >
            <RefreshCw className="h-4 w-4" /> Try again
          </button>
        </div>
      </main>
    );
  }

  // A genuinely dead link (404/410): the only branch that tells the vendor to ask for a fresh one.
  // The server's curated message (when present) rides below as a small detail line.
  if (!info) {
    return (
      <main className="min-h-screen flex items-center justify-center">
        <div className="max-w-md text-center p-6">
          <p className="text-rose-600 font-medium">This link is no longer available.</p>
          <p className="text-sm text-slate-500 mt-2">
            Ask your customer for a fresh upload link.
          </p>
          {loadError?.message && loadError.message !== "This link is no longer available." && (
            <p className="text-xs text-slate-500 mt-3">{loadError.message}</p>
          )}
        </div>
      </main>
    );
  }

  return (
    <main className="min-h-screen bg-sky-50/60 flex items-start justify-center px-4 py-12">
      {/* FP-130: a persistent polite live region so a screen-reader vendor HEARS the upload
          transition — the visible "Uploading…" line and the Received card are otherwise silent.
          Rendered unconditionally (only its text changes) so the SR has the region before the
          state flips; absolutely-positioned `sr-only` keeps it out of the flex flow. The error
          path has its own role="alert", so it's deliberately excluded here. */}
      <div aria-live="polite" className="sr-only">
        {uploading
          ? "Uploading your document, please wait."
          : /* Stay silent on the completion line while an error is showing — its role="alert"
               region announces the failure, and "Upload complete" alongside it would be confusing
               even though it's technically still true for an earlier successful file. */
            !error && uploaded.length > 0
            ? `Upload complete. ${info.orgName} has your ${uploaded.length === 1 ? "document" : "documents"}.`
            : ""}
      </div>
      <div className="w-full max-w-xl space-y-6">
        <div className="text-center space-y-2">
          <div className="inline-flex items-center gap-2 text-sky-700 text-sm font-medium">
            <ShieldCheck className="w-5 h-5" /> Secure upload
          </div>
          <h1 className="text-3xl font-semibold text-sky-950">
            Hi {info.vendorName}
          </h1>
          <p className="text-slate-600">
            {info.orgName} asked for your latest compliance documents. Send them below.
          </p>
        </div>

        {/* The owner's specific ask — typed when the link was created and fetched
            in PortalInfo, but previously never rendered, so the vendor never saw
            it. Show it prominently above the dropzone. whitespace-pre-line keeps
            any line breaks the owner typed; it renders as escaped JSX text. (#196) */}
        {info.instructions?.trim() && (
          <div className="rounded-xl border border-sky-100 bg-white p-5 shadow-sm">
            {/* FP-122: neutral title — the backend instructions are generic boilerplate today, not the
                owner's specific ask, so don't dress them up as personalized. (A real owner-instructions
                channel is the bigger half, deferred.) */}
            <p className="text-sm font-semibold text-sky-900">What to upload</p>
            {/* Cap the height + scroll so a long note can't push the dropzone below the fold on a phone.
                tabIndex=0 so a keyboard user can scroll the box (FP-131); role/aria-label name it. */}
            <p
              tabIndex={0}
              role="region"
              aria-label="Upload instructions"
              className="mt-1 max-h-48 overflow-y-auto whitespace-pre-line text-sm text-slate-700 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring"
            >
              {info.instructions}
            </p>
          </div>
        )}

        <div
          {...getRootProps()}
          aria-disabled={atQuota || undefined}
          className={`bg-white border-2 border-dashed rounded-xl p-10 text-center shadow-sm transition focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring focus-visible:ring-offset-2 ${
            atQuota
              ? "border-slate-200 opacity-60 cursor-not-allowed"
              : isDragActive
                ? "border-sky-500 bg-sky-50 cursor-pointer"
                : "border-slate-200 hover:border-sky-300 hover:bg-sky-50/30 cursor-pointer"
          }`}
        >
          {/* The shared picker override surfaces "Take Photo" on iOS/Android — a vendor
              photographing a paper certificate is the common mobile case; the dropzone's
              own onDrop still validates the pick (UPLOAD_ACCEPT + 10 MB; HEIC transcodes
              server-side, #220) so a non-document pick is rejected with clear copy.
              NOTE: `accept` MUST stay AFTER the `{...getInputProps()}` spread —
              react-dropzone injects its own narrower `accept` and last-prop-wins is what
              lets this override take effect. (#196) */}
          <input {...getInputProps()} accept={UPLOAD_PICKER_ACCEPT} />
          <UploadCloud className="w-12 h-12 mx-auto text-sky-500" />
          <p className="mt-3 text-base font-medium text-slate-800">
            {atQuota ? (
              "Upload limit reached on this link"
            ) : isDragActive ? (
              "Drop to upload…"
            ) : (
              <>
                {/* Mobile-first copy points at the camera; the drag wording stays
                    for desktop (and pins the existing affordance test). */}
                <span className="sm:hidden">Tap to choose a file or take a photo</span>
                <span className="hidden sm:inline">Drag a file here or click to select</span>
              </>
            )}
          </p>
          {/* FP-123: include HEIC — the iPhone camera default is accepted (transcoded server-side, #220),
              so omitting it made a phone vendor think their photo wouldn't work. */}
          <p className="text-sm text-slate-500 mt-1">PDF, JPEG, PNG, or HEIC · 10 MB max</p>
          <p className="text-xs text-slate-500 mt-2">
            {usedUploads} / {info.maxUploads} uploads used on this link
          </p>
          {uploading && <p className="text-xs text-sky-600 mt-2">Uploading…</p>}
        </div>

        {error && (
          <div
            role="alert"
            aria-live="polite"
            data-testid={`portal-error-${error.kind}`}
            className="text-center space-y-2"
          >
            {/* FP-124: name WHICH file failed — on a multi-file drop the vendor otherwise can't tell. */}
            {error.retryFile && (
              <p className="text-xs text-slate-500">
                Couldn&apos;t upload <span className="font-medium">{error.retryFile.name}</span>.
              </p>
            )}
            <p className="text-sm text-rose-600">{error.message}</p>
            {/* rate_limit → transient throttle; quota_exhausted → permanently dead link (no retry,
                point at a fresh link). Both are driven by the #45 discriminator code, not copy-sniffing. */}
            {error.kind === "rate_limit" && (
              <p className="text-xs text-slate-500">Try again in about an hour.</p>
            )}
            {error.kind === "quota_exhausted" && (
              <p className="text-xs text-slate-500">
                This link is exhausted. Please ask your customer to send you a new upload link.
              </p>
            )}
            {/* FP-124: the file-preserving Retry now renders on EVERY *retryable* failure (a network
                blip AND a rate-limit), not only the rate-limit branch — the file is still selected, so
                a one-shot vendor never has to find and drag it again. quota_exhausted is the one
                PERMANENT kind (a burned link), so it's excluded even though uploadFile captures its
                file — retrying a dead link is futile/hostile (FP-123). Coarse-pointer min height (FP-130). */}
            {error.retryFile && error.kind !== "quota_exhausted" && (
              <button
                type="button"
                onClick={onRetry}
                disabled={uploading}
                className="inline-flex items-center gap-1 rounded-md border border-sky-300 bg-white px-3 py-1.5 text-sky-700 hover:bg-sky-50 disabled:opacity-50 disabled:cursor-not-allowed pointer-coarse:min-h-11"
              >
                <RefreshCw className="w-3.5 h-3.5" /> Retry upload
              </button>
            )}
          </div>
        )}

        {uploaded.length > 0 && (
          <div className="bg-white rounded-xl border border-emerald-100 p-5 shadow-sm">
            <p className="text-sm font-semibold text-emerald-700 flex items-center gap-1">
              <CheckCircle2 className="w-4 h-4" /> Received
            </p>
            <ul className="mt-2 space-y-1 text-sm text-slate-700">
              {uploaded.map((u) => (
                <li key={u.id} className="flex justify-between">
                  <span>{u.name}</span>
                  <span className="text-xs text-slate-500">Processing…</span>
                </li>
              ))}
            </ul>
            {/* "What happens next" — a cold vendor who's never heard of CompliDrop must not be left
                wondering if the upload worked or whether more is needed (#240 zero-touch sweep).
                Without it, a successful upload silently generates "did it go through?" support emails. */}
            <p className="mt-3 border-t border-emerald-100 pt-3 text-sm text-slate-600">
              That&apos;s everything {info.orgName} needs. They&apos;ll review your{" "}
              {uploaded.length === 1 ? "document" : "documents"} and reach out only if something&apos;s
              missing — you can close this page.
            </p>
          </div>
        )}

        <p className="text-center text-xs text-slate-500">
          Powered by <span className="font-semibold text-sky-700">CompliDrop</span>
        </p>
      </div>
    </main>
  );
}
