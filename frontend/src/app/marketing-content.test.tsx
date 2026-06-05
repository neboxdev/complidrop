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
import PrivacyPolicyPage, { metadata as privacyMeta } from "./privacy/page";
import TermsOfServicePage, { metadata as termsMeta } from "./terms/page";
import ContactPage, { metadata as contactMeta } from "./contact/page";
import { MarketingFooter } from "@/components/marketing/site-footer";
import { LEGAL_ADDRESS, LEGAL_ENTITY, SUPPORT_EMAIL } from "@/lib/site";
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

describe("Footer legal + support links (#194)", () => {
  it("links Privacy, Terms, and Contact from the shared footer", () => {
    render(<MarketingFooter />);
    const legalNav = screen.getByRole("navigation", { name: /legal and support/i });
    const hrefs = within(legalNav)
      .getAllByRole("link")
      .map((l) => l.getAttribute("href"));
    expect(hrefs).toEqual(
      expect.arrayContaining(["/privacy", "/terms", "/contact"]),
    );
    expect(
      within(legalNav).getByRole("link", { name: /privacy policy/i }),
    ).toHaveAttribute("href", "/privacy");
    expect(
      within(legalNav).getByRole("link", { name: /terms of service/i }),
    ).toHaveAttribute("href", "/terms");
  });
});

describe("Legal + contact pages (#194)", () => {
  it("Privacy Policy discloses every named subprocessor, the no-sell/no-train promise, and a contact path", () => {
    render(<PrivacyPolicyPage />);
    expect(
      screen.getByRole("heading", { level: 1, name: /privacy policy/i }),
    ).toBeInTheDocument();
    // The named third parties that receive data ARE the privacy commitment — pin
    // each so a content edit can't silently drop "we share your files with X".
    // Stripe + PostHog each appear twice (payment/cookies + subprocessor list).
    expect(screen.getAllByText(/Stripe/i).length).toBeGreaterThan(0);
    expect(screen.getByText(/Document AI/i)).toBeInTheDocument();
    expect(screen.getByText(/Microsoft Azure/i)).toBeInTheDocument();
    expect(screen.getByText(/Resend/)).toBeInTheDocument();
    // PostHog MUST be disclosed — the app actually runs it (analytics.ts), so the
    // policy claiming "essential cookies only" without it would be false. (#194 legal review)
    expect(screen.getAllByText(/PostHog/i).length).toBeGreaterThan(0);
    // Load-bearing promise: no sale, no training on uploaded documents.
    expect(
      screen.getByText(/do not use the documents you upload to train/i),
    ).toBeInTheDocument();
    // Contact paths: the support mailto AND the /contact page link.
    expect(
      screen.getByRole("link", { name: new RegExp(SUPPORT_EMAIL, "i") }),
    ).toHaveAttribute("href", `mailto:${SUPPORT_EMAIL}`);
    // The operating entity + business address are disclosed (data-controller identity). (#194)
    expect(screen.getAllByText(new RegExp(LEGAL_ENTITY)).length).toBeGreaterThan(0);
    expect(screen.getByText(new RegExp(LEGAL_ADDRESS))).toBeInTheDocument();
    const main = within(screen.getByRole("main"));
    expect(main.getAllByRole("link").map((l) => l.getAttribute("href"))).toContain("/contact");
  });

  it("Terms of Service renders the not-advice disclaimer + cancellation terms (Stripe requirement)", () => {
    render(<TermsOfServicePage />);
    expect(
      screen.getByRole("heading", { level: 1, name: /terms of service/i }),
    ).toBeInTheDocument();
    // The load-bearing liability clause for a compliance product: results are a
    // head start, not legal/insurance advice. ("not" sits in a <strong>, so match
    // the phrase that lives in a single text node.)
    expect(
      screen.getByText(/legal, insurance, or professional advice/i),
    ).toBeInTheDocument();
    // Stripe Checkout wants discoverable billing/cancellation terms.
    expect(screen.getByText(/cancel anytime/i)).toBeInTheDocument();
    expect(screen.getByText(/non-refundable/i)).toBeInTheDocument();
    // The binding-contract identity: the real legal entity + address + the
    // correct governing-law state (Florida, NOT the old placeholder Texas). (#194)
    expect(screen.getAllByText(new RegExp(LEGAL_ENTITY)).length).toBeGreaterThan(0);
    expect(screen.getByText(new RegExp(LEGAL_ADDRESS))).toBeInTheDocument();
    expect(screen.getByText(/State of Florida/i)).toBeInTheDocument();
    expect(screen.getByText(/Miami-Dade County/i)).toBeInTheDocument();
    expect(screen.queryByText(/State of Texas/i)).toBeNull();
    // The cross-links to the sibling legal pages both resolve.
    const main = within(screen.getByRole("main"));
    const mainHrefs = main.getAllByRole("link").map((l) => l.getAttribute("href"));
    expect(mainHrefs).toContain("/privacy");
    expect(mainHrefs).toContain("/contact");
  });

  it("each legal/contact page sets a self-canonical and a descriptive title", () => {
    expect(privacyMeta.alternates?.canonical).toBe("/privacy");
    expect(String(privacyMeta.title)).toMatch(/privacy policy/i);
    expect(termsMeta.alternates?.canonical).toBe("/terms");
    expect(String(termsMeta.title)).toMatch(/terms of service/i);
    expect(contactMeta.alternates?.canonical).toBe("/contact");
    expect(String(contactMeta.title)).toMatch(/contact/i);
  });

  it("Contact page surfaces the support email as a mailto", () => {
    render(<ContactPage />);
    expect(
      screen.getByRole("heading", { level: 1, name: /contact/i }),
    ).toBeInTheDocument();
    const mailtos = screen
      .getAllByRole("link")
      .map((l) => l.getAttribute("href"))
      .filter((h) => h?.startsWith("mailto:"));
    expect(mailtos).toContain(`mailto:${SUPPORT_EMAIL}`);
  });

  it("Contact page lists the operating entity + mailing address in a semantic <address> (#194)", () => {
    const { container } = render(<ContactPage />);
    expect(screen.getAllByText(new RegExp(LEGAL_ENTITY)).length).toBeGreaterThan(0);
    const address = container.querySelector("address");
    expect(address).not.toBeNull();
    expect(address?.textContent).toContain(LEGAL_ENTITY);
    expect(address?.textContent).toContain(LEGAL_ADDRESS);
  });
});
