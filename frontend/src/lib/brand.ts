/**
 * CompliDrop brand constants — single source of truth for the droplet artwork
 * and brand palette used by:
 *   - `components/Logo.tsx`            (live React component)
 *   - `app/apple-icon.tsx`             (180×180 PNG via next/og)
 *   - `app/opengraph-image.tsx`        (1200×630 PNG via next/og)
 *
 * The canonical source artwork lives in `docs/brand/logo-refresh-2026/svg/`
 * and is mirrored as static files at `frontend/public/brand/` for use by
 * external consumers (email templates, social embeds, third-party uses).
 *
 * **Do not add orange `#F97316` here.** Orange is reserved for UI accents
 * (CTAs, "Most Popular" pill, expiring badges) — diluting it inside the logo
 * weakens its UI signal. See `CLAUDE.md` → "no orange in the logo" rule.
 */

export const BRAND_COLORS = {
  sky: "#0EA5E9",
  navy: "#0C4A6E",
  white: "#FFFFFF",
} as const;

/**
 * Droplet outline path, viewBox 0 0 100 100. Matches
 * `docs/brand/logo-refresh-2026/svg/complidrop-mark.svg`.
 */
export const DROPLET_PATH =
  "M50 4 C 50 4, 14 38, 14 62 C 14 82, 30 96, 50 96 C 70 96, 86 82, 86 62 C 86 38, 50 4, 50 4 Z";

/**
 * Inner check stroke path, viewBox 0 0 100 100. Render as
 * `fill="none" stroke-width="9" stroke-linecap="round" stroke-linejoin="round"`.
 */
export const CHECK_PATH = "M30 60 L 46 74 L 72 44";
