"use client";

import { Card, CardContent } from "@/components/ui/card";
import { useMe } from "@/hooks/useAuth";

export default function SettingsPage() {
  const me = useMe();
  return (
    <div className="max-w-3xl mx-auto px-6 py-8 space-y-4">
      <h1 className="text-2xl font-semibold text-sky-900">Settings</h1>
      <Card>
        <CardContent className="p-6 space-y-2 text-sm">
          <p><span className="text-slate-500">Organization:</span> {me.data?.organizationName}</p>
          <p><span className="text-slate-500">Email:</span> {me.data?.email}</p>
          <p><span className="text-slate-500">Role:</span> {me.data?.role}</p>
          <p><span className="text-slate-500">Plan:</span> {me.data?.plan}</p>
          <p><span className="text-slate-500">Time zone:</span> {me.data?.timeZone}</p>
        </CardContent>
      </Card>
      <Card>
        <CardContent className="p-6 text-sm text-slate-500">
          Billing + team management land in later phases.
        </CardContent>
      </Card>
    </div>
  );
}
