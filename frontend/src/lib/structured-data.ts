/**
 * Typed JSON-LD (schema.org) builders for CompliDrop's marketing surface.
 *
 * Why structured data: it's how search engines and AI assistants understand the
 * page beyond prose — the entity ("CompliDrop is a SoftwareApplication"), the
 * price, and the Q&A pairs they can lift verbatim. The GEO research in [#176]
 * rates schema as *modest* on its own (do the basics, don't over-invest), so we
 * keep to the high-signal types and wire them off `lib/site.ts` so the facts
 * match the rest of the site by construction.
 *
 * Rendering: pass any builder's output to `<JsonLd data={…} />`
 * (`components/JsonLd.tsx`), which serializes it into an inline
 * `<script type="application/ld+json">` with the required XSS escaping.
 *
 * Input invariant: every value these builders receive today is a static,
 * build-time constant (brand facts, the glossary/FAQ arrays, prices from
 * `lib/plans`). `<JsonLd>`'s `<`→`<` escaping is correct regardless, but if
 * a dynamic or user-supplied source is ever fed in (a CMS, customer-named
 * entities, review text), validate it at that boundary — and never synthesize
 * facts (see the no-`aggregateRating` note on `softwareApplicationLd`).
 *
 * `@id` values are stable URL fragments so entities can reference one another
 * (e.g. SoftwareApplication.publisher → the Organization) across the graph.
 */
import {
  SITE_CATEGORY,
  SITE_DESCRIPTION,
  SITE_NAME,
  SITE_URL,
  PRO_PRICE_USD,
  LOGO_PATH,
  absoluteUrl,
} from "@/lib/site";

/** A JSON-LD node. Loose by design — schema.org shapes are open-ended. */
export type JsonLdData = Record<string, unknown>;

const ORG_ID = absoluteUrl("/#organization");
const WEBSITE_ID = absoluteUrl("/#website");
const SOFTWARE_ID = absoluteUrl("/#software");

/**
 * The company entity. Anchors brand recognition in Google's Knowledge Graph and
 * gives AI assistants a single canonical record to attach facts to. Rendered
 * site-wide in the root layout.
 *
 * `sameAs` is intentionally omitted until we have real, claimed profiles
 * (Crunchbase, G2, Capterra, Wikidata — the off-site work tracked in [#176]).
 * Pointing it at profiles that don't exist yet would assert false facts, so we
 * add entries only as each profile goes live.
 */
export function organizationLd(): JsonLdData {
  return {
    "@context": "https://schema.org",
    "@type": "Organization",
    "@id": ORG_ID,
    name: SITE_NAME,
    url: SITE_URL,
    logo: absoluteUrl(LOGO_PATH),
    description: SITE_DESCRIPTION,
  };
}

/** The site entity. No `SearchAction` — there is no public site-search endpoint, and declaring a fake one is worse than omitting it. */
export function webSiteLd(): JsonLdData {
  return {
    "@context": "https://schema.org",
    "@type": "WebSite",
    "@id": WEBSITE_ID,
    name: SITE_NAME,
    url: SITE_URL,
    publisher: { "@id": ORG_ID },
  };
}

/**
 * The product entity (homepage). Describes what we sell and the headline price.
 *
 * No `aggregateRating` / `review`: we have no real reviews yet, and fabricating
 * them is a Google manual-action risk the GEO research called out explicitly.
 * Add a real `aggregateRating` only once genuine review data exists — never
 * synthesize it.
 */
export function softwareApplicationLd(): JsonLdData {
  return {
    "@context": "https://schema.org",
    "@type": "SoftwareApplication",
    "@id": SOFTWARE_ID,
    name: SITE_NAME,
    applicationCategory: SITE_CATEGORY,
    operatingSystem: "Web",
    url: SITE_URL,
    description: SITE_DESCRIPTION,
    offers: {
      "@type": "Offer",
      price: PRO_PRICE_USD,
      priceCurrency: "USD",
    },
    publisher: { "@id": ORG_ID },
  };
}

/** A single FAQ entry. `answer` MUST be the plain-text form of what's visible on the page — Google requires FAQPage markup to match rendered content. */
export interface FaqItem {
  question: string;
  answer: string;
}

/**
 * FAQPage — the format AI assistants quote most directly. Drive both this and
 * the rendered Q&A from the same data array so the visible text and the schema
 * never diverge (a hard Google requirement, and a trust signal for AI).
 */
export function faqPageLd(items: readonly FaqItem[]): JsonLdData {
  return {
    "@context": "https://schema.org",
    "@type": "FAQPage",
    mainEntity: items.map((item) => ({
      "@type": "Question",
      name: item.question,
      acceptedAnswer: { "@type": "Answer", text: item.answer },
    })),
  };
}

/** One crumb: a visible label and the site-relative path it points at. */
export interface Breadcrumb {
  name: string;
  path: string;
}

/** BreadcrumbList — earns breadcrumb rich results and helps engines model site hierarchy. */
export function breadcrumbLd(items: readonly Breadcrumb[]): JsonLdData {
  return {
    "@context": "https://schema.org",
    "@type": "BreadcrumbList",
    itemListElement: items.map((item, index) => ({
      "@type": "ListItem",
      position: index + 1,
      name: item.name,
      item: absoluteUrl(item.path),
    })),
  };
}

/** A glossary term for {@link definedTermLd}. */
export interface DefinedTermInput {
  name: string;
  description: string;
  slug: string;
}

/**
 * DefinedTerm — marks a glossary entry as a definition belonging to our
 * glossary set. Definitional content is a strong AI-citation magnet, and the
 * `DefinedTermSet` link helps engines treat the glossary as a coherent body.
 */
export function definedTermLd({ name, description, slug }: DefinedTermInput): JsonLdData {
  return {
    "@context": "https://schema.org",
    "@type": "DefinedTerm",
    name,
    description,
    url: absoluteUrl(`/glossary/${slug}`),
    inDefinedTermSet: {
      "@type": "DefinedTermSet",
      name: "CompliDrop Compliance Glossary",
      url: absoluteUrl("/glossary"),
    },
  };
}
