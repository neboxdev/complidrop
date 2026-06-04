/**
 * Single source of truth for CompliDrop's public-web identity: the canonical
 * origin and the brand "NAP" facts (name, description, category, price) that
 * must stay byte-identical everywhere they surface —
 *   - `<head>` metadata          (`app/layout.tsx` + per-page `metadata`)
 *   - the metadata routes         (`app/robots.ts`, `sitemap.ts`, `manifest.ts`)
 *   - JSON-LD structured data     (`lib/structured-data.ts`)
 *
 * Consistent brand facts across every surface is both a classic SEO signal and
 * — per the GEO/AEO research in [#176] — a factor in whether AI assistants will
 * confidently name us. One drifted price or description and the answer hedges.
 *
 * Price-bearing display copy lives in `lib/plans.ts` (the existing source of
 * truth for the `$49` / `$39` labels). This module imports the Pro price from
 * there rather than re-stating it, so the marketing dollar figure and the
 * schema `Offer` price can never disagree.
 */
import { PLANS } from "@/lib/plans";

/**
 * The canonical origin used as `metadataBase` and as the base for every
 * absolute URL in structured data. Defaults to the production origin; override
 * per-environment via `NEXT_PUBLIC_SITE_URL` (preview/staging deploys).
 *
 * An empty string is treated as unset — CI/preview environments commonly
 * forward the variable without a value, and `new URL("")` throws. (Moved here
 * from `layout.tsx` so every SEO surface composes URLs against one origin.)
 */
export const SITE_URL =
  process.env.NEXT_PUBLIC_SITE_URL?.trim() || "https://www.complidrop.com";

export const SITE_NAME = "CompliDrop";

/**
 * Customer-facing support inbox — the single source of truth for every "contact
 * support" / "email us" affordance (the document-detail processing-error help
 * link, the legal footer Contact link, the marketing header support link). One
 * constant so the address can never drift between surfaces. (#193, #194, #195)
 */
export const SUPPORT_EMAIL = "support@complidrop.com";

/**
 * One-line value proposition. Buyer language + the terms people actually search
 * ("certificate of insurance", "COI tracking"). Kept ≤ ~160 chars so it works
 * verbatim as a `<meta name="description">`.
 */
export const SITE_DESCRIPTION =
  "COI tracking software for small businesses. Upload a certificate of insurance, license, or permit — CompliDrop reads the dates and coverage, flags what's non-compliant, and reminds you before anything expires.";

/** Short brand tagline (sentence-cased; the hero renders its own styled split). */
export const SITE_TAGLINE = "Stop chasing paper. Start dropping docs.";

/** schema.org `applicationCategory` for the SoftwareApplication entity. */
export const SITE_CATEGORY = "BusinessApplication";

/**
 * Canonical Pro monthly price as a bare numeric string (no currency symbol),
 * derived from the `lib/plans.ts` label so it tracks any future price change.
 * Used as the schema.org `Offer.price`. `"$49"` → `"49"`.
 */
export const PRO_PRICE_USD = PLANS.pro.monthlyPriceLabel.replace(/[^0-9.]/g, "");

/**
 * The static brand lockup mirrored into `public/brand/` (see `lib/brand.ts`).
 * A stable public path — unlike the route-generated `/opengraph-image` and
 * `/apple-icon`, whose URLs carry Next-managed hashes — so it's safe to embed
 * in JSON-LD `logo` / `image`.
 */
export const LOGO_PATH = "/brand/complidrop-logo-horizontal.svg";

/**
 * Join a path onto {@link SITE_URL}. JSON-LD requires fully-qualified URLs —
 * `metadataBase` only rewrites relative URLs inside `<head>` metadata, never
 * the JSON inside a `<script type="application/ld+json">`.
 */
export function absoluteUrl(path = "/"): string {
  return new URL(path, SITE_URL).toString();
}
