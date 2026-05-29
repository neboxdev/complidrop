import type { MetadataRoute } from "next";
import { absoluteUrl } from "@/lib/site";
import { GLOSSARY_TERMS } from "@/lib/glossary";

// Only PUBLIC marketing pages belong here — the homepage, the content pages,
// and one entry per glossary term. Auth pages (/login, /register) and the app
// surfaces are deliberately omitted (the latter are also disallowed in
// robots.ts). Keep this in sync as content pages are added.
export default function sitemap(): MetadataRoute.Sitemap {
  const lastModified = new Date();

  const staticPages: MetadataRoute.Sitemap = [
    { url: absoluteUrl("/"), lastModified, changeFrequency: "weekly", priority: 1 },
    {
      url: absoluteUrl("/coi-tracking-for-event-venues"),
      lastModified,
      changeFrequency: "monthly",
      priority: 0.8,
    },
    {
      url: absoluteUrl("/coi-tracking-software-vs-spreadsheet"),
      lastModified,
      changeFrequency: "monthly",
      priority: 0.8,
    },
    { url: absoluteUrl("/faq"), lastModified, changeFrequency: "monthly", priority: 0.7 },
    { url: absoluteUrl("/glossary"), lastModified, changeFrequency: "monthly", priority: 0.6 },
  ];

  const glossaryPages: MetadataRoute.Sitemap = GLOSSARY_TERMS.map((term) => ({
    url: absoluteUrl(`/glossary/${term.slug}`),
    lastModified,
    changeFrequency: "monthly",
    priority: 0.5,
  }));

  return [...staticPages, ...glossaryPages];
}
