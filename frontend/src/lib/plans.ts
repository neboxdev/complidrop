/**
 * Single source of truth for plan IDs + price-bearing display copy
 * (#71 + #71 followup review).
 *
 * The pricing CTAs on the landing page link to `/register?plan=<id>`;
 * the register page reads the param and renders plan-aware copy; the
 * opengraph image bakes the headline price; the settings page renders
 * the current-tier strip. Before this module each of those surfaces
 * carried its own `$49` / `$39` / `$468` literals — five copies of
 * the same numbers, drifting independently.
 *
 * Scope clarification (per #71 followup architecture review): this
 * module owns ID + PRICE-BEARING display copy — the dollar amounts
 * that drift. Per-surface NARRATIVE copy (page headings, subtitles,
 * marketing taglines) stays in the surface that owns it. A future
 * contributor tempted to hoist e.g. RegisterForm's `PLAN_HEADINGS`
 * here should resist: those headings are signup-funnel-specific UX,
 * not shared facts.
 *
 * Two enums with overlapping but distinct purposes (#147, ADR 0011):
 *
 *   - `KNOWN_PLAN_IDS` (`free | pro | annual`) — the PUBLIC URL-reachable
 *     plan ids. Used by `parsePlanId(?plan=…)` on the register page and
 *     by the landing-page pricing CTAs. Marketing emails, deep links,
 *     and SEO surfaces resolve through this enum.
 *
 *   - `KNOWN_CHECKOUT_PLAN_IDS` (`pro | annual | founding`) — the
 *     CHECKOUT-eligible plan ids. Used by the settings-page billing
 *     tiles and the `/api/billing/checkout` wire request body. `free`
 *     is excluded (no checkout for free); `founding` is included but
 *     stays out of `KNOWN_PLAN_IDS` because it's an authenticated-only
 *     promo tier — visitors don't see Founding on the landing page,
 *     they see it in `/settings` after signing up Free.
 *
 *   - The intersection `{pro, annual}` is the public + checkout
 *     surface that both flows can rely on.
 *
 * `founding` had no entry in this registry before #147 — the settings
 * page hardcoded `$39` and the wire vocab was a magic string in two
 * places (settings + backend switch). ADR 0011 documents the rationale
 * for unifying as `free | pro | annual | founding` while keeping the
 * public/auth surface split.
 *
 * Cross-consistency invariants (pinned in plans.test.ts):
 *   - Every id in KNOWN_PLAN_IDS has a corresponding PLANS[id] entry.
 *   - Every id in KNOWN_CHECKOUT_PLAN_IDS has a corresponding PLANS[id]
 *     entry.
 *   - `KNOWN_PLAN_IDS` and `KNOWN_CHECKOUT_PLAN_IDS` share `pro` and
 *     `annual` (the public + checkout intersection); `founding` is in
 *     checkout-only; `free` is in URL-only.
 *   - Each plan's `bannerCopy` (when non-null) contains its own
 *     `monthlyPriceLabel`. A future price change that touched the
 *     label but forgot the banner would otherwise be silent.
 *   - Annual's bannerCopy contains both the monthly + billed totals.
 */

export const KNOWN_PLAN_IDS = ["free", "pro", "annual"] as const;
export type PlanId = (typeof KNOWN_PLAN_IDS)[number];

/**
 * Checkout-eligible plan ids (#147, ADR 0011). Distinct from
 * `KNOWN_PLAN_IDS` because:
 *
 *   - `free` is in `KNOWN_PLAN_IDS` (it's a public URL-reachable
 *     signup path) but NOT here (you don't "check out" of Free).
 *   - `founding` is here (it's a paid tier surfaced in the settings
 *     billing tiles + sent on `/api/billing/checkout`) but NOT in
 *     `KNOWN_PLAN_IDS` (it's an authenticated-only promo; landing
 *     page does not show a Founding CTA).
 *
 * The settings page reads this enum to render its billing tiles; the
 * `checkout.mutate(plan)` call is typed `CheckoutPlanId`. The backend
 * `/api/billing/checkout` endpoint accepts exactly these three values
 * — anything else (including the legacy `"monthly"`) returns 400.
 */
