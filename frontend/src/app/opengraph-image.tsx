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
 * Brand constants come from `@/lib/brand` (single source of truth).
 */
import { ImageResponse } from "next/og";
import { readFile } from "node:fs/promises";
import { join } from "node:path";
import { BRAND_COLORS, CHECK_PATH, DROPLET_PATH } from "@/lib/brand";

export const alt = "CompliDrop — Stop Chasing Paper. Start Dropping Docs.";
export const size = { width: 1200, height: 630 };
export const contentType = "image/png";

const TINT = "#F0F9FF";
const MUTED = "#64748B";

// Embed Plus Jakarta Sans so the wordmark in the OG render matches the
// typography used on the live site. The file is OFL-licensed and committed
// under `app/_assets/` (the `_` prefix opts the folder out of routing).
// Memoized at module scope so the buffer is read once per process and reused
// across renders — important if this route ever transitions from the default
// static optimization to dynamic mode.
const fontPath = join(process.cwd(), "src/app/_assets/PlusJakartaSans-Bold.ttf");
const fontPromise = readFile(fontPath);

export default async function OpenGraphImage() {
  const fontData = await fontPromise;

  return new ImageResponse(
    (
      <div
        style={{
          width: "100%",
          height: "100%",
          background: `linear-gradient(135deg, ${TINT} 0%, ${BRAND_COLORS.white} 50%, ${TINT} 100%)`,
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
            <path d={DROPLET_PATH} fill={BRAND_COLORS.sky} />
            <path
              d={CHECK_PATH}
              fill="none"
              stroke={BRAND_COLORS.white}
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
            <span style={{ color: BRAND_COLORS.navy }}>Compli</span>
            <span style={{ color: BRAND_COLORS.sky }}>Drop</span>
          </div>
        </div>

        {/* Tagline */}
        <div
          style={{
            marginTop: 56,
            fontSize: 52,
            fontWeight: 700,
            letterSpacing: "-0.02em",
            color: BRAND_COLORS.navy,
            textAlign: "center",
            lineHeight: 1.15,
            display: "flex",
            flexDirection: "column",
            alignItems: "center",
          }}
        >
          <span>Stop Chasing Paper.</span>
          <span style={{ color: BRAND_COLORS.sky }}>Start Dropping Docs.</span>
        </div>

        {/* Footer line */}
        <div
          style={{
            marginTop: 48,
            fontSize: 28,
            color: MUTED,
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
