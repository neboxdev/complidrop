"use client";

import Link from "next/link";
import { useQuery, useMutation, useQueryClient } from "@tanstack/react-query";
import { AlertTriangle, RotateCw } from "lucide-react";
import { Card, CardContent } from "@/components/ui/card";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { Skeleton } from "@/components/ui/skeleton";
import { ToggleSwitch } from "@/components/ui/switch";
import { api, GENERIC_FALLBACK_MESSAGE } from "@/lib/api";
import { isAuthError } from "@/lib/query-client";
import { deliveryStatusLabel, relativeTime } from "@/lib/display-labels";

type Reminder = {
  id: string;
  daysBefore: number;
  notifyInternalUser: boolean;
  notifyVendor: boolean;
  isActive: boolean;
  emailSubjectTemplate: string | null;
};

type ReminderHistoryEntry = {
  id: string;
  recipient: string;
  sentAt: string;
  sendDate: string;
  status: string;
  reminderId: string;
  documentId: string;
  /** FP-090: which document/vendor/rung the reminder was about (null if the doc/reminder is gone). */
  documentName: string | null;
  vendorName: string | null;
  daysBefore: number | null;
};

type ReminderGaps = { vendorsWithoutEmail: number; documentsWithoutExpiration: number };

export default function RemindersPage() {
  const qc = useQueryClient();
  const reminders = useQuery<Reminder[]>({
    queryKey: ["reminders"],
    queryFn: ({ signal }) => api.get<Reminder[]>("/api/reminders", { signal }),
  });
  const history = useQuery<ReminderHistoryEntry[]>({
    queryKey: ["reminders", "history"],
    queryFn: ({ signal }) => api.get<ReminderHistoryEntry[]>("/api/reminders/history", { signal }),
  });
  // FP-091 (disclosure half): surface the silent no-send paths.
  const gaps = useQuery<ReminderGaps>({
    queryKey: ["reminders", "gaps"],
    queryFn: ({ signal }) => api.get<ReminderGaps>("/api/reminders/gaps", { signal }),
  });

  const update = useMutation({
    mutationKey: ["reminder-update"],
    // Serialize the PUTs: the endpoint is a full-row write (last commit wins
    // server-side), so two near-simultaneous PUTs could still commit out of
    // order and silently drop the later flip. Scoped mutations run their
    // network calls one-at-a-time IN ORDER while onMutate still fires
    // immediately (query-core runs onMutate before the canRun gate), so the
    // optimistic UX is unchanged (#264 review).
    scope: { id: "reminder-update" },
    // The PUT body is rebuilt from the QUERY CACHE, not the render-scope
    // `reminders.data` snapshot — and onMutate below has already merged this
    // mutation's patch into that cache. Two toggles flipped on one row inside
    // the refetch window therefore each see the other's value instead of
    // sending a stale field that silently reverts the first flip (#264 / FP-093).
    mutationFn: (vars: { id: string; patch: Partial<Reminder> }) => {
      const current = qc.getQueryData<Reminder[]>(["reminders"])?.find((r) => r.id === vars.id);
      const next = { ...current, ...vars.patch };
      return api.put<void>(`/api/reminders/${vars.id}`, {
        notifyInternalUser: next.notifyInternalUser ?? true,
        notifyVendor: next.notifyVendor ?? false,
        isActive: next.isActive ?? true,
        emailSubjectTemplate: next.emailSubjectTemplate ?? null,
      });
    },
    onMutate: async (vars) => {
      // Cancel any in-flight list refetch so a response that predates this
      // flip can't land after it and overwrite the optimistic state.
      await qc.cancelQueries({ queryKey: ["reminders"], exact: true });
      const prevRow = qc.getQueryData<Reminder[]>(["reminders"])?.find((r) => r.id === vars.id);
      qc.setQueryData<Reminder[]>(["reminders"], (old) =>
        old?.map((r) => (r.id === vars.id ? { ...r, ...vars.patch } : r)),
      );
      // Capture ONLY the fields this mutation touches, for a targeted error
      // rollback — a full-row snapshot restore would clobber a sibling
      // mutation's optimistic patch. Known limit: the capture reflects sibling
      // OPTIMISTIC state, so rollback exactness is guaranteed only for the
      // single-pending-mutation case (same switch toggled twice in one flight
      // window + both PUTs failing + refetch failing can restore a phantom
      // value); the last-settler refetch owns repair in every multi-mutation
      // corner. Don't "fix" by capturing differently — the correct rollback
      // target depends on the sibling's outcome, unknowable at capture time.
      const previous = prevRow
        ? (Object.fromEntries(
            (Object.keys(vars.patch) as (keyof Reminder)[]).map((k) => [k, prevRow[k]]),
          ) as Partial<Reminder>)
        : undefined;
      return { previous };
    },
    onError: (_err, vars, ctx) => {
      // Targeted rollback so the failed flip reverts even when the network is
      // down and the onSettled refetch below can't reach the server. Restores
      // only this mutation's own fields (see onMutate).
      if (!ctx?.previous) return;
      const previous = ctx.previous;
      qc.setQueryData<Reminder[]>(["reminders"], (old) =>
        old?.map((r) => (r.id === vars.id ? { ...r, ...previous } : r)),
      );
    },
    onSettled: () => {
      // Only the LAST settling mutation invalidates: an earlier mutation's
      // refetch could return state that predates a still-pending PUT and
      // visually revert it. isMutating counts this mutation while settling.
      // exact: true — toggling reminder config can't change delivery history,
      // so don't drag the 200-row history query along (matches cancelQueries).
      if (qc.isMutating({ mutationKey: ["reminder-update"] }) === 1) {
        return qc.invalidateQueries({ queryKey: ["reminders"], exact: true });
      }
    },
    // Toggles have no local error UI — opt into the global mutation-error
    // toast so a failed save isn't silently lost (the switch would otherwise
    // appear to flip with no persisted change). See lib/query-client.ts.
    meta: { errorToast: true },
  });

  return (
    <div className="max-w-5xl mx-auto px-6 py-8 space-y-6">
      <header>
        <h1 className="text-2xl font-semibold text-sky-900">Reminders</h1>
        {/* FP-093: say WHO gets emailed and WHEN — the table headers alone were jargon. */}
        <p className="text-slate-500">
          We email your team — and, when you turn it on, the vendor — before a document expires.
          Sent automatically at 8 AM in your org&apos;s local time zone.
        </p>
      </header>

      {/* FP-091 disclosure: these recipients/documents silently get no reminder today. */}
      {(gaps.data?.vendorsWithoutEmail ?? 0) > 0 || (gaps.data?.documentsWithoutExpiration ?? 0) > 0 ? (
        <div
          role="status"
          className="flex items-start gap-2 rounded-md border border-amber-200 bg-amber-50 px-4 py-3 text-sm text-amber-900"
        >
          <AlertTriangle className="mt-0.5 h-4 w-4 shrink-0 text-amber-600" aria-hidden="true" />
          <div className="space-y-0.5">
            {(gaps.data?.vendorsWithoutEmail ?? 0) > 0 && (
              <p>
                {gaps.data!.vendorsWithoutEmail} vendor{gaps.data!.vendorsWithoutEmail === 1 ? "" : "s"} ha
                {gaps.data!.vendorsWithoutEmail === 1 ? "s" : "ve"} no contact email — they won&apos;t get
                vendor reminders.{" "}
                <Link href="/vendors" className="font-medium underline">Add their email</Link>.
              </p>
            )}
            {(gaps.data?.documentsWithoutExpiration ?? 0) > 0 && (
              <p>
                {gaps.data!.documentsWithoutExpiration} document
                {gaps.data!.documentsWithoutExpiration === 1 ? "" : "s"} ha
                {gaps.data!.documentsWithoutExpiration === 1 ? "s" : "ve"} no expiration date, so no reminder
                can be scheduled for {gaps.data!.documentsWithoutExpiration === 1 ? "it" : "them"}.{" "}
                <Link href="/documents" className="font-medium underline">Review them</Link>.
              </p>
            )}
          </div>
        </div>
      ) : null}

      <Card>
        <CardContent className="p-6 overflow-x-auto">
          {reminders.isLoading ? (
            <div role="status" aria-label="Loading reminders" className="space-y-2">
              {[0, 1, 2, 3].map((i) => (
                <Skeleton key={i} className="h-9 w-full" />
              ))}
            </div>
          ) : reminders.isError && !isAuthError(reminders.error) ? (
            // FP-094: an outage must not render as an empty schedule (which reads as "reminders off").
            <div className="py-6 text-center" role="alert">
              <AlertTriangle className="mx-auto h-7 w-7 text-rose-500" />
              <p className="mt-2 text-sm font-medium text-slate-800">Couldn&apos;t load your reminder schedule.</p>
              <p className="text-xs text-slate-500">{reminders.error?.message?.trim() || GENERIC_FALLBACK_MESSAGE}</p>
              <Button variant="outline" size="sm" className="mt-3" onClick={() => reminders.refetch()} disabled={reminders.isFetching}>
                <RotateCw className={`w-3.5 h-3.5 mr-1 ${reminders.isFetching ? "animate-spin" : ""}`} /> Retry
              </Button>
            </div>
          ) : (
          <table className="w-full text-sm">
            <thead className="text-xs uppercase text-slate-500">
              <tr>
                <th className="text-left py-2">When</th>
                <th className="text-left py-2">Notify team</th>
                <th className="text-left py-2">Notify vendor</th>
                <th className="text-left py-2">Active</th>
              </tr>
            </thead>
            <tbody>
              {(reminders.data ?? []).map((r) => (
                <tr key={r.id} className="border-t border-slate-100">
                  <td className="py-3 font-medium">{r.daysBefore} days before a document expires</td>
                  <td className="py-3">
                    <ToggleSwitch
                      checked={r.notifyInternalUser}
                      aria-label={`Notify team ${r.daysBefore} days before expiry`}
                      onCheckedChange={(checked) => update.mutate({ id: r.id, patch: { notifyInternalUser: checked } })}
                    />
                  </td>
                  <td className="py-3">
                    <ToggleSwitch
                      checked={r.notifyVendor}
                      aria-label={`Notify vendor ${r.daysBefore} days before expiry`}
                      onCheckedChange={(checked) => update.mutate({ id: r.id, patch: { notifyVendor: checked } })}
                    />
                  </td>
                  <td className="py-3">
                    <ToggleSwitch
                      checked={r.isActive}
                      aria-label={`Reminder ${r.daysBefore} days before expiry active`}
                      onCheckedChange={(checked) => update.mutate({ id: r.id, patch: { isActive: checked } })}
                    />
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
          )}
        </CardContent>
      </Card>

      <Card>
        {/* FP-095: overflow-x-auto so the Status column doesn't clip on phones once rows exist. */}
        <CardContent className="p-6 space-y-4 overflow-x-auto">
          <h2 className="font-semibold text-slate-800">Recent deliveries</h2>
          {history.isLoading ? (
            <div role="status" aria-label="Loading recent deliveries" className="space-y-2">
              {[0, 1, 2].map((i) => (
                <Skeleton key={i} className="h-9 w-full" />
              ))}
            </div>
          ) : history.isError && !isAuthError(history.error) ? (
            // FP-094: a failed fetch must NOT read as "No reminders sent yet" — that's a false claim
            // in the trust-critical direction.
            <div className="py-6 text-center" role="alert">
              <AlertTriangle className="mx-auto h-7 w-7 text-rose-500" />
              <p className="mt-2 text-sm font-medium text-slate-800">Couldn&apos;t load recent deliveries.</p>
              <p className="text-xs text-slate-500">{history.error?.message?.trim() || GENERIC_FALLBACK_MESSAGE}</p>
              <Button variant="outline" size="sm" className="mt-3" onClick={() => history.refetch()} disabled={history.isFetching}>
                <RotateCw className={`w-3.5 h-3.5 mr-1 ${history.isFetching ? "animate-spin" : ""}`} /> Retry
              </Button>
            </div>
          ) : (history.data ?? []).length === 0 ? (
            <p className="text-sm text-slate-500">No reminders sent yet.</p>
          ) : (
            <table className="w-full text-sm">
              <thead className="text-xs uppercase text-slate-500">
                <tr>
                  <th className="text-left py-2">When</th>
                  <th className="text-left py-2">Document</th>
                  <th className="text-left py-2">Vendor</th>
                  <th className="text-left py-2">Reminder</th>
                  <th className="text-left py-2">Recipient</th>
                  <th className="text-left py-2">Status</th>
                </tr>
              </thead>
              <tbody>
                {(history.data ?? []).map((h) => (
                  <tr key={h.id} className="border-t border-slate-100">
                    <td className="py-2 text-slate-600 whitespace-nowrap">{relativeTime(h.sentAt)}</td>
                    <td className="py-2 text-slate-600">
                      {/* FP-090: name + link the document; fall back when it's been removed. */}
                      {h.documentName ? (
                        <Link href={`/documents/${h.documentId}`} className="text-sky-700 hover:underline">
                          {h.documentName}
                        </Link>
                      ) : (
                        <span className="text-slate-400">(removed document)</span>
                      )}
                    </td>
                    <td className="py-2 text-slate-600">{h.vendorName ?? "—"}</td>
                    <td className="py-2 text-slate-600 whitespace-nowrap">
                      {h.daysBefore != null ? `${h.daysBefore} days before` : "—"}
                    </td>
                    <td className="py-2 text-slate-600">{h.recipient}</td>
                    <td className="py-2">
                      <Badge className={statusHue(h.status)}>{deliveryStatusLabel(h.status)}</Badge>
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          )}
        </CardContent>
      </Card>
    </div>
  );
}

function statusHue(status: string): string {
  switch (status) {
    case "delivered": return "bg-emerald-100 text-emerald-700 border-transparent";
    case "bounced":
    case "complained":
    case "failed":
      return "bg-rose-100 text-rose-700 border-transparent";
    default: return "bg-sky-100 text-sky-700 border-transparent";
  }
}

