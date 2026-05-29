/**
 * Per-page metadata helper. Every public marketing page calls `pageMetadata()`
 * so the things that are easy to forget — a self-referencing canonical, an Open
 * Graph block with an image, and a Twitter card — are always present and
 * correct.
 *
 * Why a helper instead of per-page literals, and the two shallow-merge traps it
 * exists to avoid:
 *   - **Canonical must NOT live in the root layout.** A static `alternates`
 *     there would be inherited by every route, canonicalizing all of them to
 *     "/". Canonical is inherently per-page, so it belongs per-page.
 *   - **A per-page `openGraph` REPLACES the parent's (Next merges shallowly).**
 *     So we must set `openGraph.images` here explicitly: the root
 *     `opengraph-image` file convention only injects `og:image` into the root
 *     segment (the homepage), NOT into child routes that re-declare
 *     `openGraph`. Without an explicit image, every content page would ship
 *     with no social card. We point at the route-generated brand card
 *     (`/opengraph-image`); on the homepage the file convention overrides this
 *     with the same image, so there's no duplicate. (#176 review.)
 *   - **`robots` must be OMITTED, not set to `undefined`, when indexable.**
 *     Returning `robots: undefined` replaces the root layout's `googleBot`
 *     directives (max-image-preview / max-snippet) under shallow-merge; omitting
 *     the key lets them be inherited. (#176 review.)
 *
 * The `title` string is composed by the root layout's `title.template`
 * (`"%s | CompliDrop"`), so pass the bare page title (no brand suffix).
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
  const ogImage = absoluteUrl("/opengraph-image");
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
      images: [ogImage],
    },
    twitter: {
      card: "summary_large_image",
      title,
      description,
      images: [ogImage],
    },
    // Omit `robots` entirely when indexable so the root layout's directives are
    // inherited; only set it to deindex (setting `undefined` would wipe them).
    ...(index ? {} : { robots: { index: false, follow: true } }),
  };
}
