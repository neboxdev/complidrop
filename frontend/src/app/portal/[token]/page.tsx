"use client";

import { useParams } from "next/navigation";
import { useState, useCallback, useEffect } from "react";
import { useDropzone } from "react-dropzone";
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
      if (body.error) throw new Error(body.error.message);
      setInfo(body.data);
    } catch (err) {
      setError(err instanceof Error ? err.message : "Could not load portal.");
    } finally {
      setLoading(false);
    }
  }, [params.token]);

  const onDrop = useCallback(
    async (accepted: File[]) => {
      if (accepted.length === 0) return;
      setUploading(true);
      setError(null);
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
    [params.token],
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

  if (!info || error && !info) {
    return (
      <main className="min-h-screen flex items-center justify-center">
        <div className="max-w-md text-center p-6">
          <p className="text-rose-600 font-medium">This link is no longer available.</p>
          <p className="text-sm text-slate-500 mt-2">{error ?? "Ask your customer for a fresh upload link."}</p>
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
          className={`bg-white border-2 border-dashed rounded-xl p-10 text-center shadow-sm cursor-pointer transition ${
            isDragActive ? "border-sky-500 bg-sky-50" : "border-slate-200 hover:border-sky-300 hover:bg-sky-50/30"
          }`}
        >
          <input {...getInputProps()} />
          <UploadCloud className="w-12 h-12 mx-auto text-sky-500" />
          <p className="mt-3 text-base font-medium text-slate-800">
            {isDragActive ? "Drop to upload…" : "Drag a file here or click to select"}
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
