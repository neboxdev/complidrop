"use client";

import { useMutation } from "@tanstack/react-query";
import { toast } from "sonner";
import { Card, CardContent } from "@/components/ui/card";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { Badge } from "@/components/ui/badge";
import { useMe, useUpdateOrganization, type Me } from "@/hooks/useAuth";
import { useSubscription } from "@/hooks/useSubscription";
import { api, GENERIC_FALLBACK_MESSAGE } from "@/lib/api";
import { cn } from "@/lib/utils";
import { listTimeZones, describeNextSend, CURATED_TIMEZONES } from "@/lib/timezones";
import { SecuritySection, DataExportSection, DangerZone } from "./account-management";
import {
  KNOWN_CHECKOUT_PLAN_IDS,
  PLANS,
  type CheckoutPlanId,
} from "@/lib/plans";
import { useRouter, useSearchParams } from "next/navigation";
import { useEffect, useId, useMemo, useRef, useState } from "react";
import { requestTourRestart, resetOnboardingTips } from "@/lib/onboarding";

// Map the raw Stripe subscription status to friendly copy — never interpolate
// "past_due" / "incomplete_expired" into the UI. The default branch catches any
// status we haven't enumerated so a new Stripe state can't leak as a raw code.
// (#188)
function billingStatusNotice(
  status: string | undefined,
): { text: string; warn: boolean } | null {
  switch ((status ?? "").toLowerCase()) {
    case "":
    case "active":
      return null;
    case "trialing":
      return { text: "You're in your free trial.", warn: false };
    case "past_due":
      return {
        text: "Your last payment didn't go through. Update your card to keep your account active.",
        warn: true,
      };
    case "canceled":
    case "cancelled":
      return { text: "Your plan is canceled and won't renew.", warn: true };
    case "unpaid":
      return {
        text: "Your account has an unpaid invoice — update your billing to restore full access.",
        warn: true,
      };
    case "incomplete":
    case "incomplete_expired":
      return { text: "Your subscription setup didn't finish. Try upgrading again.", warn: true };
    default:
      return { text: "There's a problem with your billing. Open Manage billing to fix it.", warn: true };
  }
}

// Skeleton for the billing card while the subscription loads — so a slow query
// never flashes "free" + upgrade tiles at a paying customer (#316 FP-111).
// Hoisted per the react-hooks/static-components rule.
function BillingSkeleton() {
  return (
    <div className="grid grid-cols-1 sm:grid-cols-3 gap-4" aria-hidden="true">
      {[0, 1, 2].map((i) => (
        <div key={i} className="p-3 rounded-md bg-slate-50">
          <div className="h-3 w-20 rounded bg-slate-200 motion-safe:animate-pulse" />
          <div className="mt-2 h-5 w-12 rounded bg-slate-200 motion-safe:animate-pulse" />
        </div>
      ))}
    </div>
  );
}

// currentPeriodEnd is a real instant, so the viewer's local zone is correct here
// (contrast formatCalendarDate, which pins UTC for date-only facts). (#316 FP-115)
function formatRenewalDate(iso: string): string {
  const d = new Date(iso);
  if (Number.isNaN(d.getTime())) return "";
  return d.toLocaleDateString(undefined, { year: "numeric", month: "long", day: "numeric" });
}

