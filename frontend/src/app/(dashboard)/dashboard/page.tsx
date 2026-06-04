"use client";

import { Card, CardContent } from "@/components/ui/card";
import { useMe } from "@/hooks/useAuth";
import { useDashboardStats, useExpiryPipeline, useRecentActivity } from "@/hooks/useDashboard";
import { actionLabel } from "@/lib/display-labels";
import Link from "next/link";
import { FileText, Clock, AlertTriangle, ShieldCheck, Users, Zap } from "lucide-react";

export default function DashboardPage() {
  const me = useMe();
  const stats = useDashboardStats();
  const pipeline = useExpiryPipeline();
  const activity = useRecentActivity();

  // Scale the bars to the BIGGEST bucket (min 1 to avoid /0), so a single
  // bucket of 11+ documents isn't visually flattened by a hardcoded max. (#188)
  const p = pipeline.data;
  const pipelineMax = Math.max(
    1,
    p?.expired ?? 0,
    p?.bucket30 ?? 0,
    p?.bucket60 ?? 0,
    p?.bucket90 ?? 0,
    p?.beyond ?? 0,
  );

  return (
    <div className="max-w-6xl mx-auto px-6 py-8 space-y-8">
      <header>
        <h1 className="text-2xl font-semibold text-sky-900">
          Welcome, {me.data?.fullName?.split(" ")[0] ?? "there"}
        </h1>
        <p className="text-slate-500">Here&apos;s a snapshot of where your vendors stand.</p>
      </header>

      <section className="grid grid-cols-1 md:grid-cols-4 gap-4">
        <StatCard icon={FileText} label="Total documents" value={stats.data?.totalDocuments ?? 0} hue="sky" />
        <StatCard icon={ShieldCheck} label="Compliant" value={stats.data?.compliant ?? 0} hue="emerald" />
        <StatCard icon={Clock} label="Expiring ≤ 30d" value={stats.data?.expiringSoon ?? 0} hue="amber" />
        <StatCard icon={AlertTriangle} label="Non-compliant" value={stats.data?.nonCompliant ?? 0} hue="rose" />
      </section>

      <section className="grid grid-cols-1 md:grid-cols-3 gap-4">
        <Card>
          <CardContent className="p-5 flex items-center gap-4">
            <div className="w-10 h-10 rounded-lg bg-sky-50 text-sky-700 flex items-center justify-center">
              <Users className="w-5 h-5" />
            </div>
            <div>
              <p className="text-2xl font-semibold text-slate-900">{stats.data?.totalVendors ?? 0}</p>
              <p className="text-xs text-slate-500">Vendors tracked</p>
            </div>
          </CardContent>
        </Card>
        <Card>
          <CardContent className="p-5 flex items-center gap-4">
            <div className="w-10 h-10 rounded-lg bg-amber-50 text-amber-700 flex items-center justify-center">
              <Zap className="w-5 h-5" />
            </div>
            <div>
              <p className="text-2xl font-semibold text-slate-900">{stats.data?.pendingExtraction ?? 0}</p>
              <p className="text-xs text-slate-500">Still being read</p>
            </div>
          </CardContent>
        </Card>
        <Card>
          <CardContent className="p-5 flex items-center gap-4">
            <div className="w-10 h-10 rounded-lg bg-emerald-50 text-emerald-700 flex items-center justify-center">
              <ShieldCheck className="w-5 h-5" />
            </div>
            <div>
              <p className="text-2xl font-semibold text-slate-900">{stats.data?.complianceRate ?? 0}%</p>
              <p className="text-xs text-slate-500">Compliance rate</p>
            </div>
          </CardContent>
        </Card>
      </section>

      <section>
        <Card>
          <CardContent className="p-6">
            <h2 className="text-base font-semibold text-slate-800 mb-4">When documents expire</h2>
            <div className="grid grid-cols-2 sm:grid-cols-3 md:grid-cols-5 gap-3 text-center">
              <PipelineBucket label="Expired" value={pipeline.data?.expired ?? 0} hue="rose" max={pipelineMax} />
              <PipelineBucket label="Next 30 days" value={pipeline.data?.bucket30 ?? 0} hue="amber" max={pipelineMax} />
              <PipelineBucket label="30–60 days" value={pipeline.data?.bucket60 ?? 0} hue="sky" max={pipelineMax} />
              <PipelineBucket label="60–90 days" value={pipeline.data?.bucket90 ?? 0} hue="sky" max={pipelineMax} />
              <PipelineBucket label="90+ days" value={pipeline.data?.beyond ?? 0} hue="emerald" max={pipelineMax} />
            </div>
          </CardContent>
        </Card>
      </section>

      <section className="grid grid-cols-1 md:grid-cols-2 gap-4">
        <Card>
          <CardContent className="p-6 space-y-3">
            <h2 className="text-base font-semibold text-slate-800">Drop a document</h2>
            <p className="text-sm text-slate-500">Upload a COI, license, or permit — we&apos;ll extract the key fields automatically.</p>
            <Link href="/documents" className="inline-flex items-center text-sm font-medium text-sky-700 hover:text-sky-800">
              Go to Documents →
            </Link>
          </CardContent>
        </Card>
        <Card>
          <CardContent className="p-6 space-y-3">
            <h2 className="text-base font-semibold text-slate-800">Recent activity</h2>
            {activity.isLoading ? (
              <p className="text-sm text-slate-400">Loading…</p>
            ) : (activity.data ?? []).length === 0 ? (
              <p className="text-sm text-slate-500">No recent activity yet.</p>
            ) : (
              <ul className="text-sm divide-y divide-slate-100">
                {(activity.data ?? []).slice(0, 6).map((a) => (
                  <li key={a.id} className="py-2 flex justify-between">
                    <span className="text-slate-700">{actionLabel(a.action)}</span>
                    <span className="text-xs text-slate-400">{new Date(a.createdAt).toLocaleString()}</span>
                  </li>
                ))}
              </ul>
            )}
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

function PipelineBucket({ label, value, hue, max }: { label: string; value: number; hue: "rose" | "amber" | "sky" | "emerald"; max: number }) {
  const hueBar = {
    rose: "bg-rose-500",
    amber: "bg-amber-500",
    sky: "bg-sky-500",
    emerald: "bg-emerald-500",
  }[hue];
  const ratio = Math.min(1, value / Math.max(1, max));
  return (
    <div>
      <div className="h-24 bg-slate-100 rounded-md flex items-end overflow-hidden">
        <div className={`w-full ${hueBar}`} style={{ height: `${Math.max(6, ratio * 100)}%` }} />
      </div>
      <p className="mt-2 text-xs font-medium text-slate-600">{label}</p>
      <p className="text-lg font-semibold text-slate-900">{value}</p>
    </div>
  );
}
