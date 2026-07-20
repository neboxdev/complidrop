"use client";

import { useState } from "react";
import { Card, CardContent } from "@/components/ui/card";
import { Skeleton } from "@/components/ui/skeleton";
import { Button } from "@/components/ui/button";
import { useMe, useCompleteOnboarding } from "@/hooks/useAuth";
import { useDashboardStats, useExpiryPipeline, useRecentActivity, type ExpiryPipeline } from "@/hooks/useDashboard";
import { actionLabel, relativeTime } from "@/lib/display-labels";
import { GENERIC_FALLBACK_MESSAGE } from "@/lib/api";
import { isAuthError } from "@/lib/query-client";
import { peekTourRestart, clearTourRestart } from "@/lib/onboarding";
import { WelcomeModal } from "@/components/onboarding/WelcomeModal";
import { GetStartedChecklist, useOnboardingChecklist } from "@/components/onboarding/GetStartedChecklist";
import { SampleDataBanner, TrySampleButton } from "@/components/onboarding/SampleData";
import Link from "next/link";
import { FileText, Clock, AlertTriangle, ShieldCheck, Users, Zap, RotateCw } from "lucide-react";

export default function DashboardPage() {
  const me = useMe();
  const stats = useDashboardStats();
  const pipeline = useExpiryPipeline();
  const activity = useRecentActivity();
  const onboarding = useOnboardingChecklist();
  const completeOnboarding = useCompleteOnboarding();

  // First-run welcome modal (#191). Auto-opens for a never-onboarded user, or when
  // "Restart tour" in Settings hands off via a one-shot localStorage flag (read once
  // here at mount — the dashboard is client-only so this lazy read is hydration-safe).
  const [tourDismissed, setTourDismissed] = useState(false);
  const [wantsTourRestart] = useState(() => peekTourRestart());

  const showWelcome =
    !tourDismissed && (wantsTourRestart || me.data?.hasCompletedOnboarding === false);

  // An EXPLICIT dismissal (Skip / the X / the final CTA) persists completion so the
  // tour never returns. A persist failure is low-stakes — the tour simply re-appears
  // next visit (the server flag stays false), which is the natural recovery, so we
  // don't nag with a toast; the `onError` just keeps the rejected mutation from
  // surfacing as an unhandled error. (#318 FP-046)
  function completeWelcome() {
    setTourDismissed(true);
    clearTourRestart();
    if (me.data && !me.data.hasCompletedOnboarding) {
      completeOnboarding.mutate(undefined, { onError: () => {} });
    }
  }

  // An INCIDENTAL dismissal (backdrop click / Escape) minimizes: closes for this
  // session WITHOUT persisting, so a stray click no longer ends the tour forever —
  // it returns next visit, and "Restart tour" still lives in Settings. (#318 FP-046)
  function minimizeWelcome() {
    setTourDismissed(true);
    clearTourRestart();
  }

  // Show the get-started checklist + stat grid ONLY when stats loaded successfully.
  // On an outage we must render NEITHER a zeroed grid NOR the first-run checklist —
  // an API hiccup must never make a paying account look brand-new-and-empty (#318
  // FP-040). Loading shows skeletons (not hard zeros, #318 FP-046).
  const hasData = (stats.data?.totalDocuments ?? 0) > 0;

  return (
    <div className="max-w-6xl mx-auto px-6 py-8 space-y-8">
      <header>
        <h1 className="text-2xl font-semibold text-sky-900">
          Welcome, {me.data?.fullName?.split(" ")[0] ?? "there"}
        </h1>
        <p className="text-slate-500">Here&apos;s a snapshot of where your vendors stand.</p>
      </header>

      <WelcomeModal open={showWelcome} onComplete={completeWelcome} onMinimize={minimizeWelcome} />

      {/* Auto-hides once every step is done — and is gated on a SUCCESSFUL stats read so
          a transient outage never re-surfaces it all-unchecked over a real account (FP-040). */}
      {stats.isSuccess && <GetStartedChecklist checklist={onboarding} />}

      {/* Shown only while sample-demo data exists; renders nothing otherwise. */}
      <SampleDataBanner />

      {/* Stat region: error card on failure (never a zeroed grid), skeletons while
          loading, the real grid once we have data, and nothing for a brand-new
          (zero-document) account — the checklist guides them instead. (#318 FP-040/046) */}
      {stats.isError ? (
        <DashboardError
          message={stats.error?.message?.trim() || GENERIC_FALLBACK_MESSAGE}
          onRetry={() => {
            void stats.refetch();
            void pipeline.refetch();
          }}
          isRetrying={stats.isFetching}
        />
      ) : stats.isLoading ? (
        <StatGridSkeleton />
      ) : hasData ? (
        <>
          <section className="grid grid-cols-1 md:grid-cols-4 gap-4">
            <StatCard icon={FileText} label="Total documents" value={stats.data?.totalDocuments ?? 0} hue="sky" href="/documents" />
            <StatCard icon={ShieldCheck} label="Compliant" value={stats.data?.compliant ?? 0} hue="emerald" href="/documents?status=Compliant" />
            <StatCard icon={Clock} label="Expiring within 30 days" value={stats.data?.expiringSoon ?? 0} hue="amber" href="/documents?status=ExpiringSoon" />
            <StatCard icon={AlertTriangle} label="Non-compliant" value={stats.data?.nonCompliant ?? 0} hue="rose" href="/documents?status=NonCompliant" />
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

          {/* The pipeline is its OWN request, so it fails independently of stats.
              Rendering buckets requires loaded data — a failed or still-pending read
              shows an error or a skeleton, never `?? 0` zeros, which on this card
              would read as "nothing is expired" over an org with expired COIs (#368).
              The `!isAuthError` guard routes an EXPIRED-SESSION 401 to the global
              redirect (lib/query-client.ts) instead of this card, matching
              documents/vendors; that case falls through to the skeleton, which claims
              nothing. */}
          <section>
            <Card>
              <CardContent className="p-6">
                <h2 className="text-base font-semibold text-slate-800 mb-4">When documents expire</h2>
                {pipeline.isSuccess ? (
                  <PipelineBuckets data={pipeline.data} />
                ) : pipeline.isError && !isAuthError(pipeline.error) ? (
                  <div className="space-y-2" role="alert">
                    <p className="text-sm text-slate-600">We couldn&apos;t load when your documents expire.</p>
                    {/* Server copy (already sanitized to friendly text by api.ts) or the
                        shared fallback — the house error-card pattern in CLAUDE.md. */}
                    <p className="text-xs text-slate-500">
                      {pipeline.error?.message?.trim() || GENERIC_FALLBACK_MESSAGE}
                    </p>
                    <Button variant="outline" size="sm" onClick={() => void pipeline.refetch()} disabled={pipeline.isFetching}>
                      <RotateCw className={pipeline.isFetching ? "w-3.5 h-3.5 mr-1 animate-spin" : "w-3.5 h-3.5 mr-1"} />
                      Try again
                    </Button>
                  </div>
                ) : (
                  <PipelineSkeleton />
                )}
              </CardContent>
            </Card>
          </section>
        </>
      ) : null}

      <section className="grid grid-cols-1 md:grid-cols-2 gap-4">
        <Card>
          <CardContent className="p-6 space-y-3">
            <h2 className="text-base font-semibold text-slate-800">Drop a document</h2>
            <p className="text-sm text-slate-500">Upload a COI, license, or permit — we&apos;ll extract the key fields automatically.</p>
            <Link href="/documents" className="inline-flex items-center text-sm font-medium text-sky-700 hover:text-sky-800">
              Go to Documents →
            </Link>
            {/* No COI on hand? Seed a sample and land on a real verdict in ~a minute (#238).
                Hidden once a sample exists — the banner above owns it then. */}
            {!stats.data?.hasSampleData && (
              <div className="border-t border-slate-100 pt-3">
                <p className="text-xs text-slate-500">No certificate handy?</p>
                <TrySampleButton className="mt-1.5" />
              </div>
            )}
          </CardContent>
        </Card>
        <Card>
          <CardContent className="p-6 space-y-3">
            <h2 className="text-base font-semibold text-slate-800">Recent activity</h2>
            {activity.isError ? (
              // A failed fetch must NOT read as "nothing has ever happened" — that's the
              // trust-critical false-empty FP-040 flags for the feed too. (#318)
              <div className="space-y-2" role="alert">
                <p className="text-sm text-slate-600">We couldn&apos;t load your recent activity.</p>
                <Button variant="outline" size="sm" onClick={() => void activity.refetch()} disabled={activity.isFetching}>
                  <RotateCw className={activity.isFetching ? "w-3.5 h-3.5 mr-1 animate-spin" : "w-3.5 h-3.5 mr-1"} />
                  Try again
                </Button>
              </div>
            ) : activity.isLoading ? (
              // Skeleton rows mirror the activity list's shape so the card
              // reserves its space (no layout shift when the data lands). (#197)
              <ul role="status" aria-label="Loading recent activity" className="divide-y divide-slate-100">
                {[0, 1, 2, 3].map((i) => (
                  <li key={i} className="py-2 flex items-center justify-between">
                    <Skeleton className="h-4 w-32" />
                    <Skeleton className="h-3 w-20" />
                  </li>
                ))}
              </ul>
            ) : (activity.data ?? []).length === 0 ? (
              <p className="text-sm text-slate-500">No recent activity yet.</p>
            ) : (
              <ul className="text-sm divide-y divide-slate-100">
                {(activity.data ?? []).slice(0, 6).map((a) => (
                  <li key={a.id} className="py-2 flex justify-between gap-3">
                    <span className="text-slate-700">{actionLabel(a.action)}</span>
                    <span className="text-xs text-slate-500 shrink-0">{relativeTime(a.createdAt)}</span>
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

// Error card for the stat region (#318 FP-040): on a stats outage we show this +
// Retry instead of a zeroed grid that lies a paying account is brand-new-empty.
// `message` is already jargon-free (api.ts sanitizes every ApiError to the server
// copy or GENERIC_FALLBACK_MESSAGE — never raw statusText/status codes/TypeErrors).
function DashboardError({
  message,
  onRetry,
  isRetrying,
}: {
  message: string;
  onRetry: () => void;
  isRetrying: boolean;
}) {
  return (
    <Card>
      <CardContent className="p-8 text-center" role="alert">
        <AlertTriangle className="w-8 h-8 mx-auto text-rose-500" />
        <p className="mt-3 text-sm font-medium text-slate-800">We couldn&apos;t load your dashboard.</p>
        <p className="mt-1 text-xs text-slate-500">{message}</p>
        <Button variant="outline" size="sm" className="mt-4" onClick={onRetry} disabled={isRetrying}>
          <RotateCw className={isRetrying ? "w-3.5 h-3.5 mr-1 animate-spin" : "w-3.5 h-3.5 mr-1"} />
          Retry
        </Button>
      </CardContent>
    </Card>
  );
}

// Loading placeholder for the stat region (#318 FP-046): skeletons that mirror the
// grid + pipeline shape, so a cold load reserves space instead of flashing hard zeros.
function StatGridSkeleton() {
  return (
    <div className="space-y-8" role="status" aria-label="Loading your dashboard">
      <section className="grid grid-cols-1 md:grid-cols-4 gap-4">
        {[0, 1, 2, 3].map((i) => (
          <Card key={i}>
            <CardContent className="p-5 flex items-center gap-4">
              <Skeleton className="w-10 h-10 rounded-lg" />
              <div className="space-y-2">
                <Skeleton className="h-6 w-10" />
                <Skeleton className="h-3 w-24" />
              </div>
            </CardContent>
          </Card>
        ))}
      </section>
      <section>
        <Card>
          <CardContent className="p-6 space-y-4">
            <Skeleton className="h-4 w-40" />
            <PipelineSkeleton labelled={false} />
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
  href,
}: {
  icon: React.ComponentType<{ className?: string }>;
  label: string;
  value: number;
  hue: "sky" | "emerald" | "amber" | "rose";
  /** When set, the whole card links to a pre-filtered documents view (FP-041). */
  href?: string;
}) {
  const hueClasses = {
    sky: "bg-sky-50 text-sky-700",
    emerald: "bg-emerald-50 text-emerald-700",
    amber: "bg-amber-50 text-amber-700",
    rose: "bg-rose-50 text-rose-700",
  }[hue];
  const card = (
    <Card className={href ? "h-full transition-shadow hover:shadow-md" : undefined}>
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
  return href ? (
    <Link
      href={href}
      aria-label={`${label}: ${value}. View these documents.`}
      className="block rounded-xl focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring"
    >
      {card}
    </Link>
  ) : (
    card
  );
}

// The five buckets, rendered ONLY from a loaded pipeline read (#368). Taking a
// non-optional `ExpiryPipeline` is the point: there is no `?? 0` fallback left to
// turn a failed request into a confident "Expired: 0".
function PipelineBuckets({ data }: { data: ExpiryPipeline }) {
  // Scale the bars to the BIGGEST bucket (min 1 to avoid /0), so a single
  // bucket of 11+ documents isn't visually flattened by a hardcoded max. (#188)
  const max = Math.max(1, data.expired, data.bucket30, data.bucket60, data.bucket90, data.beyond);
  return (
    <div className="grid grid-cols-2 sm:grid-cols-3 md:grid-cols-5 gap-3 text-center">
      <PipelineBucket label="Expired" value={data.expired} hue="rose" max={max} href="/documents?status=Expired" />
      <PipelineBucket label="Next 30 days" value={data.bucket30} hue="amber" max={max} href="/documents?expiresWithin=30" />
      <PipelineBucket label="30–60 days" value={data.bucket60} hue="sky" max={max} />
      <PipelineBucket label="60–90 days" value={data.bucket90} hue="sky" max={max} />
      <PipelineBucket label="90+ days" value={data.beyond} hue="emerald" max={max} />
    </div>
  );
}

// Loading placeholder for the bucket row — mirrors the grid's shape so the card
// reserves its space. Shared with StatGridSkeleton, which renders the same row for
// the cold-load case; `labelled={false}` there because that skeleton's own wrapper
// already carries role="status", and nesting a second one would announce the region
// twice to a screen reader.
function PipelineSkeleton({ labelled = true }: { labelled?: boolean }) {
  return (
    <div
      className="grid grid-cols-2 sm:grid-cols-3 md:grid-cols-5 gap-3"
      role={labelled ? "status" : undefined}
      aria-label={labelled ? "Loading when documents expire" : undefined}
    >
      {[0, 1, 2, 3, 4].map((i) => (
        <Skeleton key={i} className="h-24 rounded-md" />
      ))}
    </div>
  );
}

function PipelineBucket({ label, value, hue, max, href }: { label: string; value: number; hue: "rose" | "amber" | "sky" | "emerald"; max: number; href?: string }) {
  const hueBar = {
    rose: "bg-rose-500",
    amber: "bg-amber-500",
    sky: "bg-sky-500",
    emerald: "bg-emerald-500",
  }[hue];
  const ratio = Math.min(1, value / Math.max(1, max));
  // A zero-count bucket draws NO colored bar (#318 FP-049) — the old `Math.max(6, …)`
  // floor painted a 6%-tall stub even for 0, reading as "a few" at a glance.
  const heightPct = value === 0 ? 0 : Math.max(6, ratio * 100);
  const body = (
    <>
      <div className="h-24 bg-slate-100 rounded-md flex items-end overflow-hidden">
        <div className={`w-full ${hueBar}`} style={{ height: `${heightPct}%` }} />
      </div>
      <p className="mt-2 text-xs font-medium text-slate-600">{label}</p>
      <p className="text-lg font-semibold text-slate-900">{value}</p>
    </>
  );
  return href ? (
    <Link
      href={href}
      aria-label={`${label}: ${value} documents. View them.`}
      className="block rounded-md focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring hover:opacity-90"
    >
      {body}
    </Link>
  ) : (
    <div>{body}</div>
  );
}