export default function SettingsPage() {
  const me = useMe();
  const params = useSearchParams();
  const subscription = useSubscription();

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

  // Only "paid" once the subscription has actually LOADED as a paid plan — never
  // while loading or on error, or a transient API hiccup would render a paying
  // customer as free with live Upgrade tiles. (#316 FP-111)
  const isPaid = subscription.isSuccess && subscription.data.plan !== "free";
  const { refetch: refetchSub } = subscription;

  // ?canceled=true is terminal + truthful (the user backed out) — toast it.
  useEffect(() => {
    if (params.get("canceled") === "true") toast.info("Checkout canceled — no changes made.");
  }, [params]);

  // ?upgraded=true is NOT terminal: the Stripe webhook that flips the plan to
  // paid can lag a few seconds. Poll the subscription until it lands rather than
  // toasting a success off the URL param (which would lie if the webhook hasn't
  // arrived), and hide the upgrade tiles while we wait so they can't re-checkout.
  // (#316 FP-114). `activating` is DERIVED (not synced state) — show it while we
  // returned from checkout, the plan hasn't flipped, and we haven't timed out.
  const upgraded = params.get("upgraded") === "true";
  const [gaveUpActivating, setGaveUpActivating] = useState(false);
  const activating = upgraded && !isPaid && !gaveUpActivating;
  useEffect(() => {
    if (!activating) return;
    const poll = setInterval(() => refetchSub(), 3000);
    const giveUp = setTimeout(() => setGaveUpActivating(true), 30000);
    return () => {
      clearInterval(poll);
      clearTimeout(giveUp);
    };
  }, [activating, refetchSub]);

  // Celebrate exactly once when the plan actually lands paid after an upgrade —
  // a ref guard (not state) so the toast can't re-fire on later refetches.
  const celebratedUpgrade = useRef(false);
  useEffect(() => {
    if (upgraded && isPaid && !celebratedUpgrade.current) {
      celebratedUpgrade.current = true;
      toast.success("You're all set — welcome to your paid plan!");
    }
  }, [upgraded, isPaid]);

  return (
    <div className="max-w-3xl mx-auto px-6 py-8 space-y-6">
      <h1 className="text-2xl font-semibold text-sky-900">Settings</h1>

      {me.data && (
        // `key` re-seeds the form's local state if the org name/zone changes
        // from an external source (a background /me refetch, or another tab
        // saving via the shared Me cache) — without it the inputs would keep
        // showing stale edits against a moved baseline.
        <OrgSettingsForm
          key={`${me.data.organizationName}|${me.data.timeZone}`}
          me={me.data}
        />
      )}

      <Card>
        <CardContent className="p-6 space-y-4">
          <div className="flex items-start justify-between">
            <div>
              <h2 className="text-base font-semibold text-slate-800">Plan & billing</h2>
              {subscription.isSuccess && (
                <p className="text-sm text-slate-500">
                  You&apos;re on the{" "}
                  <strong className="capitalize">{subscription.data.plan}</strong> plan.
                </p>
              )}
            </div>
            {isPaid && (
              <Badge className="bg-emerald-100 text-emerald-700 border-transparent">paid</Badge>
            )}
          </div>

          {subscription.isLoading && <BillingSkeleton />}

          {subscription.isError && (
            <div className="rounded-md bg-rose-50 px-3 py-3 text-sm text-rose-700">
              <p>We couldn&apos;t load your billing details.</p>
              <Button variant="outline" size="sm" className="mt-2" onClick={() => refetchSub()}>
                Try again
              </Button>
            </div>
          )}

          {activating && (
            <p className="rounded-md bg-sky-50 px-3 py-2 text-sm text-sky-700" role="status">
              Activating your plan… this can take a few seconds.
            </p>
          )}

          {subscription.isSuccess && (
            <>
              {(() => {
                const notice = billingStatusNotice(subscription.data.status);
                return notice ? (
                  <p
                    className={cn(
                      "rounded-md px-3 py-2 text-sm",
                      notice.warn
                        ? "bg-rose-50 text-rose-700"
                        : "bg-sky-50 text-sky-700",
                    )}
                  >
                    {notice.text}
                  </p>
                ) : null;
              })()}

              <div className="grid grid-cols-1 sm:grid-cols-3 gap-4 text-sm">
                <div className="p-3 rounded-md bg-slate-50">
                  <p className="text-xs uppercase text-slate-500">Documents</p>
                  <p className="text-lg font-semibold text-slate-900">
                    {subscription.data.documentsUsed}
                    {subscription.data.documentLimit != null && ` / ${subscription.data.documentLimit}`}
                  </p>
                  {/* FP-113: a post-downgrade over-limit count ("12 / 5") was unexplained. */}
                  {subscription.data.documentLimit != null &&
                    subscription.data.documentsUsed > subscription.data.documentLimit && (
                      <p className="mt-0.5 text-xs text-amber-700">
                        Over your plan limit — your documents are kept; upgrade to add more.
                      </p>
                    )}
                </div>
                <div className="p-3 rounded-md bg-slate-50">
                  <p className="text-xs uppercase text-slate-500">Vendor portal</p>
                  <p className="text-lg font-semibold text-slate-900">
                    {subscription.data.hasVendorPortal ? "On" : "Off"}
                  </p>
                  {/* FP-113: "Off" alone had no explanation or path. */}
                  {!subscription.data.hasVendorPortal && (
                    <p className="mt-0.5 text-xs text-slate-500">Upgrade to collect documents from vendors.</p>
                  )}
                </div>
                <div className="p-3 rounded-md bg-slate-50">
                  <p className="text-xs uppercase text-slate-500">AI reading cost</p>
                  <p className="text-lg font-semibold text-slate-900">
                    ${subscription.data.extractionSpend.toFixed(2)}
                  </p>
                  {/* FP-113: was 10px — too small to read. */}
                  <p className="text-xs text-slate-500">this month · included in your plan</p>
                </div>
              </div>

              {/* currentPeriodEnd is a real instant (Stripe billing-cycle end) → render in the viewer's
                  local zone, not pinned UTC. cancelAtPeriodEnd distinguishes "Renews" from "Ends": an
                  active sub set to cancel stays Status="active" until the period end, so it must read
                  "Ends on …" (#323 / FP-115). The fully-canceled case is handled by billingStatusNotice. */}
              {isPaid && subscription.data.currentPeriodEnd && (
                <p className="text-xs text-slate-500">
                  {subscription.data.cancelAtPeriodEnd
                    ? `Ends on ${formatRenewalDate(subscription.data.currentPeriodEnd)} — your plan won't renew.`
                    : `Renews on ${formatRenewalDate(subscription.data.currentPeriodEnd)}.`}
                </p>
              )}

              {/* Tiles are driven off `KNOWN_CHECKOUT_PLAN_IDS` + `PLANS`
                  (#147, ADR 0011); a price change edits one file. `annual` is the
                  highlighted conversion default (matches the landing pricing).
                  Hidden while `activating` so a mid-webhook user can't re-checkout. */}
              {!activating &&
                (subscription.data.plan === "free" ? (
                  <div className="grid grid-cols-1 md:grid-cols-3 gap-3 pt-2">
                    {KNOWN_CHECKOUT_PLAN_IDS.map((id) => (
                      <PlanCard
                        key={id}
                        name={PLANS[id].label}
                        price={PLANS[id].monthlyPriceLabel}
                        tagline={PLANS[id].tagline ?? ""}
                        billedNote={
                          PLANS[id].annualBilledLabel
                            ? `${PLANS[id].annualBilledLabel}${PLANS[id].annualSavingsLabel ? ` · ${PLANS[id].annualSavingsLabel}` : ""}`
                            : null
                        }
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
                ))}
            </>
          )}
        </CardContent>
      </Card>

      <OnboardingResetCard />

      {/* Account & access management (#183). */}
      <SecuritySection />
      <DataExportSection />
      <DangerZone />
    </div>
  );
}

// Hoisted per the react-hooks/static-components rule. "Restart tour" re-arms the
// first-run experience (#191): it re-shows every dismissed per-page tip and flags
// the dashboard to replay the welcome modal, then navigates there.
function OnboardingResetCard() {
  const router = useRouter();
  return (
    <Card>
      <CardContent className="p-6 flex flex-col gap-3 sm:flex-row sm:items-center sm:justify-between">
        <div>
          <h2 className="text-base font-semibold text-slate-800">Product tour</h2>
          <p className="text-sm text-slate-500">
            Replay the welcome walkthrough and bring back the first-visit tips.
          </p>
        </div>
        <Button
          variant="outline"
          className="sm:w-auto"
          onClick={() => {
            resetOnboardingTips();
            requestTourRestart();
            router.push("/dashboard");
          }}
        >
          Restart tour
        </Button>
      </CardContent>
    </Card>
  );
}

// Hoisted to module scope per the react-hooks/static-components rule. Lets the
// org owner fix their organization name + IANA time zone — the zone silently
// drives reminder send time (#185), so the form previews the next send to make
// the effect visible before saving.
function OrgSettingsForm({ me }: { me: Me }) {
  const update = useUpdateOrganization();
  const nameId = useId();
  const tzId = useId();
  const [name, setName] = useState(me.organizationName);
  const [timeZone, setTimeZone] = useState(me.timeZone);
  // Memoize the (~400-entry) IANA list; recompute only if the saved zone
  // changes (so a saved-but-unusual zone is always present + selectable).
  const zones = useMemo(() => listTimeZones(me.timeZone), [me.timeZone]);
  // The "All time zones" group excludes the curated ones (they're already up top), so a zone
  // never appears twice. A saved-but-unusual zone is still here (listTimeZones prepends it). (FP-112)
  const otherZones = useMemo(() => {
    const curated = new Set(CURATED_TIMEZONES.map((z) => z.value));
    return zones.filter((z) => !curated.has(z));
  }, [zones]);
  const dirty = name.trim() !== me.organizationName || timeZone !== me.timeZone;

  const onSubmit = (e: React.FormEvent) => {
    e.preventDefault();
    const trimmed = name.trim();
    if (!trimmed) {
      toast.error("Organization name is required.");
      return;
    }
    update.mutate(
      { name: trimmed, timeZone },
      {
        onSuccess: () => toast.success("Organization settings saved."),
        onError: (err) =>
          toast.error(err instanceof Error && err.message ? err.message : GENERIC_FALLBACK_MESSAGE),
      },
    );
  };

  return (
    <Card>
      <CardContent className="p-6 space-y-4">
        <div>
          <h2 className="text-base font-semibold text-slate-800">Organization</h2>
          <p className="text-sm text-slate-500">
            Your time zone controls when daily reminders are sent.
          </p>
        </div>
        <form onSubmit={onSubmit} className="space-y-4">
          <div>
            <label htmlFor={nameId} className="text-sm font-medium text-slate-700">
              Organization name
            </label>
            <Input
              id={nameId}
              value={name}
              onChange={(e) => setName(e.target.value)}
              maxLength={200}
              className="mt-1"
            />
          </div>
          <div>
            <label htmlFor={tzId} className="text-sm font-medium text-slate-700">
              Time zone
            </label>
            <select
              id={tzId}
              value={timeZone}
              onChange={(e) => setTimeZone(e.target.value)}
              // pointer-coarse:min-h-11 gives the native select a 44px touch target on phones (FP-131).
              className="mt-1 block w-full rounded-md border border-input bg-white px-3 py-2 text-sm text-slate-900 pointer-coarse:min-h-11 focus-visible:border-ring focus-visible:outline-none focus-visible:ring-3 focus-visible:ring-ring"
            >
              {/* FP-112: friendly US-first zones up top; the full IANA list under "All time zones"
                  for everyone else. Finding your own zone shouldn't mean scrolling from Africa/Abidjan. */}
              <optgroup label="Common">
                {CURATED_TIMEZONES.map((z) => (
                  <option key={z.value} value={z.value}>
                    {z.label}
                  </option>
                ))}
              </optgroup>
              <optgroup label="All time zones">
                {otherZones.map((z) => (
                  <option key={z} value={z}>
                    {z}
                  </option>
                ))}
              </optgroup>
            </select>
            <p className="mt-1.5 text-xs text-slate-500">{describeNextSend(timeZone)}</p>
          </div>
          <div className="flex flex-wrap gap-x-6 gap-y-1 text-sm">
            <p><span className="text-slate-500">Email:</span> {me.email}</p>
            <p><span className="text-slate-500">Role:</span> {me.role}</p>
          </div>
          <Button type="submit" disabled={!dirty || update.isPending}>
            {update.isPending ? "Saving…" : "Save changes"}
          </Button>
        </form>
      </CardContent>
    </Card>
  );
}

function PlanCard({
  name,
  price,
  tagline,
  billedNote,
  featured,
  onClick,
  pending,
}: {
  name: string;
  price: string;
  tagline: string;
  billedNote?: string | null;
  featured?: boolean;
  onClick: () => void;
  pending: boolean;
}) {
  return (
    // role="group" + aria-label give screen-reader users a "Pro plan",
    // "Annual plan", "Founding plan" landmark to navigate to, and let
    // tests scope assertions with `screen.getByRole('group', { name:
    // /Pro plan/i })` — a stable a11y-anchored selector rather than the
    // brittle `closest("div.rounded-md")` pattern (#147 review).
    <div
      role="group"
      aria-label={`${name} plan`}
      className={`p-4 rounded-md border ${featured ? "border-sky-500 bg-sky-50" : "border-slate-200"}`}
    >
      <p className="text-sm font-medium text-slate-700">{name}</p>
      <p className="text-2xl font-semibold text-slate-900">
        {price}
        <span className="text-xs text-slate-500 font-normal">/mo</span>
      </p>
      {billedNote && <p className="text-xs text-emerald-700 mt-0.5">{billedNote}</p>}
      <p className="text-xs text-slate-500 mt-1">{tagline}</p>
      <Button className="w-full mt-3" size="sm" onClick={onClick} disabled={pending}>
        {pending ? "Redirecting…" : `Upgrade to ${name}`}
      </Button>
    </div>
  );
}
