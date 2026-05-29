/**
 * Per-page metadata helper. Every public marketing page calls `pageMetadata()`
 * so the three things that are easy to forget — a self-referencing canonical,
 * an Open Graph block, and a Twitter card — are always present and correct.
 *
 * Why a helper instead of per-page literals:
 *   - **Canonical must NOT live in the root layout.** A static `alternates`
 *     there would be inherited by every route, canonicalizing all of them to
 *     "/". Canonical is inherently per-page, so it belongs per-page.
 *   - Next merges metadata shallowly: a child that sets `openGraph` REPLACES the
 *     parent's wholesale. Centralizing the block here means every page ships a
 *     complete, consistent OG/Twitter payload rather than a partial one.
 *
 * The `title` string is composed by the root layout's `title.template`
 * (`"%s | CompliDrop"`), so pass the bare page title (no brand suffix). The OG
 * image is supplied site-wide by the `opengraph-image` file convention; we
 * deliberately don't set `openGraph.images` here so that file keeps priority.
 */
import type { Metadata } from "next";
import { SITE_NAME, absoluteUrl } from "@/lib/site";

export interface PageMetaInput {
  /** Bare page title; the layout template appends " | CompliDrop". */
  title: string;
  description: string;
  /** Site-relative path, e.g. "/faq". Becomes the canonical + og:url. */
  path: string;
  /** Defaults to true. Set false to keep a page out of the index (none today). */
  index?: boolean;
}

export function pageMetadata({ title, description, path, index = true }: PageMetaInput): Metadata {
  const url = absoluteUrl(path);
  return {
    title,
    description,
    alternates: { canonical: path },
    openGraph: {
      type: "website",
      url,
      siteName: SITE_NAME,
      locale: "en_US",
      title,
      description,
    },
    twitter: {
      card: "summary_large_image",
      title,
      description,
    },
    robots: index
      ? undefined
      : { index: false, follow: true },
  };
}
