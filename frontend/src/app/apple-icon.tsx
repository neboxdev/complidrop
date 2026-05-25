/**
 * Apple touch icon — generated at build time via Next.js's `ImageResponse`.
 *
 * Renders the sky-tile favicon (rounded square + inset white droplet + sky
 * check) at 180×180 PNG. Next 16's App Router auto-emits the
 * `<link rel="apple-touch-icon" sizes="180x180" type="image/png">` tag from
 * this file's `size` + `contentType` exports.
 *
 * Source artwork: `docs/brand/logo-refresh-2026/svg/complidrop-favicon.svg`.
 */
import { ImageResponse } from "next/og";

export const size = { width: 180, height: 180 };
export const contentType = "image/png";

// Match the favicon SVG's rx="22" on a 100×100 viewBox → at 180×180 that's
// 180 * (22 / 100) ≈ 40. The inner droplet uses transform translate(15 12) scale(0.7)
// in the source SVG; the inline <svg> below preserves that.
export default function AppleIcon() {
  return new ImageResponse(
    (
      <div
        style={{
          width: "100%",
          height: "100%",
          background: "#0EA5E9",
          borderRadius: 40,
          display: "flex",
          alignItems: "center",
          justifyContent: "center",
        }}
      >
        <svg
          width={140}
          height={140}
          viewBox="0 0 100 100"
          xmlns="http://www.w3.org/2000/svg"
        >
          <g transform="translate(15 12) scale(0.7)">
            <path
              d="M50 4 C 50 4, 14 38, 14 62 C 14 82, 30 96, 50 96 C 70 96, 86 82, 86 62 C 86 38, 50 4, 50 4 Z"
              fill="#FFFFFF"
            />
            <path
              d="M30 60 L 46 74 L 72 44"
              fill="none"
              stroke="#0EA5E9"
              strokeWidth={9}
              strokeLinecap="round"
              strokeLinejoin="round"
            />
          </g>
        </svg>
      </div>
    ),
    { ...size },
  );
}
