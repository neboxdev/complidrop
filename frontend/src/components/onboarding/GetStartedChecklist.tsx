"use client";

import Link from "next/link";
import { Check, ChevronRight } from "lucide-react";
import { Card, CardContent } from "@/components/ui/card";
import { useDashboardStats } from "@/hooks/useDashboard";
import { useSubscription } from "@/hooks/useSubscription";
import { TrySampleButton } from "@/components/onboarding/SampleData";

export type ChecklistStep = {
  key: string;
  label: string;
  hint: string;
  href: string;
  done: boolean;
};

export type OnboardingChecklist = {
  steps: ChecklistStep[];
  completedCount: number;
  isComplete: boolean;
  isLoading: boolean;
};

/**
 * Derives the "Get started" checklist (#191) from REAL data, so steps tick off as
 * the user actually does them rather than from a stored flag. Step 4 (reminders)
 * is pre-checked: the default expiry reminders are seeded at registration.
 *
 * All four signals come from /api/dashboard/stats (already fetched by the
 * dashboard) — vendor count, the server-derived `anyVendorWithRequirements`
 * boolean, and document count — so the dashboard never has to pull the full
 * vendor list just to render this card.
 */
export function useOnboardingChecklist(): OnboardingChecklist {
  const stats = useDashboardStats();
  const s = stats.data;

  // Done-flags derive from stats alone, so they can gate the subscription fetch below.
  const vendorDone = (s?.totalVendors ?? 0) > 0;
  const requirementsDone = s?.anyVendorWithRequirements ?? false;
  const documentDone = (s?.totalDocuments ?? 0) > 0;
  const allDone = vendorDone && requirementsDone && documentDone; // reminders pre-checked

  // Plan gate (#261): vendor upload links are a Pro entitlement, so the "Collect a
  // document" hint must not recommend them to a Free org (whose link generation the
  // server 403s). Only an explicit `true` unlocks the upload-link phrasing — while
  // the subscription is loading, the plan-safe copy shows. Deliberately NOT part of
  // the checklist's isLoading: the hint wording is cosmetic and must not delay the
  // card (or flash it away) for the sake of billing data. The fetch only runs while
  // the card can actually render (stats loaded, steps incomplete) — a fully-onboarded
  // org's dashboard must not pay a billing query for a hint that never shows (#261
  // review). Settings / vendor detail share the cache entry via the same query key.
  const subscription = useSubscription({ enabled: s !== undefined && !allDone });
  const hasPortal = subscription.data?.hasVendorPortal === true;

  const steps: ChecklistStep[] = [
    {
      key: "vendor",
      label: "Add your first vendor",
      hint: "The business whose documents you track.",
      href: "/vendors",
      done: vendorDone,
    },
    {
      key: "requirements",
      label: "Choose what they must prove",
      hint: "Pick a requirement checklist for the vendor.",
      href: "/vendors",
      done: requirementsDone,
    },
    {
      key: "document",
      label: "Collect a document",
      hint: hasPortal
        ? "Upload a COI, or send the vendor an upload link."
        : "Upload a COI you have on file.",
      href: "/documents",
      done: documentDone,
    },
    {
      key: "reminders",
      label: "Expiry reminders are on",
      hint: "We email you before anything lapses — already set up for you.",
      href: "/reminders",
      done: true,
    },
  ];

  const completedCount = steps.filter((step) => step.done).length;
  return {
    steps,
    completedCount,
    isComplete: completedCount === steps.length,
    isLoading: stats.isLoading,
  };
}

/**
 * The dashboard "Get started" card. Owns its own visibility — renders nothing once
 * every step is done, OR while stats are still loading (so a returning, fully-
 * onboarded user never flashes an all-incomplete checklist on a cold cache) — so
 * the dashboard can mount it unconditionally.
 */
export function GetStartedChecklist({ checklist }: { checklist: OnboardingChecklist }) {
  const { steps, completedCount, isComplete, isLoading } = checklist;
  if (isLoading || isComplete) return null;

  return (
    <Card>
      <CardContent className="p-6">
        <div className="flex items-center justify-between gap-4">
          <div>
            <h2 className="text-base font-semibold text-slate-800">Get started</h2>
            <p className="text-sm text-slate-500">A few steps to your first audit-ready vendor.</p>
          </div>
          <span className="shrink-0 text-sm font-medium text-slate-500">
            {completedCount} of {steps.length}
          </span>
        </div>

        <ol className="mt-4 space-y-2">
          {steps.map((step) =>
            step.done ? (
              <li
                key={step.key}
                className="flex items-center gap-3 rounded-lg border border-emerald-100 bg-emerald-50/60 px-3 py-2.5"
              >
                <span className="flex h-6 w-6 shrink-0 items-center justify-center rounded-full bg-emerald-500 text-white">
                  <Check className="h-3.5 w-3.5" aria-hidden="true" />
                </span>
                <p className="text-sm font-medium text-slate-600 line-through decoration-slate-300">
                  {step.label}
                </p>
              </li>
            ) : (
              <li key={step.key}>
                <Link
                  href={step.href}
                  className="group flex items-center gap-3 rounded-lg border border-slate-200 px-3 py-2.5 hover:border-sky-300 hover:bg-sky-50/40 pointer-coarse:min-h-11"
                >
                  <span
                    className="flex h-6 w-6 shrink-0 rounded-full border-2 border-slate-300"
                    aria-hidden="true"
                  />
                  <div className="min-w-0 flex-1">
                    <p className="text-sm font-medium text-slate-800">{step.label}</p>
                    <p className="text-xs text-slate-500">{step.hint}</p>
                  </div>
                  <ChevronRight
                    className="h-4 w-4 shrink-0 text-slate-400 group-hover:text-sky-600"
                    aria-hidden="true"
                  />
                </Link>
                {/* The cold-start gap (#238): the COI is the one asset Pat may not have. Offer the
                    sample so "Collect a document" can complete with no file on hand. Only on the
                    document step, and only while it's incomplete (this unchecked branch). */}
                {step.key === "document" && (
                  <p className="mt-1.5 pl-9 text-xs text-slate-500">
                    No document handy?{" "}
                    <TrySampleButton
                      variant="link"
                      size="sm"
                      showIcon={false}
                      label="Try a sample certificate"
                      className="h-auto p-0 align-baseline text-xs font-medium"
                    />
                  </p>
                )}
              </li>
            ),
          )}
        </ol>
      </CardContent>
    </Card>
  );
}
