"use client";

import { Card, CardContent } from "@/components/ui/card";
import { useDocuments } from "@/hooks/useDocuments";
import { useMe } from "@/hooks/useAuth";
import Link from "next/link";
import { FileText, Clock, AlertTriangle, ShieldCheck } from "lucide-react";

export default function DashboardPage() {
  const me = useMe();
  const docs = useDocuments();

  const items = docs.data?.items ?? [];
  const total = docs.data?.total ?? 0;
  const expiringSoon = items.filter((d) => d.daysUntilExpiry !== null && d.daysUntilExpiry <= 30 && d.daysUntilExpiry >= 0).length;
  const nonCompliant = items.filter((d) => d.complianceStatus === "NonCompliant").length;
  const compliant = items.filter((d) => d.complianceStatus === "Compliant").length;

  return (
    <div className="max-w-6xl mx-auto px-6 py-8 space-y-8">
      <header>
        <h1 className="text-2xl font-semibold text-sky-900">
          Welcome, {me.data?.fullName?.split(" ")[0] ?? "there"}
        </h1>
        <p className="text-slate-500">Here&apos;s a snapshot of your compliance posture.</p>
      </header>

      <section className="grid grid-cols-1 md:grid-cols-4 gap-4">
        <StatCard icon={FileText} label="Total documents" value={total} hue="sky" />
        <StatCard icon={ShieldCheck} label="Compliant" value={compliant} hue="emerald" />
        <StatCard icon={Clock} label="Expiring ≤ 30 days" value={expiringSoon} hue="amber" />
        <StatCard icon={AlertTriangle} label="Non-compliant" value={nonCompliant} hue="rose" />
      </section>

      <section className="grid grid-cols-1 md:grid-cols-2 gap-4">
        <Card>
          <CardContent className="p-6 space-y-3">
            <h2 className="text-base font-semibold text-slate-800">Drop your first document</h2>
            <p className="text-sm text-slate-500">
              Upload a COI, license, or permit and we&apos;ll extract the key fields automatically.
            </p>
            <Link
              href="/documents"
              className="inline-flex items-center text-sm font-medium text-sky-700 hover:text-sky-800"
            >
              Go to Documents →
            </Link>
          </CardContent>
        </Card>
        <Card>
          <CardContent className="p-6 space-y-3">
            <h2 className="text-base font-semibold text-slate-800">Invite a vendor</h2>
            <p className="text-sm text-slate-500">
              Generate a no-login upload link and text it to your subcontractor.
            </p>
            <Link
              href="/vendors"
              className="inline-flex items-center text-sm font-medium text-sky-700 hover:text-sky-800"
            >
              Manage vendors →
            </Link>
          </CardContent>
        </Card>
      </section>
    </div>
  );
}

function StatCard({
  icon: Icon,
  label,
  value,
  hue,
}: {
  icon: React.ComponentType<{ className?: string }>;
  label: string;
  value: number;
  hue: "sky" | "emerald" | "amber" | "rose";
}) {
  const hueClasses = {
    sky: "bg-sky-50 text-sky-700",
    emerald: "bg-emerald-50 text-emerald-700",
    amber: "bg-amber-50 text-amber-700",
    rose: "bg-rose-50 text-rose-700",
  }[hue];
  return (
    <Card>
      <CardContent className="p-5 flex items-center gap-4">
        <div className={`w-10 h-10 rounded-lg flex items-center justify-center ${hueClasses}`}>
          <Icon className="w-5 h-5" />
        </div>
        <div>
          <p className="text-2xl font-semibold text-slate-900">{value}</p>
          <p className="text-xs text-slate-500">{label}</p>
        </div>
      </CardContent>
    </Card>
  );
}
