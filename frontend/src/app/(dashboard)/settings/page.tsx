"use client";

import { useQuery, useMutation } from "@tanstack/react-query";
import { toast } from "sonner";
import { Card, CardContent } from "@/components/ui/card";
import { Button } from "@/components/ui/button";
import { Badge } from "@/components/ui/badge";
import { useMe } from "@/hooks/useAuth";
import { api } from "@/lib/api";
import {
  KNOWN_CHECKOUT_PLAN_IDS,
  PLANS,
  type CheckoutPlanId,
} from "@/lib/plans";
import { useSearchParams } from "next/navigation";
import { useEffect } from "react";

type SubscriptionInfo = {
  plan: string;
  status: string;
  documentLimit: number | null;
  documentsUsed: number;
  hasVendorPortal: boolean;
  currentPeriodEnd: string | null;
  extractionSpend: number;
};

export default function SettingsPage() {
  const me = useMe();
  const params = useSearchParams();
  const subscription = useQuery<SubscriptionInfo>({
    queryKey: ["billing", "subscription"],
    queryFn: () => api.get<SubscriptionInfo>("/api/billing/subscription"),
  });

  const checkout = useMutation({
    // The wire vocab matches `KNOWN_CHECKOUT_PLAN_IDS` (#147, ADR 0011).
    // The backend `/api/billing/checkout` endpoint accepts exactly these
    // three values; any other string returns 400 billing.plan_unknown.
    mutationFn: (plan: CheckoutPlanId) =>
      api.post<{ sessionUrl: string }>(
        "/api/billing/checkout",
        { plan },
        { idempotencyKey: crypto.randomUUID() },
      ),
    onSuccess: (res) => {
      window.location.href = res.sessionUrl;
    },
    onError: (err) => toast.error(err instanceof Error ? err.message : "Checkout failed"),
  });

  const portal = useMutation({
    mutationFn: () => api.post<{ sessionUrl: string }>("/api/billing/portal"),
    onSuccess: (res) => {
      window.location.href = res.sessionUrl;
    },
    onError: (err) => toast.error(err instanceof Error ? err.message : "Portal unavailable"),
  });

  useEffect(() => {
    if (params.get("upgraded") === "true") toast.success("Welcome — you're now on a paid plan!");
    if (params.get("canceled") === "true") toast.info("Checkout canceled — no changes made.");
  }, [params]);

  const isPaid = subscription.data?.plan && subscription.data.plan !== "free";

  return (
    <div className="max-w-3xl mx-auto px-6 py-8 space-y-6">
      <h1 className="text-2xl font-semibold text-sky-900">Settings</h1>

      <Card>
        <CardContent className="p-6 space-y-2 text-sm">
          <p><span className="text-slate-500">Organization:</span> {me.data?.organizationName}</p>
          <p><span className="text-slate-500">Email:</span> {me.data?.email}</p>
          <p><span className="text-slate-500">Role:</span> {me.data?.role}</p>
          <p><span className="text-slate-500">Time zone:</span> {me.data?.timeZone}</p>
        </CardContent>
      </Card>

      <Card>
        <CardContent className="p-6 space-y-4">
          <div className="flex items-start justify-between">
            <div>
              <h2 className="text-base font-semibold text-slate-800">Plan & billing</h2>
              <p className="text-sm text-slate-500 capitalize">
                You&apos;re on the <strong>{subscription.data?.plan ?? "free"}</strong> plan
                {subscription.data?.status ? ` · ${subscription.data.status}` : ""}.
              </p>
            </div>
            {isPaid && (
              <Badge className="bg-emerald-100 text-emerald-700 border-transparent">paid</Badge>
            )}
          </div>

          {subscription.data && (
            <div className="grid grid-cols-3 gap-4 text-sm">
              <div className="p-3 rounded-md bg-slate-50">
                <p className="text-xs uppercase text-slate-500">Documents</p>
                <p className="text-lg font-semibold text-slate-900">
                  {subscription.data.documentsUsed}
                  {subscription.data.documentLimit != null && ` / ${subscription.data.documentLimit}`}
                </p>
              </div>
              <div className="p-3 rounded-md bg-slate-50">
                <p className="text-xs uppercase text-slate-500">Vendor portal</p>
                <p className="text-lg font-semibold text-slate-900">
                  {subscription.data.hasVendorPortal ? "On" : "Off"}
                </p>
              </div>
              <div className="p-3 rounded-md bg-slate-50">
                <p className="text-xs uppercase text-slate-500">LLM spend MTD</p>
                <p className="text-lg font-semibold text-slate-900">
                  ${subscription.data.extractionSpend.toFixed(2)}
                </p>
              </div>
            </div>
          )}

          {!isPaid ? (
            // Tiles are driven off `KNOWN_CHECKOUT_PLAN_IDS` + `PLANS`
            // (#147, ADR 0011). Each tile's price + tagline reads from
            // the registry; a price change requires editing exactly one
            // file (`@/lib/plans.ts`) and the tile, the landing card,
            // and the opengraph headline all update together.
            //
            // `annual` is highlighted as the conversion default — see
            // the landing-page pricing section which uses the same
            // `featured` styling on Annual. A future redesign that
            // moves the highlight to Founding would change only this
            // condition.
            <div className="grid grid-cols-1 md:grid-cols-3 gap-3 pt-2">
              {KNOWN_CHECKOUT_PLAN_IDS.map((id) => (
                <PlanCard
                  key={id}
                  name={PLANS[id].label}
                  price={PLANS[id].monthlyPriceLabel}
                  tagline={PLANS[id].tagline ?? ""}
                  featured={id === "annual"}
                  onClick={() => checkout.mutate(id)}
                  pending={checkout.isPending}
                />
              ))}
            </div>
          ) : (
            <div>
              <Button onClick={() => portal.mutate()} disabled={portal.isPending}>
                Manage billing
              </Button>
            </div>
          )}
        </CardContent>
      </Card>
    </div>
  );
}

function PlanCard({
  name,
  price,
  tagline,
  featured,
  onClick,
  pending,
}: {
  name: string;
  price: string;
  tagline: string;
  featured?: boolean;
  onClick: () => void;
  pending: boolean;
}) {
  return (
    <div
      className={`p-4 rounded-md border ${featured ? "border-sky-500 bg-sky-50" : "border-slate-200"}`}
    >
      <p className="text-sm font-medium text-slate-700">{name}</p>
      <p className="text-2xl font-semibold text-slate-900">
        {price}
        <span className="text-xs text-slate-500 font-normal">/mo</span>
      </p>
      <p className="text-xs text-slate-500 mt-1">{tagline}</p>
      <Button className="w-full mt-3" size="sm" onClick={onClick} disabled={pending}>
        {pending ? "Redirecting…" : `Upgrade to ${name}`}
      </Button>
    </div>
  );
}
