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
  extractionSpend: number;
};

/**
 * The org's subscription/entitlement snapshot (/api/billing/subscription).
 * One shared hook (one query key) so the settings billing tiles, the vendor
 * detail page's portal-link gating, and the onboarding checklist hint (#261)
 * all read the same cache entry instead of re-declaring the query inline.
 */
export function useSubscription() {
  return useQuery<SubscriptionInfo>({
    queryKey: ["billing", "subscription"],
    queryFn: ({ signal }) =>
      api.get<SubscriptionInfo>("/api/billing/subscription", { signal }),
  });
}
