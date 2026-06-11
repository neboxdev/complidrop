"use client";

import { useQuery, useMutation, useQueryClient } from "@tanstack/react-query";
import { Card, CardContent } from "@/components/ui/card";
import { Badge } from "@/components/ui/badge";
import { Skeleton } from "@/components/ui/skeleton";
import { ToggleSwitch } from "@/components/ui/switch";
import { api } from "@/lib/api";
import { deliveryStatusLabel } from "@/lib/display-labels";

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
};

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

  const update = useMutation({
    mutationKey: ["reminder-update"],
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
      qc.setQueryData<Reminder[]>(["reminders"], (old) =>
        old?.map((r) => (r.id === vars.id ? { ...r, ...vars.patch } : r)),
      );
    },
    // No snapshot rollback on error: with overlapping toggle mutations a
    // restored snapshot would clobber the other mutation's optimistic patch —
    // the refetch below re-syncs to server truth instead, and the global
    // error toast (meta below) explains the revert.
    onSettled: () => {
      // Only the LAST settling mutation invalidates: an earlier mutation's
      // refetch could return state that predates a still-pending PUT and
      // visually revert it. isMutating counts this mutation while settling.
      if (qc.isMutating({ mutationKey: ["reminder-update"] }) === 1) {
        return qc.invalidateQueries({ queryKey: ["reminders"] });
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
        <p className="text-slate-500">Sent automatically at 8 AM in your org&apos;s local time zone.</p>
      </header>

      <Card>
        <CardContent className="p-6 overflow-x-auto">
          <table className="w-full text-sm">
            <thead className="text-xs uppercase text-slate-500">
              <tr>
                <th className="text-left py-2">Lead time</th>
                <th className="text-left py-2">Notify team</th>
                <th className="text-left py-2">Notify vendor</th>
                <th className="text-left py-2">Active</th>
              </tr>
            </thead>
            <tbody>
              {(reminders.data ?? []).map((r) => (
                <tr key={r.id} className="border-t border-slate-100">
                  <td className="py-3 font-medium">{r.daysBefore} days before</td>
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
        </CardContent>
      </Card>

      <Card>
        <CardContent className="p-6 space-y-4">
          <h2 className="font-semibold text-slate-800">Recent deliveries</h2>
          {history.isLoading ? (
            <div role="status" aria-label="Loading recent deliveries" className="space-y-2">
              {[0, 1, 2].map((i) => (
                <Skeleton key={i} className="h-9 w-full" />
              ))}
            </div>
          ) : (history.data ?? []).length === 0 ? (
            <p className="text-sm text-slate-500">No reminders sent yet.</p>
          ) : (
            <table className="w-full text-sm">
              <thead className="text-xs uppercase text-slate-500">
                <tr>
                  <th className="text-left py-2">When</th>
                  <th className="text-left py-2">Recipient</th>
                  <th className="text-left py-2">Status</th>
                </tr>
              </thead>
              <tbody>
                {(history.data ?? []).map((h) => (
                  <tr key={h.id} className="border-t border-slate-100">
                    <td className="py-2 text-slate-600">{new Date(h.sentAt).toLocaleString()}</td>
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

