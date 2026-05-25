/**
 * Open Graph image — generated at build time via Next.js's `ImageResponse`.
 *
 * Renders the twotone lockup ("Compli" navy + "Drop" sky) on a soft tinted
 * background, with the tagline below. 1200×630 PNG.
 *
 * Next 16's App Router auto-emits the corresponding `<meta property="og:image">`
 * and `og:image:width / height / type` tags from this file's `size` +
 * `contentType` exports. The accompanying `opengraph-image.alt.txt` (committed
 * alongside) supplies `og:image:alt`.
 *
 * Source artwork: `docs/brand/logo-refresh-2026/`.
 */
import { ImageResponse } from "next/og";
import { readFile } from "node:fs/promises";
import { join } from "node:path";

export const alt = "CompliDrop — Stop Chasing Paper. Start Dropping Docs.";
export const size = { width: 1200, height: 630 };
export const contentType = "image/png";

const COLORS = {
  sky: "#0EA5E9",
  navy: "#0C4A6E",
  tint: "#F0F9FF",
  white: "#FFFFFF",
  muted: "#64748B",
};

export default async function OpenGraphImage() {
  // Embed Plus Jakarta Sans so the wordmark in the OG render matches the
  // typography used on the live site. The file is OFL-licensed and committed
  // under `app/_assets/` (the `_` prefix opts the folder out of routing).
  const fontData = await readFile(
    join(process.cwd(), "src/app/_assets/PlusJakartaSans-Bold.ttf"),
  );

  return new ImageResponse(
    (
      <div
        style={{
          width: "100%",
          height: "100%",
          background: `linear-gradient(135deg, ${COLORS.tint} 0%, ${COLORS.white} 50%, ${COLORS.tint} 100%)`,
          display: "flex",
          flexDirection: "column",
          alignItems: "center",
          justifyContent: "center",
          padding: 96,
          fontFamily: "Plus Jakarta Sans",
        }}
      >
        {/* Lockup: droplet + twotone wordmark */}
        <div
          style={{
            display: "flex",
            alignItems: "center",
            gap: 36,
          }}
        >
          <svg
            width={160}
            height={160}
            viewBox="0 0 100 100"
            xmlns="http://www.w3.org/2000/svg"
          >
            <path
              d="M50 4 C 50 4, 14 38, 14 62 C 14 82, 30 96, 50 96 C 70 96, 86 82, 86 62 C 86 38, 50 4, 50 4 Z"
              fill={COLORS.sky}
            />
            <path
              d="M30 60 L 46 74 L 72 44"
              fill="none"
              stroke={COLORS.white}
              strokeWidth={9}
              strokeLinecap="round"
              strokeLinejoin="round"
            />
          </svg>
          <div
            style={{
              fontSize: 156,
              fontWeight: 700,
              letterSpacing: "-0.04em",
              lineHeight: 1,
              display: "flex",
            }}
          >
            <span style={{ color: COLORS.navy }}>Compli</span>
            <span style={{ color: COLORS.sky }}>Drop</span>
          </div>
        </div>

        {/* Tagline */}
        <div
          style={{
            marginTop: 56,
            fontSize: 52,
            fontWeight: 700,
            letterSpacing: "-0.02em",
            color: COLORS.navy,
            textAlign: "center",
            lineHeight: 1.15,
            display: "flex",
            flexDirection: "column",
            alignItems: "center",
          }}
        >
          <span>Stop Chasing Paper.</span>
          <span style={{ color: COLORS.sky }}>Start Dropping Docs.</span>
        </div>

        {/* Footer line */}
        <div
          style={{
            marginTop: 48,
            fontSize: 28,
            color: COLORS.muted,
            display: "flex",
          }}
        >
          AI compliance tracking for COIs, licenses &amp; permits — $49 / mo
        </div>
      </div>
    ),
    {
      ...size,
      fonts: [
        {
          name: "Plus Jakarta Sans",
          data: fontData,
          weight: 700,
          style: "normal",
        },
      ],
    },
  );
}
