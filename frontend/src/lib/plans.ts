/**
 * Single source of truth for plan IDs + display copy (#71).
 *
 * The pricing CTAs on the landing page link to `/register?plan=<id>`;
 * the register page reads the param and renders plan-aware copy; the
 * opengraph image bakes the headline price; the settings page renders
 * the current-tier strip. Before this module each of those surfaces
 * carried its own `$49` / `$39` / `$468` literals — five copies of
 * the same numbers, drifting independently.
 *
 * KNOWN VOCAB MISMATCH (out of scope for #71): the frontend UI uses
 * `free | pro | annual` (this file), while
 * `frontend/src/app/(dashboard)/settings/page.tsx`'s
 * `useCheckoutMutation` and the backend `BillingEndpoints` use
 * `monthly | annual | founding` (the Stripe price-ID names). The
 * vocab cleanup requires aligning the backend Stripe config keys with
 * this file's ids — a separate ticket; today this module covers only
 * the display side of `free | pro | annual`.
 *
 * Tests that assert on the dollar values import from THIS module
 * (e.g. `expect(banner.textContent).toMatch(PLANS.annual.monthlyPrice)`)
 * so a future price change updates the test and the UI together.
 */

export const KNOWN_PLAN_IDS = ["free", "pro", "annual"] as const;
export type PlanId = (typeof KNOWN_PLAN_IDS)[number];

/**
 * Per-plan display copy. Keep all dollar-amounts here so the landing
 * page, register banners, opengraph headline, and settings tiles
 * agree by construction.
 *
 *   - `monthlyPriceLabel`: the "$X" headline shown in marketing.
 *   - `annualBilledLabel` / `annualSavingsLabel`: only present on the
 *     Annual plan; render conditionally.
 *   - `bannerCopy`: what the register page shows when ?plan=<id> is
 *     selected. `null` on free (no banner).
 *
 * String literals deliberately — these are static marketing copy, not
 * computed prices. A "compute" approach (multiplication, formatting)
 * would obscure the eyeball-check between this file and the
 * marketing-side rendered output.
 */
export const PLANS: Record<
  PlanId,
  {
    id: PlanId;
    label: string;
    monthlyPriceLabel: string;
    annualBilledLabel: string | null;
    annualSavingsLabel: string | null;
    bannerCopy: string | null;
  }
> = {
  free: {
    id: "free",
    label: "Free",
    monthlyPriceLabel: "$0",
    annualBilledLabel: null,
    annualSavingsLabel: null,
    bannerCopy: null,
  },
  pro: {
    id: "pro",
    label: "Pro",
    monthlyPriceLabel: "$49",
    annualBilledLabel: null,
    annualSavingsLabel: null,
    bannerCopy: "You selected the Pro plan — $49/month. Cancel anytime.",
  },
  annual: {
    id: "annual",
    label: "Annual",
    monthlyPriceLabel: "$39",
    annualBilledLabel: "Billed $468/year",
    annualSavingsLabel: "Save $120",
    bannerCopy:
      "You selected the Annual plan — $39/month, billed $468/year. Save $120.",
  },
};

/**
 * Tolerant parser for the `?plan=` URL parameter. Lowercases + trims
 * before checking the allow-list, so `?plan=Annual` or `?plan=PRO `
 * still resolve to a known plan. Anything outside the allow-list
 * falls back to "free".
 */
export function parsePlanId(raw: string | null | undefined): PlanId {
  const value = (raw ?? "").trim().toLowerCase();
  return (KNOWN_PLAN_IDS as readonly string[]).includes(value)
    ? (value as PlanId)
    : "free";
}
