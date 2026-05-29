import { describe, it, expect, vi } from "vitest";
import { render, screen } from "@testing-library/react";
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
function jsonLdTypes(container: HTMLElement): string[] {
  return Array.from(container.querySelectorAll('script[type="application/ld+json"]'))
    .map((el) => {
      try {
        return (JSON.parse(el.textContent ?? "{}") as { "@type"?: string })["@type"] ?? "";
      } catch {
        return "";
      }
    })
    .filter(Boolean);
}

describe("FAQ page", () => {
  it("renders the questions, FAQPage schema, and a signup CTA", () => {
    const { container } = render(<FaqPage />);
    expect(screen.getByRole("heading", { level: 1, name: /frequently asked questions/i })).toBeInTheDocument();
    expect(screen.getByText(/what is complidrop\?/i)).toBeInTheDocument();
    expect(jsonLdTypes(container)).toContain("FAQPage");
    expect(linkHrefs()).toContain("/register");
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
  it("speaks to venues, links the additional-insured gotcha, and converts", () => {
    render(<EventVenuesPage />);
    expect(screen.getByRole("heading", { level: 1, name: /event venues/i })).toBeInTheDocument();
    expect(screen.getAllByText(/additional insured/i).length).toBeGreaterThan(0);
    const hrefs = linkHrefs();
    expect(hrefs).toContain("/glossary/additional-insured-vs-certificate-holder");
    expect(hrefs).toContain("/register");
  });
});
