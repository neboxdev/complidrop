import type { MetadataRoute } from "next";
import { SITE_DESCRIPTION, SITE_NAME } from "@/lib/site";
import { BRAND_COLORS } from "@/lib/brand";

// Web app manifest — Next auto-links it (`<link rel="manifest">`). The icon
// points at the stable static brand mark in public/brand (the route-generated
// /apple-icon and /opengraph-image carry Next-managed hashes, so they're not
// safe to hardcode here). Theme color matches the brand sky used in `viewport`.
export default function manifest(): MetadataRoute.Manifest {
  return {
    name: `${SITE_NAME} — COI & Compliance Tracking`,
    short_name: SITE_NAME,
    description: SITE_DESCRIPTION,
    start_url: "/",
    display: "standalone",
    background_color: BRAND_COLORS.white,
    theme_color: BRAND_COLORS.sky,
    icons: [
      {
        src: "/brand/complidrop-mark.svg",
        sizes: "any",
        type: "image/svg+xml",
        purpose: "any",
      },
    ],
  };
}
