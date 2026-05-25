/**
 * CompliDrop logo — adapted from `docs/brand/logo-refresh-2026/components/Logo.jsx`
 * to TSX with typed variants. SVG paths are inlined (no external image fetch).
 *
 * Variants:
 *   - primary  → sky droplet + navy wordmark   (default, white/light backgrounds)
 *   - twotone  → sky droplet + "Compli" navy + "Drop" sky   (marketing hero)
 *   - reverse  → sky droplet + white wordmark  (navy / dark backgrounds)
 *   - mark     → icon only (no wordmark)
 *
 * Plus Jakarta Sans must be loaded on the page (already loaded site-wide via
 * `next/font/google` in `frontend/src/app/layout.tsx`).
 *
 * Brand constants (paths, colors) live in `@/lib/brand` and are shared with
 * the OG and apple-icon `ImageResponse` generators.
 */

import type { CSSProperties } from "react";
import { BRAND_COLORS, CHECK_PATH, DROPLET_PATH } from "@/lib/brand";

export type LogoVariant = "primary" | "twotone" | "reverse" | "mark";

export interface LogoProps {
  /** Lockup variant. Defaults to `"primary"`. */
  variant?: LogoVariant;
  /**
   * Lockup height in px. For `mark` this is the icon size. For lockup variants
   * the icon size equals `height` and the wordmark scales to `~0.81 × height`
   * (matching the 52 / 64 ratio in the canonical SVG exports — icon-dominant).
   * Defaults to `36`. Non-positive or non-finite values fall back to the default.
   */
  height?: number;
  /** Extra className passed to the outer span. */
  className?: string;
  /**
   * Accessible name. Defaults to `"CompliDrop"`. Ignored when `decorative` is
   * `true`.
   */
  title?: string;
  /**
   * When `true`, the entire lockup is hidden from assistive tech
   * (`aria-hidden="true"`, no role, no aria-label). Use this when a parent
   * element supplies the accessible name (e.g. `<Link aria-label="…">`)
   * so the wordmark isn't announced twice.
   */
  decorative?: boolean;
}

interface MarkProps {
  size: number;
  dropFill?: string;
  checkStroke?: string;
}

function Mark({
  size,
  dropFill = BRAND_COLORS.sky,
  checkStroke = BRAND_COLORS.white,
}: MarkProps) {
  return (
    <svg
      width={size}
      height={size}
      viewBox="0 0 100 100"
      xmlns="http://www.w3.org/2000/svg"
      aria-hidden="true"
      focusable="false"
    >
      <path d={DROPLET_PATH} fill={dropFill} />
      <path
        d={CHECK_PATH}
        fill="none"
        stroke={checkStroke}
        strokeWidth={9}
        strokeLinecap="round"
        strokeLinejoin="round"
      />
    </svg>
  );
}

const DEFAULT_HEIGHT = 36;

const WORDMARK_FONT_STYLE: CSSProperties = {
  fontFamily: "'Plus Jakarta Sans', system-ui, sans-serif",
  fontWeight: 700,
  letterSpacing: "-0.02em",
  lineHeight: 1,
  whiteSpace: "nowrap",
};

export function Logo({
  variant = "primary",
  height = DEFAULT_HEIGHT,
  className,
  title = "CompliDrop",
  decorative = false,
}: LogoProps) {
  // Guard against NaN / 0 / negative heights — silently fall back to the
  // default rather than emit invalid CSS (`fontSize: 'NaNpx'`, etc.).
  const safeHeight =
    Number.isFinite(height) && height > 0 ? height : DEFAULT_HEIGHT;

  const a11yProps = decorative
    ? { "aria-hidden": true as const }
    : { role: "img" as const, "aria-label": title };

  if (variant === "mark") {
    return (
      <span
        className={className}
        {...a11yProps}
        style={{ display: "inline-flex", alignItems: "center" }}
      >
        <Mark size={safeHeight} />
      </span>
    );
  }

  // Proportions derived from the canonical export at
  // `docs/brand/logo-refresh-2026/svg/complidrop-logo-horizontal.svg`:
  // viewBox 0 0 380 80, icon scaled to 64 px tall, wordmark font-size 52,
  // gap-between 16. Yields fontSize ≈ 0.81 × icon and gap ≈ 0.22 × icon. The
  // reference `Logo.jsx` in the handoff used a `1.3 × icon` fontSize comment
  // that was inconsistent with the actual SVG exports — we match the SVGs.
  const fontSize = Math.round(safeHeight * 0.81);
  const gap = Math.round(safeHeight * 0.22);

  const wordmarkStyle: CSSProperties = {
    ...WORDMARK_FONT_STYLE,
    fontSize: `${fontSize}px`,
  };

  let wordmark;
  if (variant === "twotone") {
    wordmark = (
      <span style={{ ...wordmarkStyle, color: BRAND_COLORS.navy }}>
        Compli<span style={{ color: BRAND_COLORS.sky }}>Drop</span>
      </span>
    );
  } else if (variant === "reverse") {
    wordmark = (
      <span style={{ ...wordmarkStyle, color: BRAND_COLORS.white }}>CompliDrop</span>
    );
  } else {
    wordmark = (
      <span style={{ ...wordmarkStyle, color: BRAND_COLORS.navy }}>CompliDrop</span>
    );
  }

  return (
    <span
      className={className}
      {...a11yProps}
      style={{ display: "inline-flex", alignItems: "center", gap: `${gap}px` }}
    >
      <Mark size={safeHeight} />
      {wordmark}
    </span>
  );
}

export default Logo;
