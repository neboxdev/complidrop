"use client";

import { useQuery } from "@tanstack/react-query";
import { api } from "@/lib/api";

export type SubscriptionInfo = {
  plan: string;
  status: string;
  documentLimit: number | null;
  documentsUsed: number;
  hasVendorPortal: boolean;
  currentPeriodEnd: string | null;
  /** True when the active sub is set to cancel at the period end (#323): the card says "Ends on"
   * instead of "Renews on". */
  cancelAtPeriodEnd: boolean;
  extractionSpend: number;
};

/**
 * The org's subscription/entitlement snapshot (/api/billing/subscription).
 * One shared hook (one query key) so the settings billing tiles, the vendor
 * detail page's portal-link gating, and the onboarding checklist hint (#261)
 * all read the same cache entry instead of re-declaring the query inline.
 *
 * `enabled` passes through so a consumer that only needs the data
 * conditionally (the checklist hint — invisible once onboarding completes)
 * can skip the fetch instead of paying a Subscriptions seek + Documents
 * COUNT for copy that never renders (#261 review).
 */
export function useSubscription(options?: { enabled?: boolean }) {
  return useQuery<SubscriptionInfo>({
    queryKey: ["billing", "subscription"],
    queryFn: ({ signal }) =>
      api.get<SubscriptionInfo>("/api/billing/subscription", { signal }),
    enabled: options?.enabled ?? true,
  });
}
