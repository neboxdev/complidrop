import type { MetadataRoute } from "next";
import { absoluteUrl } from "@/lib/site";

// The app surfaces — never useful in search, and the portal is token-gated, so
// keep token URLs out of any index. (These sit behind auth too; disallowing
// also stops crawlers wasting budget probing them.) Route groups like
// `(dashboard)` don't appear in the URL, so we list the real paths.
const APP_SURFACES = [
  "/api/",
  "/dashboard",
  "/documents",
  "/vendors",
  "/rules",
  "/reminders",
  "/export",
  "/settings",
  "/portal/",
];

// Search + AI-assistant crawlers we explicitly welcome. `*` already allows them,
// but listing them documents the deliberate decision (see [#176]) so no one
// later "tidies up" robots.txt and accidentally blocks us: disallowing
// OAI-SearchBot removes CompliDrop from ChatGPT Search, Google-Extended from
// Google AI Overviews, and so on. They get the same access as any crawler —
// everything except the app surfaces.
export const AI_CRAWLERS = [
  "OAI-SearchBot",
  "ChatGPT-User",
  "GPTBot",
  "PerplexityBot",
  "Google-Extended",
  "ClaudeBot",
  "Claude-Web",
  "CCBot",
];

export default function robots(): MetadataRoute.Robots {
  const access = { allow: "/", disallow: APP_SURFACES };
  return {
    rules: [
      { userAgent: "*", ...access },
      ...AI_CRAWLERS.map((userAgent) => ({ userAgent, ...access })),
    ],
    sitemap: absoluteUrl("/sitemap.xml"),
  };
}
