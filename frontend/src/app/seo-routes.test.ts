import { describe, it, expect } from "vitest";
import robots, { AI_CRAWLERS } from "./robots";
import sitemap from "./sitemap";
import manifest from "./manifest";
import { absoluteUrl } from "@/lib/site";
import { GLOSSARY_TERMS } from "@/lib/glossary";

describe("robots.txt", () => {
  const result = robots();
  const rules = Array.isArray(result.rules) ? result.rules : [result.rules];

  const disallowList = (d: string | string[] | undefined): string[] =>
    d === undefined ? [] : Array.isArray(d) ? d : [d];

  it("points crawlers at the sitemap", () => {
    expect(String(result.sitemap)).toMatch(/\/sitemap\.xml$/);
  });

  it("keeps the token-gated portal and api out of the index", () => {
    const wildcard = rules.find((r) => r.userAgent === "*");
    expect(wildcard).toBeDefined();
    const disallowed = disallowList(wildcard!.disallow);
    expect(disallowed).toContain("/portal/");
    expect(disallowed).toContain("/api/");
    expect(disallowed).toContain("/dashboard");
  });

  it("welcomes every declared AI assistant crawler — each allowed '/' (#176)", () => {
    // Iterate the ACTUAL exported list, so deleting a bot (e.g. CCBot — a real
    // GEO training source) fails this test rather than passing a stale subset.
    // Blocking OAI-SearchBot / Google-Extended would remove us from ChatGPT
    // Search / Google AI Overviews.
    expect(AI_CRAWLERS.length).toBeGreaterThan(0);
    for (const bot of AI_CRAWLERS) {
      const rule = rules.find((r) => r.userAgent === bot);
      expect(rule, `expected a robots rule for ${bot}`).toBeDefined();
      expect(rule!.allow).toBe("/");
    }
  });
});

describe("sitemap.xml", () => {
  const entries = sitemap();
  const urls = entries.map((e) => e.url);

  it("lists every public marketing page", () => {
    expect(urls).toContain(absoluteUrl("/"));
    expect(urls).toContain(absoluteUrl("/faq"));
    expect(urls).toContain(absoluteUrl("/glossary"));
    expect(urls).toContain(absoluteUrl("/coi-tracking-for-event-venues"));
    expect(urls).toContain(absoluteUrl("/coi-tracking-software-vs-spreadsheet"));
  });

  it("includes one entry per glossary term", () => {
    for (const term of GLOSSARY_TERMS) {
      expect(urls).toContain(absoluteUrl(`/glossary/${term.slug}`));
    }
  });

  it("omits auth pages and app surfaces", () => {
    const joined = urls.join(" ");
    expect(joined).not.toMatch(/\/login\b/);
    expect(joined).not.toMatch(/\/register\b/);
    expect(joined).not.toMatch(/\/dashboard\b/);
    expect(joined).not.toMatch(/\/portal\b/);
  });
});

describe("manifest.json", () => {
  const result = manifest();

  it("declares the PWA basics with the brand theme", () => {
    expect(result.name).toMatch(/CompliDrop/);
    expect(result.start_url).toBe("/");
    expect(result.theme_color).toBe("#0EA5E9");
    expect(result.icons?.length ?? 0).toBeGreaterThan(0);
    expect(result.icons?.[0]?.src).toBeTruthy();
  });
});
