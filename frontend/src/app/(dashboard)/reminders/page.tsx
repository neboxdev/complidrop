"use client";

import { useQuery, useMutation, useQueryClient } from "@tanstack/react-query";
import { Card, CardContent } from "@/components/ui/card";
import { Badge } from "@/components/ui/badge";
import { api } from "@/lib/api";
import { deliveryStatusLabel } from "@/lib/display-labels";

type Reminder = {
  id: string;
  daysBefore: number;
  notifyInternalUser: boolean;
  notifyVendor: boolean;
  isActive: boolean;
  emailSubjectTemplate: string | null;
  emailBodyTemplate: string | null;
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
    queryFn: () => api.get<Reminder[]>("/api/reminders"),
  });
  const history = useQuery<ReminderHistoryEntry[]>({
    queryKey: ["reminders", "history"],
    queryFn: () => api.get<ReminderHistoryEntry[]>("/api/reminders/history"),
  });

  const update = useMutation({
    mutationFn: (vars: { id: string; patch: Partial<Reminder> }) => {
      const current = reminders.data?.find((r) => r.id === vars.id);
      const next = { ...current, ...vars.patch };
      return api.put<void>(`/api/reminders/${vars.id}`, {
        notifyInternalUser: next.notifyInternalUser ?? true,
        notifyVendor: next.notifyVendor ?? false,
        isActive: next.isActive ?? true,
        emailSubjectTemplate: next.emailSubjectTemplate ?? null,
        emailBodyTemplate: next.emailBodyTemplate ?? null,
      });
    },
    onSuccess: () => qc.invalidateQueries({ queryKey: ["reminders"] }),
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
                    <Toggle
                      on={r.notifyInternalUser}
                      onToggle={() => update.mutate({ id: r.id, patch: { notifyInternalUser: !r.notifyInternalUser } })}
                    />
                  </td>
                  <td className="py-3">
                    <Toggle
                      on={r.notifyVendor}
                      onToggle={() => update.mutate({ id: r.id, patch: { notifyVendor: !r.notifyVendor } })}
                    />
                  </td>
                  <td className="py-3">
                    <Toggle
                      on={r.isActive}
                      onToggle={() => update.mutate({ id: r.id, patch: { isActive: !r.isActive } })}
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
            <p className="text-sm text-slate-400">Loading…</p>
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

function Toggle({ on, onToggle }: { on: boolean; onToggle: () => void }) {
  // The clickable button carries a ≥44px hit area on touch
  // (`pointer-coarse:min-h-11/min-w-11`, WCAG 2.5.5) while the visual track
  // stays compact — the track is an inner span so enlarging the target doesn't
  // enlarge the pill. Switch SEMANTICS (role="switch" / aria-checked /
  // aria-label / focus ring / non-color cue) are intentionally left to the
  // accessibility-hardening ticket #189; #181 owns the touch-target only.
  return (
    <button
      onClick={onToggle}
      className="inline-flex items-center justify-center pointer-coarse:min-h-11 pointer-coarse:min-w-11"
    >
      <span
        className={`relative inline-flex h-5 w-9 items-center rounded-full transition ${
          on ? "bg-sky-500" : "bg-slate-200"
        }`}
      >
        <span
          className={`inline-block h-3 w-3 transform rounded-full bg-white transition ${
            on ? "translate-x-5" : "translate-x-1"
          }`}
        />
      </span>
    </button>
  );
}
