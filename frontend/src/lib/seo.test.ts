import { describe, it, expect } from "vitest";
import { pageMetadata } from "./seo";

describe("pageMetadata", () => {
  const meta = pageMetadata({ title: "FAQ", description: "Answers about CompliDrop", path: "/faq" });

  it("sets a self-referencing canonical from the path", () => {
    expect(meta.alternates?.canonical).toBe("/faq");
  });

  it("ships an absolute OG image so content pages aren't card-less (#176 bug)", () => {
    // The root opengraph-image file convention only covers the homepage segment;
    // a per-page openGraph block replaces the inherited image, so it must set
    // its own. Regression guard for the og:image/twitter:image fix.
    const og = meta.openGraph as { images?: unknown; url?: unknown; type?: unknown; siteName?: unknown };
    expect(og.type).toBe("website");
    expect(og.siteName).toBe("CompliDrop");
    expect(String(og.url)).toMatch(/\/faq$/);
    const ogImages = og.images as string[];
    expect(String(ogImages[0])).toMatch(/^https?:\/\/.*\/opengraph-image$/);

    const tw = meta.twitter as { card?: unknown; images?: string[] };
    expect(tw.card).toBe("summary_large_image");
    expect(String(tw.images![0])).toMatch(/\/opengraph-image$/);
  });

  it("OMITS robots when indexable so the layout's googleBot directives survive (#176 bug)", () => {
    // Returning `robots: undefined` would REPLACE the layout's directives under
    // shallow-merge; the key must be absent entirely.
    expect("robots" in meta).toBe(false);
  });

  it("deindexes only when index:false is explicitly passed", () => {
    const noindex = pageMetadata({ title: "x", description: "d", path: "/x", index: false });
    expect(noindex.robots).toEqual({ index: false, follow: true });
  });
});
