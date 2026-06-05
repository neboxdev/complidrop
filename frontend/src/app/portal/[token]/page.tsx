"use client";

import { useParams } from "next/navigation";
import { useState, useCallback, useEffect } from "react";
import { useDropzone, type FileRejection } from "react-dropzone";
import { UploadCloud, CheckCircle2, ShieldCheck, RefreshCw } from "lucide-react";
import { ApiEnvelope } from "@/lib/api";

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

// Map react-dropzone's machine-readable rejection codes to vendor-facing
// human copy. Keep the strings short — the portal target audience is a
// non-technical user landing here once.
function rejectionCopy(rejections: FileRejection[]): string | null {
  if (rejections.length === 0) return null;
  const first = rejections[0].errors[0];
  switch (first?.code) {
    case "file-invalid-type":
      // HEIC/HEIF (the iPhone camera default) is now accepted and transcoded to
      // JPEG server-side (#220), so the old "switch to Most Compatible" workaround
      // is gone. This now only fires for genuinely unsupported types (a Word doc, a
      // video, a .zip) — point at the formats that do work.
      return "We can't read that file type. Please upload a PDF or a photo (JPEG, PNG, or HEIC).";
    case "file-too-large":
      // Drop the desktop "split/compress" language — on a phone-photo surface
      // the actionable fix is to reshoot from further back or send a PDF. (#196 review)
      return "That file is over the 10 MB limit. If it's a photo, try taking it again from a bit further back, or upload a PDF.";
    case "file-too-small":
      return "That file is empty.";
    case "too-many-files":
      return "Please drop one file at a time.";
    default:
      return first?.message ?? "That file couldn't be accepted.";
  }
}

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
        <p className="text-center text-xs text-slate-400">
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

  const fetchInfo = useCallback(async () => {
    try {
      setLoading(true);
      const res = await fetch(`${API_BASE}/api/portal/${params.token}`);
      const body = (await res.json()) as ApiEnvelope<PortalInfo>;
      if (body.error) {
        // Server-curated error message — explicitly vendor-facing copy
        // the backend chose to render. Safe to surface as the detail
        // line below the static recovery copy. Tagged "other" since
        // the GET-info path never returns the 429 discriminator pair —
        // those only fire on the POST /upload route.
        setError({
          kind: classifyUploadError(body.error.code),
          message: body.error.message,
          retryFile: null,
        });
        return;
      }
      setInfo(body.data);
    } catch {
      // Network failure, JSON parse error, or any other low-level
      // issue. `err.message` here is a browser/JS internal (e.g.
      // "Failed to fetch", "Unexpected token < in JSON at …") and
      // would leak implementation details to vendors. The static
      // recovery copy alone is sufficient — leave `error` null.
    } finally {
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
        // "other" with a jargon-free static copy. No retry-file so
        // the rate-limit retry button stays hidden.
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

  // Quota-exhausted disables the dropzone client-side. The backend
  // still enforces via 409 — this is a UX guard, not a security one.
  const atQuota = info ? info.uploadCount >= info.maxUploads : false;

  const { getRootProps, getInputProps, isDragActive } = useDropzone({
    onDrop,
    accept: {
      "application/pdf": [".pdf"],
      "image/jpeg": [".jpg", ".jpeg"],
      "image/png": [".png"],
      // iPhone "High Efficiency" photos. Accepted here and transcoded to JPEG
      // server-side on ingest (#220) — no more "switch to Most Compatible" dead-end.
      "image/heic": [".heic"],
      "image/heif": [".heif"],
    },
    maxSize: 10 * 1024 * 1024,
    disabled: atQuota,
  });

  useEffect(() => {
    fetchInfo();
  }, [fetchInfo]);

  if (loading) {
    return <PortalLoadingSkeleton />;
  }

  // `info` is the only signal for the bad-link branch — `error && !info`
  // is redundant (subset of `!info`). When the GET /api/portal/{token}
  // failed for any reason (4xx, 5xx, network error, malformed body),
  // setInfo was never called and we land here. Always show the static
  // recovery copy (AC #3: "shows the recovery copy"); render the
  // curated server message as a small detail line BELOW it when
  // available so transient failures stay diagnosable without leaking
  // codes or stack traces. AC #2 says "does not expose INTERNAL
  // errors" — internal = dot-namespaced codes, stack traces. The
  // server's human message is vendor-facing copy by definition.
  if (!info) {
    return (
      <main className="min-h-screen flex items-center justify-center">
        <div className="max-w-md text-center p-6">
          <p className="text-rose-600 font-medium">This link is no longer available.</p>
          <p className="text-sm text-slate-500 mt-2">
            Ask your customer for a fresh upload link.
          </p>
          {error && error.message !== "This link is no longer available." && (
            <p className="text-xs text-slate-500 mt-3">{error.message}</p>
          )}
        </div>
      </main>
    );
  }

  return (
    <main className="min-h-screen bg-sky-50/60 flex items-start justify-center px-4 py-12">
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
            <p className="text-sm font-semibold text-sky-900">
              What {info.orgName} needs from you
            </p>
            {/* Cap the height + scroll so a long owner note can't push the
                dropzone below the fold on a phone (the vendor must still see
                the upload box). (#196 review) */}
            <p className="mt-1 max-h-48 overflow-y-auto whitespace-pre-line text-sm text-slate-700">
              {info.instructions}
            </p>
          </div>
        )}

        <div
          {...getRootProps()}
          aria-disabled={atQuota || undefined}
          className={`bg-white border-2 border-dashed rounded-xl p-10 text-center shadow-sm transition ${
            atQuota
              ? "border-slate-200 opacity-60 cursor-not-allowed"
              : isDragActive
                ? "border-sky-500 bg-sky-50 cursor-pointer"
                : "border-slate-200 hover:border-sky-300 hover:bg-sky-50/30 cursor-pointer"
          }`}
        >
          {/* Override react-dropzone's input `accept` to include `image/*` so the
              native file picker on iOS/Android surfaces "Take Photo" — a vendor
              photographing a paper certificate is the common mobile case. The
              dropzone's own onDrop still validates against PDF/JPEG/PNG/HEIC + 10 MB
              (HEIC is transcoded to JPEG server-side, #220), so a non-document pick
              (a video, a .zip) is rejected with clear copy. NOTE: the explicit
              `accept` MUST stay AFTER the `{...getInputProps()}` spread — react-dropzone
              injects its own narrower `accept` and last-prop-wins is what lets this
              override take effect. (#196) */}
          <input {...getInputProps()} accept="image/*,application/pdf" />
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
          <p className="text-sm text-slate-500 mt-1">PDF, JPEG, or PNG · 10 MB max</p>
          <p className="text-xs text-slate-500 mt-2">
            {info.uploadCount} / {info.maxUploads} uploads used on this link
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
            <p className="text-sm text-rose-600">{error.message}</p>
            {/*
             * Branched recovery affordance — driven by error.code, not
             * by sniffing message text. The #45 discriminator-pair
             * (rate_limit.exceeded vs vendor.portal_quota_exceeded)
             * exists precisely so the client can offer the right next
             * step here without parsing copy.
             *
             *   - rate_limit → transient. Surface a retry button bound
             *     to the same file. Vendors with one shot don't need
             *     to drag the file in again just because the throttle
             *     fired; the file is still selected and the link is
             *     still healthy.
             *   - quota_exhausted → permanent. No retry button —
             *     retrying a dead link is hostile UX. Instead, point
             *     the vendor at the only recovery path (a new link
             *     from the org owner).
             *   - other → fall through to the bare message; no extra
             *     affordance, since we don't know if retrying helps.
             */}
            {error.kind === "rate_limit" && (
              <div className="text-xs text-slate-500 space-y-2">
                {/*
                 * "or retry now" only makes sense if the retry button
                 * actually renders. Today the GET-info path can't
                 * produce kind="rate_limit" (the comment in fetchInfo
                 * documents that contract) so `retryFile` is always
                 * non-null on this branch — but coupling the copy to
                 * the button's presence guards a future refactor that
                 * widened the path. If retryFile is somehow null, the
                 * vendor still gets the "try again in about an hour"
                 * guidance via the fallback below.
                 */}
                {error.retryFile ? (
                  <>
                    <p>Try again in about an hour, or retry now.</p>
                    <button
                      type="button"
                      onClick={onRetry}
                      disabled={uploading}
                      className="inline-flex items-center gap-1 rounded-md border border-sky-300 bg-white px-3 py-1.5 text-sky-700 hover:bg-sky-50 disabled:opacity-50 disabled:cursor-not-allowed"
                    >
                      <RefreshCw className="w-3.5 h-3.5" /> Retry upload
                    </button>
                  </>
                ) : (
                  <p>Try again in about an hour.</p>
                )}
              </div>
            )}
            {error.kind === "quota_exhausted" && (
              <p className="text-xs text-slate-500">
                This link is exhausted. Please ask your customer to send
                you a new upload link.
              </p>
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
          </div>
        )}

        <p className="text-center text-xs text-slate-500">
          Powered by <span className="font-semibold text-sky-700">CompliDrop</span>
        </p>
      </div>
    </main>
  );
}
