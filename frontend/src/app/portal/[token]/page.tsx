"use client";

import { useParams } from "next/navigation";
import { useState, useCallback, useEffect } from "react";
import { useDropzone, type FileRejection } from "react-dropzone";
import { UploadCloud, CheckCircle2, ShieldCheck } from "lucide-react";
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

// Map react-dropzone's machine-readable rejection codes to vendor-facing
// human copy. Keep the strings short — the portal target audience is a
// non-technical user landing here once.
function rejectionCopy(rejections: FileRejection[]): string | null {
  if (rejections.length === 0) return null;
  const first = rejections[0].errors[0];
  switch (first?.code) {
    case "file-invalid-type":
      return "That file type isn't accepted. Please upload a PDF, JPEG, or PNG.";
    case "file-too-large":
      return "That file is too large. The 10 MB cap is per file — try splitting it or compressing it.";
    case "file-too-small":
      return "That file is empty.";
    case "too-many-files":
      return "Please drop one file at a time.";
    default:
      return first?.message ?? "That file couldn't be accepted.";
  }
}

export default function PortalPage() {
  const params = useParams<{ token: string }>();
  const [info, setInfo] = useState<PortalInfo | null>(null);
  const [loading, setLoading] = useState(true);
  const [uploading, setUploading] = useState(false);
  const [uploaded, setUploaded] = useState<{ name: string; id: string }[]>([]);
  const [error, setError] = useState<string | null>(null);

  const fetchInfo = useCallback(async () => {
    try {
      setLoading(true);
      const res = await fetch(`${API_BASE}/api/portal/${params.token}`);
      const body = (await res.json()) as ApiEnvelope<PortalInfo>;
      if (body.error) {
        // Server-curated error message — explicitly vendor-facing copy
        // the backend chose to render. Safe to surface as the detail
        // line below the static recovery copy.
        setError(body.error.message);
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

  const onDrop = useCallback(
    async (accepted: File[], rejected: FileRejection[]) => {
      // react-dropzone returns rejected files separately from accepted
      // ones. Surface the rejection reason so the vendor knows why
      // nothing happened — silent rejection is hostile UX on a
      // one-shot upload surface.
      const rejectionMessage = rejectionCopy(rejected);
      if (rejectionMessage) {
        setError(rejectionMessage);
        // Don't return: still process any ACCEPTED files alongside.
      }
      if (accepted.length === 0) return;

      // Client-side quota guard: the backend will also enforce this via
      // a 409 on /upload, but blocking it here saves the vendor the
      // wasted POST and gives a clearer error.
      if (info && info.uploadCount + uploaded.length >= info.maxUploads) {
        setError(
          "You've used every upload on this link. Ask your customer for a fresh link if you need to send more.",
        );
        return;
      }

      setUploading(true);
      if (!rejectionMessage) setError(null);
      try {
        for (const file of accepted) {
          const form = new FormData();
          form.append("file", file);
          const res = await fetch(`${API_BASE}/api/portal/${params.token}/upload`, {
            method: "POST",
            body: form,
          });
          const body = (await res.json()) as ApiEnvelope<UploadResponse>;
          if (body.error) throw new Error(body.error.message);
          if (body.data) setUploaded((prev) => [...prev, { name: file.name, id: body.data!.uploadId }]);
        }
      } catch (err) {
        setError(err instanceof Error ? err.message : "Upload failed.");
      } finally {
        setUploading(false);
      }
    },
    [params.token, info, uploaded.length],
  );

  // Quota-exhausted disables the dropzone client-side. The backend
  // still enforces via 409 — this is a UX guard, not a security one.
  const atQuota = info ? info.uploadCount >= info.maxUploads : false;

  const { getRootProps, getInputProps, isDragActive } = useDropzone({
    onDrop,
    accept: {
      "application/pdf": [".pdf"],
      "image/jpeg": [".jpg", ".jpeg"],
      "image/png": [".png"],
    },
    maxSize: 10 * 1024 * 1024,
    disabled: atQuota,
  });

  useEffect(() => {
    fetchInfo();
  }, [fetchInfo]);

  if (loading) {
    return (
      <main className="min-h-screen flex items-center justify-center text-sm text-slate-500">
        Loading…
      </main>
    );
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
          {error && error !== "This link is no longer available." && (
            <p className="text-xs text-slate-400 mt-3">{error}</p>
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
            {info.orgName} asked for your latest compliance documents. Drop them here.
          </p>
        </div>

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
          <input {...getInputProps()} />
          <UploadCloud className="w-12 h-12 mx-auto text-sky-500" />
          <p className="mt-3 text-base font-medium text-slate-800">
            {atQuota
              ? "Upload limit reached on this link"
              : isDragActive
                ? "Drop to upload…"
                : "Drag a file here or click to select"}
          </p>
          <p className="text-sm text-slate-500 mt-1">PDF, JPEG, or PNG · 10 MB max</p>
          <p className="text-xs text-slate-400 mt-2">
            {info.uploadCount} / {info.maxUploads} uploads used on this link
          </p>
          {uploading && <p className="text-xs text-sky-600 mt-2">Uploading…</p>}
        </div>

        {error && (
          <p className="text-sm text-rose-600 text-center">{error}</p>
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
                  <span className="text-xs text-slate-400">Processing…</span>
                </li>
              ))}
            </ul>
          </div>
        )}

        <p className="text-center text-xs text-slate-400">
          Powered by <span className="font-semibold text-sky-700">CompliDrop</span>
        </p>
      </div>
    </main>
  );
}
