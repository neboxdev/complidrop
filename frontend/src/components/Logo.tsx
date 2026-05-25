/**
 * CompliDrop logo — adapted from `design_handoff_complidrop_logo/components/Logo.jsx`
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
 */

import type { CSSProperties } from "react";

const COLORS = {
  sky: "#0EA5E9",
  navy: "#0C4A6E",
  white: "#FFFFFF",
} as const;

// Droplet outline and inner check — extracted from the design handoff SVGs.
// viewBox is 100×100; consumers pin size via the `height` prop.
const DROPLET_PATH =
  "M50 4 C 50 4, 14 38, 14 62 C 14 82, 30 96, 50 96 C 70 96, 86 82, 86 62 C 86 38, 50 4, 50 4 Z";
const CHECK_PATH = "M30 60 L 46 74 L 72 44";

export type LogoVariant = "primary" | "twotone" | "reverse" | "mark";

export interface LogoProps {
  /** Lockup variant. Defaults to `"primary"`. */
  variant?: LogoVariant;
  /**
   * Lockup height in px. For `mark` this is the icon size. For lockup variants
   * the icon size equals `height` and the wordmark scales to `1.3 × height`.
   * Defaults to `36`.
   */
  height?: number;
  /** Extra className passed to the outer span. */
  className?: string;
  /** Accessible name. Defaults to `"CompliDrop"`. Pass empty string to render decoratively. */
  title?: string;
}

interface MarkProps {
  size: number;
  dropFill?: string;
  checkStroke?: string;
}

function Mark({ size, dropFill = COLORS.sky, checkStroke = COLORS.white }: MarkProps) {
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

const WORDMARK_FONT_STYLE: CSSProperties = {
  fontFamily: "'Plus Jakarta Sans', system-ui, sans-serif",
  fontWeight: 700,
  letterSpacing: "-0.02em",
  lineHeight: 1,
  whiteSpace: "nowrap",
};

export function Logo({
  variant = "primary",
  height = 36,
  className,
  title = "CompliDrop",
}: LogoProps) {
  // When the caller supplies its own accessible name (e.g. a wrapping
  // `<Link aria-label="...">`), pass `title=""` to render the entire Logo
  // decoratively — both the icon and the wordmark text are hidden from
  // assistive tech so the wordmark isn't announced twice.
  const isDecorative = title === "";
  const a11yProps = isDecorative
    ? { "aria-hidden": true as const }
    : { role: "img" as const, "aria-label": title };

  if (variant === "mark") {
    return (
      <span
        className={className}
        {...a11yProps}
        style={{ display: "inline-flex", alignItems: "center" }}
      >
        <Mark size={height} />
      </span>
    );
  }

  const fontSize = Math.round(height * 1.3);
  const gap = Math.round(height * 0.35);

  const wordmarkStyle: CSSProperties = {
    ...WORDMARK_FONT_STYLE,
    fontSize: `${fontSize}px`,
  };

  let wordmark;
  if (variant === "twotone") {
    wordmark = (
      <span style={{ ...wordmarkStyle, color: COLORS.navy }}>
        Compli<span style={{ color: COLORS.sky }}>Drop</span>
      </span>
    );
  } else if (variant === "reverse") {
    wordmark = <span style={{ ...wordmarkStyle, color: COLORS.white }}>CompliDrop</span>;
  } else {
    wordmark = <span style={{ ...wordmarkStyle, color: COLORS.navy }}>CompliDrop</span>;
  }

  return (
    <span
      className={className}
      {...a11yProps}
      style={{ display: "inline-flex", alignItems: "center", gap: `${gap}px` }}
    >
      <Mark size={height} />
      {wordmark}
    </span>
  );
}

export default Logo;
