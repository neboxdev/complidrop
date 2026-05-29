import { describe, it, expect } from "vitest";
import {
  organizationLd,
  webSiteLd,
  softwareApplicationLd,
  faqPageLd,
  breadcrumbLd,
  definedTermLd,
} from "./structured-data";
import { PLANS } from "./plans";

describe("structured-data builders", () => {
  it("describes the Organization with absolute URLs", () => {
    const org = organizationLd();
    expect(org["@type"]).toBe("Organization");
    expect(org.name).toBe("CompliDrop");
    expect(String(org.url)).toMatch(/^https?:\/\//);
    expect(String(org.logo)).toMatch(/^https?:\/\//);
  });

  it("links the WebSite to the Organization by @id", () => {
    const site = webSiteLd();
    expect(site["@type"]).toBe("WebSite");
    expect((site.publisher as Record<string, unknown>)["@id"]).toBe(organizationLd()["@id"]);
  });

  describe("SoftwareApplication", () => {
    const sw = softwareApplicationLd();

    it("is a BusinessApplication offered on the web", () => {
      expect(sw["@type"]).toBe("SoftwareApplication");
      expect(sw.applicationCategory).toBe("BusinessApplication");
      expect(sw.operatingSystem).toBe("Web");
    });

    it("prices the Offer from the single source of truth in lib/plans (no drift)", () => {
      const offer = sw.offers as Record<string, unknown>;
      expect(offer.priceCurrency).toBe("USD");
      // The schema price must equal the Pro label's numeric part — if someone
      // changes the price in plans.ts, this stays in sync automatically.
      expect(offer.price).toBe(PLANS.pro.monthlyPriceLabel.replace(/[^0-9.]/g, ""));
    });

    it("does NOT fabricate ratings (manual-action risk, #176)", () => {
      // We have no real reviews yet. A synthesized aggregateRating/review is a
      // Google manual-action risk — this guards against one being slipped in.
      expect(sw).not.toHaveProperty("aggregateRating");
      expect(sw).not.toHaveProperty("review");
    });
  });

  it("maps FAQ items into a FAQPage whose answers match the input verbatim", () => {
    const items = [
      { question: "What is a COI?", answer: "A certificate of insurance." },
      { question: "What does it cost?", answer: "Free for 5 documents." },
    ];
    const faq = faqPageLd(items);
    expect(faq["@type"]).toBe("FAQPage");
    const entities = faq.mainEntity as Array<Record<string, unknown>>;
    expect(entities).toHaveLength(2);
    expect(entities[0].name).toBe("What is a COI?");
    expect((entities[0].acceptedAnswer as Record<string, unknown>).text).toBe(
      "A certificate of insurance.",
    );
  });

  it("numbers breadcrumb positions from 1 with absolute item URLs", () => {
    const crumbs = breadcrumbLd([
      { name: "Home", path: "/" },
      { name: "Glossary", path: "/glossary" },
    ]);
    const list = crumbs.itemListElement as Array<Record<string, unknown>>;
    expect(list[0].position).toBe(1);
    expect(list[1].position).toBe(2);
    expect(String(list[1].item)).toMatch(/\/glossary$/);
    expect(String(list[1].item)).toMatch(/^https?:\/\//);
  });

  it("builds a DefinedTerm that points back at the glossary set", () => {
    const term = definedTermLd({
      name: "Certificate of Insurance (COI)",
      description: "A one-page proof of coverage.",
      slug: "certificate-of-insurance",
    });
    expect(term["@type"]).toBe("DefinedTerm");
    expect(String(term.url)).toMatch(/\/glossary\/certificate-of-insurance$/);
    expect((term.inDefinedTermSet as Record<string, unknown>)["@type"]).toBe("DefinedTermSet");
  });
});
