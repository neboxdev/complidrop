import { describe, it, expect, vi } from "vitest";
import { render, screen, within } from "@testing-library/react";
import type { ReactNode } from "react";

import FaqPage from "./faq/page";
import GlossaryIndexPage from "./glossary/page";
import CoiVsSpreadsheetPage from "./coi-tracking-software-vs-spreadsheet/page";
import EventVenuesPage from "./coi-tracking-for-event-venues/page";
import GlossaryTermPage, {
  generateStaticParams,
  generateMetadata,
} from "./glossary/[slug]/page";
import { GLOSSARY_TERMS } from "@/lib/glossary";

// Render next/link as a plain anchor and pin auth to anonymous (the shared
// <MarketingHeader> island reads useMe). Same approach as page.test.tsx.
vi.mock("next/link", () => ({
  __esModule: true,
  default: ({ children, href, ...rest }: { children: ReactNode; href: string }) => (
    <a href={href} {...rest}>
      {children}
    </a>
  ),
}));
vi.mock("@/hooks/useAuth", () => ({ useMe: () => ({ data: null }) }));

const linkHrefs = () => screen.getAllByRole("link").map((el) => el.getAttribute("href"));

/** Parse every JSON-LD <script> rendered into a container. */
function jsonLdNodes(container: HTMLElement): Array<Record<string, unknown>> {
  return Array.from(container.querySelectorAll('script[type="application/ld+json"]'))
    .map((el) => {
      try {
        return JSON.parse(el.textContent ?? "{}") as Record<string, unknown>;
      } catch {
        return {};
      }
    })
    .filter((node) => typeof node["@type"] === "string");
}

function jsonLdTypes(container: HTMLElement): string[] {
  return jsonLdNodes(container).map((node) => node["@type"] as string);
}

/** Pull the Q&A pairs out of the FAQPage node, if present. */
function faqPairs(container: HTMLElement): Array<{ name: string; answer: string }> {
  const faq = jsonLdNodes(container).find((node) => node["@type"] === "FAQPage");
  const entities = (faq?.mainEntity ?? []) as Array<{
    name: string;
    acceptedAnswer: { text: string };
  }>;
  return entities.map((entity) => ({ name: entity.name, answer: entity.acceptedAnswer.text }));
}

describe("FAQ page", () => {
  it("renders a body CTA and FAQPage schema that matches the visible Q&A verbatim", () => {
    const { container } = render(<FaqPage />);
    expect(screen.getByRole("heading", { level: 1, name: /frequently asked questions/i })).toBeInTheDocument();

    // The page's OWN ContentCta lives in <main>, so this proves the page body
    // converts — not just the shared header/footer /register links.
    const main = within(screen.getByRole("main"));
    expect(main.getAllByRole("link").map((l) => l.getAttribute("href"))).toContain("/register");

    // The FAQPage schema MUST match the visible Q&A verbatim — Google's
    // requirement, and the manual-action risk the code claims to guard. Assert
    // every schema question AND answer is actually rendered on the page (not
    // just that a FAQPage script exists).
    const pairs = faqPairs(container);
    expect(pairs.length).toBeGreaterThan(0);
    for (const { name, answer } of pairs) {
      expect(screen.getByText(name)).toBeInTheDocument();
      expect(screen.getByText(answer)).toBeInTheDocument();
    }
  });
});

describe("Glossary index", () => {
  it("links to every term page", () => {
    render(<GlossaryIndexPage />);
    expect(screen.getByRole("heading", { level: 1, name: /glossary/i })).toBeInTheDocument();
    const hrefs = linkHrefs();
    for (const term of GLOSSARY_TERMS) {
      expect(hrefs).toContain(`/glossary/${term.slug}`);
    }
  });
});

describe("Glossary term page (dynamic route)", () => {
  it("statically generates one page per known term", () => {
    const params = generateStaticParams();
    expect(params).toHaveLength(GLOSSARY_TERMS.length);
    expect(params).toContainEqual({ slug: "certificate-of-insurance" });
  });

  it("builds keyword-shaped metadata for a known term and a self-canonical", async () => {
    const meta = await generateMetadata({ params: Promise.resolve({ slug: "certificate-of-insurance" }) });
    expect(String(meta.title)).toMatch(/certificate of insurance/i);
    expect(meta.alternates?.canonical).toBe("/glossary/certificate-of-insurance");
  });

  it("returns empty metadata for an unknown slug (no crash)", async () => {
    const meta = await generateMetadata({ params: Promise.resolve({ slug: "not-a-real-term" }) });
    expect(meta).toEqual({});
  });

  it("renders the definition lead and DefinedTerm schema", async () => {
    const ui = await GlossaryTermPage({ params: Promise.resolve({ slug: "additional-insured-vs-certificate-holder" }) });
    const { container } = render(ui);
    expect(
      screen.getByRole("heading", { level: 1, name: /additional insured vs\. certificate holder/i }),
    ).toBeInTheDocument();
    expect(jsonLdTypes(container)).toContain("DefinedTerm");
    // Cross-links to related terms.
    expect(linkHrefs()).toContain("/glossary/certificate-holder");
  });
});

describe("Comparison page (vs. spreadsheet)", () => {
  it("renders a comparison table and the buyer keyword, with no tech jargon", () => {
    render(<CoiVsSpreadsheetPage />);
    expect(screen.getByRole("table")).toBeInTheDocument();
    expect(screen.getAllByText(/certificate of insurance/i).length).toBeGreaterThan(0);
    expect(screen.queryByText(/\bOCR\b/i)).toBeNull();
    expect(linkHrefs()).toContain("/register");
  });
});

describe("Event venues use-case page", () => {
  it("speaks to venues, links the additional-insured gotcha in the body, and converts", () => {
    render(<EventVenuesPage />);
    expect(screen.getByRole("heading", { level: 1, name: /event venues/i })).toBeInTheDocument();

    // Scope to <main> so the shared footer's hardcoded glossary/register links
    // can't satisfy these — the page body itself must link the gotcha + CTA.
    const main = within(screen.getByRole("main"));
    expect(main.getAllByText(/additional insured/i).length).toBeGreaterThan(0);
    const bodyHrefs = main.getAllByRole("link").map((l) => l.getAttribute("href"));
    expect(bodyHrefs).toContain("/glossary/additional-insured-vs-certificate-holder");
    expect(bodyHrefs).toContain("/register");
  });
});
