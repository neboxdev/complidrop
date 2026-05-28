# 0011. Plan vocab is unified as `free | pro | annual | founding`; `founding` is an authenticated-only promo tier

- **Status:** accepted
- **Date:** 2026-05-28
- **Deciders:** Ruben G.

## Context

After [#71](https://github.com/neboxdev/complidrop/issues/71) landed the `@/lib/plans.ts` single-source-of-truth module, the post-merge review surfaced that two AC items were not actually met:

- **AC #1** ("**every** frontend pricing surface imports from it") — `frontend/src/app/(dashboard)/settings/page.tsx` still hardcoded `$49 / $39 / $39` in the three billing tiles.
- **AC #2** ("plan-name vocab is consistent … settings billing tiles") — the settings page sends `"monthly" | "annual" | "founding"` to `/api/billing/checkout`, while the rest of the frontend uses `"free" | "pro" | "annual"`.

Two underlying questions had to be resolved together before a clean migration was possible:

1. **What is `founding`?** It exists in the codebase (`StripeSettings.FoundingPriceId`, a configured Stripe price in `connection string.txt`, a settings-page billing tile labelled "First 50 only"). But it has no entry in the frontend `PLANS` registry and no public URL surface. Is it a marketing pricing surface that should get a landing-page CTA, an authenticated-only promo tier, or a deprecated relic?
2. **Should the database/wire vocab keep `"monthly"` (Stripe-billing-cadence wording) or align with the customer-facing `"pro"` (product-tier wording)?**

[`complidrop-phase2-architecture.md`](C:\NewStart\Company%20documents\complidrop-phase2-architecture.md) line 587 documents the marketing positioning: *"Founding customers ($39/mo forever) grandfathered into the 'Starter' feature set."* The tier is a sliding promo capped at the first 50 paying customers. Once the cap is filled the price disappears. The phase 2 doc treats it as an early-customer perk, not a permanent public tier.

The current landing page (`frontend/src/app/page.tsx`) surfaces three CTAs — Free / Pro / Annual — and intentionally does NOT show Founding. A user discovers Founding only after signing up Free and opening `/settings`, where the third billing tile is the Founding offer.

## Decision

### 1. Plan vocab is `free | pro | annual | founding`

The four canonical plan ids are `free`, `pro`, `annual`, `founding`. This single vocab is used in:

- **Frontend display** (`PLANS[id].label`, settings billing tile names, register-form headings)
- **`Subscription.Plan` column** in the database (replacing the previous `monthly` literal)
- **`/api/billing/checkout` wire request** (replacing the previous `monthly | annual | founding` body shape)
- **`StripeService.ResolvePlanFromPriceId`** return values (now `"pro"` for the MonthlyPriceId instead of `"monthly"`)

The Stripe configuration-key names (`Stripe:MonthlyPriceId`, `Stripe:AnnualPriceId`, `Stripe:FoundingPriceId`) are deliberately NOT renamed — those are Stripe-side billing-cadence keys, not application-side plan ids. Renaming them would cascade into the user's actual `appsettings`/user-secrets/env-var configuration without changing semantics. The mapping `MonthlyPriceId → "pro" plan id` lives in `StripeService.ResolvePlanFromPriceId` and is the single boundary between Stripe wording and app wording.

### 2. `founding` is the authenticated-only promo tier (not in `KNOWN_PLAN_IDS`)

`frontend/src/lib/plans.ts` exposes two typed enums with overlapping but distinct purposes:

```typescript
// Public URL-reachable plan ids. Used by parsePlanId(?plan=...) on the
// register page. Marketing emails, landing-page CTAs, and pricing-card
// deep-links use these.
export const KNOWN_PLAN_IDS = ["free", "pro", "annual"] as const;

// Checkout-eligible plan ids. Used by the settings page billing tiles
// and the /api/billing/checkout wire request body. `founding` joins
// here but stays out of KNOWN_PLAN_IDS because it isn't a public
// landing-page surface — it's surfaced only after authentication.
export const KNOWN_CHECKOUT_PLAN_IDS = ["pro", "annual", "founding"] as const;
```

Why split:

- `KNOWN_PLAN_IDS` controls what `?plan=` URL parameters resolve to. Adding `founding` here would make `?register?plan=founding` a routable URL, which expands the marketing surface — undesirable for a sliding promo that disappears once the cap is filled.
- `KNOWN_CHECKOUT_PLAN_IDS` controls what the backend accepts on `/api/billing/checkout` and what the settings page renders as billing tiles. Both surfaces are gated behind authentication, so the "First 50 only" cap can be enforced without confusing public visitors.
- `free` is in `KNOWN_PLAN_IDS` but NOT in `KNOWN_CHECKOUT_PLAN_IDS` — there's no checkout for free, you just register.
- `founding` is in `KNOWN_CHECKOUT_PLAN_IDS` but NOT in `KNOWN_PLAN_IDS` — there's no public URL for it, you get there via the settings billing tile.
- The intersection `{pro, annual}` is the public + checkout surface that all callers can rely on.

### 3. `PLANS` registry includes `founding` so the dollar literal is single-sourced

`PLANS.founding` joins the registry with `monthlyPriceLabel: "$39"`. The settings page reads all three billing-tile prices from `PLANS[id].monthlyPriceLabel` rather than hardcoding. An intentional price change still requires updating exactly one file (`plans.ts`) — fulfilling the original [#71](https://github.com/neboxdev/complidrop/issues/71) AC #4.

`PLANS.founding.bannerCopy` is `null` because the register form's per-plan banner is keyed by `KNOWN_PLAN_IDS` (which excludes founding) — a Founding signup goes through the same Free → upgrade-in-settings flow as any other auth-only upsell, so it never needs the register banner.

### 4. Wire-vocab migration: `monthly` → `pro` in the database

A one-shot EF Core migration converts existing `Subscriptions.Plan = 'monthly'` rows to `Subscriptions.Plan = 'pro'`. The `/api/billing/checkout` endpoint now accepts `"pro" | "annual" | "founding"` exactly and returns `400 billing.plan_unknown` for any other value (including the legacy `"monthly"`). The legacy value is NOT silently re-mapped — clients have to update.

This is safe at MVP scale (no production traffic, no API consumers other than the first-party frontend). For a post-launch vocab change of comparable scope, ADR a deprecation window.

## Consequences

### Positive

- **Single vocab.** UI labels, database column, wire request body, and Stripe-resolve output all use `pro | annual | founding`. No more mental translation between settings-page-says-`monthly` and rest-of-app-says-`pro`.
- **Founding has a typed home.** Previously `founding` was a magic string in two places (settings page + backend switch). Now it's `KNOWN_CHECKOUT_PLAN_IDS[2]` with a `PLANS.founding` entry. Adding a fourth checkout tier in the future would require adding it to one enum.
- **Public vs auth surface separation.** `KNOWN_PLAN_IDS` and `KNOWN_CHECKOUT_PLAN_IDS` make the marketing-vs-authenticated distinction explicit at the type level. A future contributor tempted to slap `founding` into a marketing CTA has to add it to `KNOWN_PLAN_IDS` first — that diff touches the parser, the URL-reachable surface, and the register-form heading map, making the marketing pivot a visible change.
- **AC #1 + AC #4 of [#71](https://github.com/neboxdev/complidrop/issues/71) are finally met.** Every frontend pricing surface imports from `@/lib/plans`. An intentional price change requires updating exactly one file.

### Negative

- **Existing `Subscriptions.Plan = 'monthly'` rows need a migration.** Mitigated by being trivially small at MVP scale.
- **The wire-vocab change is a breaking API change** for anyone calling `/api/billing/checkout` with `plan: "monthly"`. Mitigated by the only caller being the first-party frontend, updated in the same PR.
- **Two enums (`KNOWN_PLAN_IDS` + `KNOWN_CHECKOUT_PLAN_IDS`) instead of one.** Carries a small ongoing cognitive cost — a contributor adding a new tier has to decide which enum(s) it joins. Counter: the comment on `KNOWN_CHECKOUT_PLAN_IDS` documents the rule and `plans.test.ts` pins the cross-consistency invariants.

### Neutral

- **Stripe config-key names (`MonthlyPriceId` / `AnnualPriceId` / `FoundingPriceId`) are unchanged.** They remain Stripe-side billing-cadence wording, not app-side plan ids. The boundary that translates between them is `StripeService.ResolvePlanFromPriceId`.
- **Future Founding-cap enforcement** ("first 50 only") is out of scope for this ADR. Today the settings tile is always shown to any free-plan user; the cap is enforced only at the Stripe-side priceId (a sold-out price returns a Stripe error). When the cap-enforcement check lands, it gates `KNOWN_CHECKOUT_PLAN_IDS` rendering, not the public URL surface — which is exactly why founding stays out of `KNOWN_PLAN_IDS`.

## Alternatives considered

### Option A — Keep `monthly` in the database, translate at the API boundary

Leave `Subscriptions.Plan = 'monthly'` as-is, and have `BillingEndpoints` translate `monthly ↔ pro` on every read/write.

Rejected because:
- Translation layers in this codebase consistently lose to alignment — the [#71](https://github.com/neboxdev/complidrop/issues/71) PR explicitly documented "KNOWN VOCAB MISMATCH" because of one such layer, and the cost was a deferred ticket.
- The whole point of #147 is removing the divergence between display vocab and wire vocab. Keeping translation layered on top doesn't solve the AC — it adds a runtime mapping that future contributors must remember.
- The migration is one-line and at MVP scale touches few rows.

### Option B — Make `founding` a public CTA (add to `KNOWN_PLAN_IDS`, add a 4th landing-page card)

Promote Founding to the landing page; treat it as the headline conversion offer.

Rejected because:
- The ticket's Non-goal #1 is "Re-pricing any plan." Adding Founding to the landing page is a marketing/positioning pivot, not a vocab cleanup. It deserves its own PM-reviewed spec.
- The "first 50 only" cap becomes user-visible once filled — the public card silently disappears or shows "sold out", confusing visitors #51+.
- Expanding Founding's surface area later is a small additive change; retracting it (sold-out → 404 on `?plan=founding`) is messier.

### Option C — Retire `founding` entirely

Drop the tier from the codebase and Stripe config.

Rejected because:
- The user has a configured Stripe price for it (`connection string.txt:26`) and it is documented in the Phase 2 architecture doc as a deliberate early-customer promo.
- This ADR is about vocab reconciliation, not product strategy.

## References

- Tickets: [#71](https://github.com/neboxdev/complidrop/issues/71) (single source of truth for plan pricing — the prior PR that left this divergence open), [#112](https://github.com/neboxdev/complidrop/pull/112) (the PR that documented the deferral), [#147](https://github.com/neboxdev/complidrop/issues/147) (this ticket).
- ADRs: none directly superseded.
- External: `C:\NewStart\Company documents\complidrop-phase2-architecture.md` §15 (the Founding tier marketing positioning), `C:\NewStart\Company documents\complidrop-technical-architecture.md` §5.1 (the original `Plan ∈ {free, monthly, annual, founding}` data model that this ADR supersedes for the application code).
