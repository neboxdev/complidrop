/**
 * Apple touch icon — generated at build time via Next.js's `ImageResponse`.
 *
 * Renders the standalone droplet (sky droplet + white check stroke) on a
 * transparent background at 180×180 PNG. Next 16's App Router auto-emits the
 * `<link rel="apple-touch-icon" sizes="180x180" type="image/png">` tag from
 * this file's `size` + `contentType` exports.
 *
 * **iOS trade-off (#58):** with a transparent background, the user's home-
 * screen wallpaper shows through the empty corners of the icon. The
 * previous sky-tile version (in #54/#55) avoided this but made the favicon
 * unreadable at browser-tab resolution. The user explicitly chose the
 * transparent variant for consistency with the browser tab favicon — see
 * issue #58. Reverting to a tiled background is a one-file change.
 *
 * Source artwork: `docs/brand/logo-refresh-2026/svg/complidrop-mark.svg`.
 */
import { ImageResponse } from "next/og";
import { BRAND_COLORS, CHECK_PATH, DROPLET_PATH } from "@/lib/brand";

export const size = { width: 180, height: 180 };
export const contentType = "image/png";

export default function AppleIcon() {
  return new ImageResponse(
    (
      <div
        style={{
          width: "100%",
          height: "100%",
          display: "flex",
          alignItems: "center",
          justifyContent: "center",
          // Transparent background — droplet shape only.
          background: "transparent",
        }}
      >
        <svg
          width={180}
          height={180}
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
      </div>
    ),
    { ...size },
  );
}
