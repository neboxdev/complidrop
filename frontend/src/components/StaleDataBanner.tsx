"use client";

import { AlertTriangle, RotateCw } from "lucide-react";
import { Button } from "@/components/ui/button";
import { cn } from "@/lib/utils";
import { GENERIC_FALLBACK_MESSAGE } from "@/lib/api";

/**
 * Discreet "couldn't refresh" indicator shown above a populated list /
 * detail view when a polling refetch has failed but the previous
 * successful response is still cached and rendered. The user keeps
 * reading the stale data; the banner signals that what they're seeing
 * may not reflect the latest state. (#97)
 *
 * Pairs with the page's `refetchInterval` short-circuit on error so the
 * banner appears for as long as the failing state persists — once the
 * user clicks Retry (or the next refetch lands a 200), the banner
 * disappears.
 *
 * Why a banner instead of a sonner toast:
 *   1. The signal must PERSIST so users don't miss it after a few
 *      seconds — a stale list with no surfaced warning is the original
 *      regression #97 was filed against.
 *   2. Toast spam: even with the polling short-circuit, the
 *      enter-error transition fires the toast once on every page mount
 *      that lands on a failing backend. A banner is idempotent — same
 *      visual whether the failure is 5 seconds or 5 minutes old.
 *
 * Why `role="status"` + `aria-live="polite"` instead of the
 * `role="alert"` used by the full-page error card:
 *   - The stale-data case is informational, NOT a blocker. The user
 *     can still see and interact with the cached data. Polite
 *     announcements let assistive tech finish reading the current
 *     element before announcing the banner, matching the visual
 *     "subtle, doesn't grab focus" treatment.
 *   - The full-page error card (no data at all) keeps `role="alert"`
 *     because that DOES block the user — there's nothing else to read.
 */
export interface StaleDataBannerProps {
  /**
   * The server-side error message from the failing query
   * (`query.error?.message`). The component itself falls back to
   * `GENERIC_FALLBACK_MESSAGE` when this is null / empty / whitespace,
   * matching the single-source-of-truth jargon-free fallback policy
   * from #77.
   */
  message?: string | null;
  /**
   * Manual retry handler — typically `() => query.refetch()`. The page
   * wires this up so the banner reads as an immediate recovery
   * affordance, mirroring the Retry button on the full-page error card.
   */
  onRetry: () => void;
  /**
   * Disables the Retry button + spins the icon while a refetch is in
   * flight. Wire to `query.isFetching` so a click can't queue parallel
   * refetches.
   */
  isRetrying?: boolean;
  /**
   * Optional context for the headline (e.g. "documents", "vendor").
   * Defaults to "data". Kept short — the banner is meant to be a
   * one-line indicator, not a detailed explanation.
   */
  noun?: string;
}

export function StaleDataBanner({
  message,
  onRetry,
  isRetrying = false,
  noun = "data",
}: StaleDataBannerProps) {
  // Falls back to the generic jargon-free string when the server
  // message is missing OR whitespace-only — matches the fallback
  // discipline that api.ts enforces for error envelopes (#77).
  const detail = message?.trim() || GENERIC_FALLBACK_MESSAGE;

  return (
    <div
      role="status"
      aria-live="polite"
      className="rounded-lg border border-amber-200 bg-amber-50 px-4 py-2.5 flex items-center gap-3 text-sm"
    >
      <AlertTriangle className="w-4 h-4 text-amber-700 shrink-0" />
      <div className="flex-1 min-w-0">
        <p className="font-medium text-amber-900">
          Couldn&apos;t refresh {noun}
        </p>
        <p className="text-xs text-amber-800 truncate">{detail}</p>
      </div>
      <Button
        variant="outline"
        size="sm"
        onClick={onRetry}
        disabled={isRetrying}
        // aria-busy mirrors the visual spinning-icon affordance for
        // assistive tech: screen-reader users get the same "retry is
        // in flight" signal that the animate-spin class gives sighted
        // users. Pinned by the StaleDataBanner.test.tsx isRetrying
        // case so a regression that drops the visual loading
        // affordance also fails the test. (#97 review — test-quality
        // reviewer)
        aria-busy={isRetrying}
        className="shrink-0 border-amber-300 hover:bg-amber-100"
      >
        <RotateCw
          className={cn("w-3.5 h-3.5 mr-1", isRetrying && "animate-spin")}
        />
        Try again
      </Button>
    </div>
  );
}