export const KNOWN_CHECKOUT_PLAN_IDS = ["pro", "annual", "founding"] as const;
export type CheckoutPlanId = (typeof KNOWN_CHECKOUT_PLAN_IDS)[number];

/**
 * Per-plan display copy. Keep all dollar-amounts here so the landing
 * page, register banners, opengraph headline, and settings tiles
 * agree by construction.
 *
 *   - `monthlyPriceLabel`: the "$X" headline shown in marketing /
 *     settings billing tiles.
 *   - `annualBilledLabel` / `annualSavingsLabel`: only present on the
 *     Annual plan; render conditionally.
 *   - `bannerCopy`: what the register page shows when `?plan=<id>` is
 *     selected. `null` on free (no banner) and `null` on founding
 *     (Founding is an authenticated-only promo per ADR 0011 — never
 *     reachable via the register banner, so the field stays null and
 *     `parsePlanId` will never route a request through it).
 *   - `tagline`: the short marketing line shown in the settings
 *     billing tiles (#147). Marketing-side narrative copy on the
 *     landing page stays in `page.tsx`; this field is the short
 *     description used by authenticated surfaces.
 *
 * Keyed by `PlanId` (the union of every known plan), so the
 * `Record<PlanId, …>` enforces at compile-time that any new
 * `PlanId` (free / pro / annual / founding) has a corresponding
 * entry. `PlanId` here is the union of `KNOWN_PLAN_IDS ∪
 * KNOWN_CHECKOUT_PLAN_IDS` — every id that can possibly need a
 * registry lookup.
 *
 * String literals deliberately — these are static marketing copy, not
 * computed prices. A "compute" approach (multiplication, formatting)
 * would obscure the eyeball-check between this file and the
 * marketing-side rendered output.
 *
 * `annualSavingsLabel` is stored CANONICALLY LOWERCASE ("save $120")
 * so call sites render it directly without per-call `.toLowerCase()`
 * transforms — the previous shape ("Save $120" + landing-page-side
 * `.toLowerCase()`) undermined the single-source-of-truth promise
 * because a reader of this file couldn't predict the rendered output.
 * Sentence-cased usage (register banner) embeds it in `bannerCopy`
 * directly with explicit Title-cased phrasing.
 */
export const PLANS: Record<
  PlanId | CheckoutPlanId,
  {
    id: PlanId | CheckoutPlanId;
    label: string;
    monthlyPriceLabel: string;
    annualBilledLabel: string | null;
    annualSavingsLabel: string | null;
    bannerCopy: string | null;
    tagline: string | null;
  }
> = {
  free: {
    id: "free",
    label: "Free",
    monthlyPriceLabel: "$0",
    annualBilledLabel: null,
    annualSavingsLabel: null,
    bannerCopy: null,
    tagline: null,
  },
  pro: {
    id: "pro",
    label: "Pro",
    monthlyPriceLabel: "$49",
    annualBilledLabel: null,
    annualSavingsLabel: null,
    bannerCopy: "You selected the Pro plan — $49/month. Cancel anytime.",
    tagline: "Unlimited docs, all features.",
  },
  annual: {
    id: "annual",
    label: "Annual",
    monthlyPriceLabel: "$39",
    annualBilledLabel: "Billed $468/year",
    annualSavingsLabel: "save $120",
    bannerCopy:
      "You selected the Annual plan — $39/month, billed $468/year. Save $120.",
    tagline: "Same features, billed yearly.",
  },
  founding: {
    id: "founding",
    label: "Founding",
    monthlyPriceLabel: "$39",
    annualBilledLabel: null,
    annualSavingsLabel: null,
    // null by design — Founding is an authenticated-only promo (ADR
    // 0011). The register-form banner is keyed by `KNOWN_PLAN_IDS`,
    // which excludes founding, so this field would never render.
    bannerCopy: null,
    tagline: "Locked forever. First 50 only.",
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
