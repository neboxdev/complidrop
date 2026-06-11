"use client";

import { useId, useState } from "react";
import { toast } from "sonner";
import { FileText, FileSpreadsheet, Calendar } from "lucide-react";
import { Card, CardContent } from "@/components/ui/card";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { api, GENERIC_FALLBACK_MESSAGE } from "@/lib/api";

export default function ExportPage() {
  const [from, setFrom] = useState(isoDaysAgo(30));
  const [to, setTo] = useState(isoDaysAgo(0));
  const [busy, setBusy] = useState(false);
  // yyyy-MM-dd strings compare correctly as strings; empty inputs don't block.
  const rangeInverted = Boolean(from && to && from > to);
  // a11y: wire the From/To date inputs to their labels via htmlFor +
  // id so screen readers announce each input with its date-range
  // context. Missed by the original #76 sweep — only the auth and
  // vendor dashboard forms were touched. (#76 followup)
  const fromId = useId();
  const toId = useId();

  const download = async (path: string, filename: string) => {
    setBusy(true);
    try {
      // api.getBlob (#254) replaces this page's old bare fetch: same cookie
      // transport and jargon-free error discipline (#77), plus two things the
      // bare fetch lacked — the coalesced silent 401-refresh (a stale session
      // recovers instead of failing the click) and the server's friendly
      // envelope message on failure (the bare fetch collapsed every error to
      // the generic fallback).
      const blob = await api.getBlob(path);
      const url = URL.createObjectURL(blob);
      const link = document.createElement("a");
      link.href = url;
      link.download = filename;
      link.click();
      URL.revokeObjectURL(url);
      toast.success("Download started");
    } catch (err) {
      // ApiError.message is already friendly (server envelope message or the
      // generic fallback); the truthy guard defends against a future
      // blob()/url-revoke throw whose .message could be browser jargon.
      const message =
        err instanceof Error && err.message ? err.message : GENERIC_FALLBACK_MESSAGE;
      toast.error(message);
    } finally {
      setBusy(false);
    }
  };

  return (
    <div className="max-w-4xl mx-auto px-6 py-8 space-y-6">
      <header>
        <h1 className="text-2xl font-semibold text-sky-900">Export</h1>
        <p className="text-slate-500">Download audit-ready reports and raw data.</p>
      </header>

      <Card>
        <CardContent className="p-6 space-y-4">
          <div className="flex items-center gap-2">
            <FileText className="w-5 h-5 text-sky-600" />
            <h2 className="font-semibold text-slate-800">PDF audit report</h2>
          </div>
          <p className="text-sm text-slate-500">
            A formatted PDF covering all active documents plus the audit log for the date range you pick.
            Good to forward to an insurer or compliance officer.
          </p>
          <div className="grid grid-cols-1 sm:grid-cols-2 gap-3 sm:items-end">
            <div>
              <label htmlFor={fromId} className="text-xs text-slate-500 flex items-center gap-1">
                <Calendar className="w-3 h-3" /> From
              </label>
              <Input id={fromId} type="date" min="2000-01-01" max="2100-12-31" value={from} onChange={(e) => setFrom(e.target.value)} className="mt-1" />
            </div>
            <div>
              <label htmlFor={toId} className="text-xs text-slate-500 flex items-center gap-1">
                <Calendar className="w-3 h-3" /> To
              </label>
              <Input id={toId} type="date" min="2000-01-01" max="2100-12-31" value={to} onChange={(e) => setTo(e.target.value)} className="mt-1" />
            </div>
          </div>
          {/* Scope clarification (#197): the date range only bounds the audit/
              activity-log section. The documents table in the PDF always lists
              every active document regardless of these dates. */}
          <p className="text-xs text-slate-500">
            The date range filters the <strong className="font-medium">activity log</strong> only —
            the documents table always lists all of your active documents.
          </p>
          {/* Inverted-range guard (#262): catch the inversion before any request —
              instant feedback beats a round-trip, even now that api.getBlob (#254)
              would surface the API's friendly 400 message on the toast. */}
          {rangeInverted && (
            <p className="text-xs text-rose-600" role="alert">
              The start date must be on or before the end date.
            </p>
          )}
          <Button
            disabled={busy || rangeInverted}
            onClick={() =>
              download(
                `/api/export/audit-report?from=${encodeURIComponent(from)}&to=${encodeURIComponent(to)}`,
                `complidrop-audit-${from}-${to}.pdf`,
              )
            }
          >
            Download PDF
          </Button>
        </CardContent>
      </Card>

      <Card>
        <CardContent className="p-6 space-y-4">
          <div className="flex items-center gap-2">
            <FileSpreadsheet className="w-5 h-5 text-emerald-600" />
            <h2 className="font-semibold text-slate-800">CSV export</h2>
          </div>
          <p className="text-sm text-slate-500">
            All active documents as CSV — useful for spreadsheets, BI tools, or one-off reporting.
          </p>
          <Button
            variant="outline"
            disabled={busy}
            onClick={() => download("/api/export/csv", `complidrop-documents-${to}.csv`)}
          >
            Download CSV
          </Button>
        </CardContent>
      </Card>
    </div>
  );
}

function isoDaysAgo(days: number): string {
  const d = new Date();
  d.setDate(d.getDate() - days);
  return d.toISOString().slice(0, 10);
}
