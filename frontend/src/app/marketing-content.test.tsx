import { readFileSync } from "node:fs";
import { resolve } from "node:path";
import { describe, it, expect, vi } from "vitest";
import { render, screen, within } from "@testing-library/react";
import type { ReactNode } from "react";

import FaqPage from "./faq/page";
import GlossaryIndexPage from "./glossary/page";
import CoiVsSpreadsheetPage from "./coi-tracking-software-vs-spreadsheet/page";
import EventVenuesPage, { metadata as eventVenuesMeta } from "./coi-tracking-for-event-venues/page";
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

/**
 * The two sentences `docs/rule-engine/G1-COUNSEL-BRIEF.md` §0 CLM-4 quotes and
 * asks the attorney to bless. Spelled out here so the brief's claim that they
 * are "pinned verbatim by test" is TRUE — the `ExportService.Disclaimer`
 * mechanism, in TypeScript. The key-phrase regexes elsewhere in this file catch
 * a claim REGRESSION (someone reintroducing "we don't sell or share your data");
 * only these catch a REWORD, which is the thing that must not happen silently to
 * copy an attorney has signed off on. Editing either string is therefore a
 * deliberate act that re-opens CLM-4. (#403)
 */
const CLM4_FAQ_SHARING_SENTENCE =
  "We don't sell your data, and we share it only as described in our Privacy Policy — with the service providers that help us run CompliDrop, and where the law or the protection of rights and safety requires it.";
const CLM4_PRIVACY_CCPA_PARENTHETICAL =
  "(we don't sell personal information, and we don't share it for targeted advertising)";

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

  // The FAQ answers are emitted as FAQPage JSON-LD — the exact strings search
  // engines and AI assistants quote — so a claim that contradicts our own
  // Privacy Policy is a machine-amplified one. (#403)
  it("qualifies the data-sharing answer instead of claiming we don't share (#403)", () => {
    const { container } = render(<FaqPage />);
    const answers = faqPairs(container).map((pair) => pair.answer);
    expect(answers.length).toBeGreaterThan(0);
    const joined = answers.join(" ");

    // "We don't sell or share your data" contradicts the policy's own
    // "Service providers we share data with" section — seven vendors, with
    // document contents going to Google to be read.
    expect(joined).not.toMatch(/sell or share/i);
    expect(joined).not.toMatch(/(don[’']t|do not|never) share your data/i);

    // What must survive: the no-sale promise, qualified by where the sharing
    // that DOES happen is disclosed.
    const security = answers.find((answer) => /sell your data/i.test(answer));
    expect(security, "an answer must still carry the no-sale promise").toBeTruthy();
    expect(security!).toMatch(/we share it only as described in our Privacy Policy/i);

    // …and the qualification must not be NARROWER than the policy it points at.
    // The policy introduces its vendor list with "These include:" (non-exhaustive
    // by construction) and reserves TWO disclosure channels — "if required by law,
    // or to protect the rights, safety, and security of CompliDrop, our customers,
    // or the public". An FAQ sentence promising only the LISTED providers, or only
    // the legally-compelled half, is a promise the policy doesn't keep.
    expect(security!).not.toMatch(/only with the service providers listed/i);
    expect(security!).toMatch(/rights and safety/i);

    // …and the visible answer is the same string the schema carries.
    expect(screen.getByText(security!)).toBeInTheDocument();
  });

  it("describes what the product does instead of guaranteeing an outcome the Terms disclaim (#403)", () => {
    const { container } = render(<FaqPage />);
    const joined = faqPairs(container)
      .map((pair) => pair.answer)
      .join(" ");

    // The Terms disclaim extraction accuracy AND reminder delivery ("not a
    // guaranteed notice"), so no answer may promise that nothing gets past us.
    expect(joined).not.toMatch(/can[’']t slip through/i);
    expect(joined).not.toMatch(/before anything expires/i);
    expect(joined).toMatch(/won[’']t slip through unnoticed/i);
    expect(joined).toMatch(/sends reminders/i);
  });
});

// The counsel gate quotes both sentences and asks for a yes/no on the exact
// wording, so a reword between the ask and the answer would leave the attorney
// blessing a string that no longer ships. (#403 / G1-COUNSEL-BRIEF §0 CLM-4)
describe("Counsel-gate copy (CLM-4)", () => {
  it("ships both CLM-4 sentences byte-for-byte as the counsel brief quotes them (#403)", () => {
    const faq = render(<FaqPage />);
    const security = faqPairs(faq.container)
      .map((pair) => pair.answer)
      .find((answer) => /sell your data/i.test(answer));
    expect(security, "an answer must still carry the no-sale promise").toBeTruthy();
    // The FAQPage JSON-LD payload search engines and AI assistants quote.
    expect(security!).toContain(CLM4_FAQ_SHARING_SENTENCE);
    // …and the visible <p> carries the identical bytes. `getByText` ignores
    // <script>, so this reads the rendered page, not the schema; the assertion
    // is against the LITERAL above, not against the page's own string.
    expect(
      within(screen.getByRole("main")).getByText(security!).textContent,
    ).toContain(CLM4_FAQ_SHARING_SENTENCE);
    faq.unmount();

    // The policy half. It sits mid-paragraph across three source lines, so
    // collapse JSX whitespace before matching the phrase.
    const privacy = render(<PrivacyPolicyPage />);
    expect((privacy.container.textContent ?? "").replace(/\s+/g, " ")).toContain(
      CLM4_PRIVACY_CCPA_PARENTHETICAL,
    );
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

// ContentCta's default body renders on the FAQ, the glossary index, every
// glossary term page, the comparison page and /contact — one string, five
// surfaces, and nothing asserted it before (#403). These assertions run over a
// RENDERED page body, not the JSON-LD, so restoring the old claim goes red.
describe("Shared content CTA (#403)", () => {
  it("uses the softened default body where a page takes it", () => {
    render(<FaqPage />);
    const main = within(screen.getByRole("main"));
    expect(
      main.getByText(/sends reminders ahead of the expiration date/i),
    ).toBeInTheDocument();
    expect(main.queryByText(/reminds you before anything expires/i)).toBeNull();
  });

  it("keeps the venue page's override to what the product actually does", () => {
    render(<EventVenuesPage />);
    const main = within(screen.getByRole("main"));
    // The override claimed CompliDrop "renews" the certificates. It renews
    // nothing: the reminder emails the vendor an upload link and the VENDOR
    // acts (ReminderBackgroundService.BuildVendorBody). There is no carrier
    // integration anywhere in the API.
    expect(main.queryByText(/renews your vendors/i)).toBeNull();
    expect(
      main.getByText(/chases the renewal ahead of the expiration date/i),
    ).toBeInTheDocument();
    // …and the lead no longer guarantees the outcome either.
    expect(main.queryByText(/never the reason a booking/i)).toBeNull();
    expect(
      main.getByText(/see who is still missing while there is time to chase them/i),
    ).toBeInTheDocument();

    // The fourth guarantee on this page, in the "How CompliDrop works for
    // venues" section. Both halves are falsifiable: a reminder reaches the
    // vendor only when the reminder has NotifyVendor on AND the vendor has a
    // non-blank ContactEmail (ReminderBackgroundService, the recipients block)
    // AND the address isn't suppressed (ADR 0031) — and whether the list is
    // clean on the day depends on the vendor uploading, which CompliDrop does
    // not control. Terms: "a helpful nudge, not a guaranteed notice".
    expect(main.queryByText(/looking at a clean list/i)).toBeNull();
    expect(main.queryByText(/reminders go out automatically/i)).toBeNull();
    expect(
      main.getByText(/marks who is covered and who still owes you a document/i),
    ).toBeInTheDocument();
  });

  it("keeps the venue page's <meta name=description> off the same guarantee", () => {
    // The page's meta description is machine-quoted the same way the FAQ's
    // JSON-LD is, and it carried the outcome promise in its own words
    // ("so a missing certificate never holds up a booking").
    const description = String(eventVenuesMeta.description);
    expect(description).not.toMatch(/never holds up a booking/i);
    expect(description).toMatch(/see who is still missing before the day/i);
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
    // The COMPUTE hosts receive the data too — the API container every upload is
    // posted to, and the host that serves the app (README § Deploy). They were
    // missing while the FAQ told readers this list is where the sharing is
    // disclosed. (#403)
    expect(screen.getByText(/Railway/i)).toBeInTheDocument();
    expect(screen.getByText(/Vercel/i)).toBeInTheDocument();
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

  it("Privacy Policy backs the FAQ's qualified sharing claim, and makes no unqualified one of its own (#403)", () => {
    const { container } = render(<PrivacyPolicyPage />);
    // The FAQ routes readers here with "we share it only as described in our
    // Privacy Policy" — true only while this page actually describes the
    // sharing under a heading a reader can find.
    //
    // NOT "the service providers listed in our Privacy Policy": that is the
    // TERMS' wording (terms/page.tsx § Your content), and it is explicitly
    // rejected for the FAQ by the sibling assertion
    // `not.toMatch(/only with the service providers listed/i)` above — the
    // policy introduces its vendors with "These include:" and reserves a
    // rights-and-safety disclosure channel beside the legal one, so a
    // list-scoped promise is narrower than the policy it points at. Quoting it
    // here would invite an editor to reintroduce round 1's confirmed bug.
    expect(
      screen.getByRole("heading", { name: /service providers we share data with/i }),
    ).toBeInTheDocument();
    // The one that matters most: full document contents go to Google to be read.
    expect(screen.getByText(/Document AI/i)).toBeInTheDocument();

    const text = container.textContent ?? "";
    // The policy must not contradict itself either — an unqualified "we don't
    // sell or share it" sat in the same document as that vendor list (seven
    // entries when #403 opened; nine since this diff added Railway and Vercel).
    // CCPA "sharing" is a term of art (targeted advertising), so say that.
    expect(text).not.toMatch(/sell or share/i);
    expect(text).toMatch(/we do not sell your data/i);
    expect(text).toMatch(/share it for targeted advertising/i);
  });

  it("Privacy Policy addresses the vendor who arrives from the portal's notice, not only account holders (#404)", () => {
    // The portal's notice-at-collection links here, so this page has to cover
    // the person reading it: someone with no account who was sent an upload
    // link. A policy that only speaks to customers is not notice to them.
    // The pre-existing paragraph below covers the person NAMED inside a
    // document; this section covers the person who UPLOADS one — different
    // reader, different remedy, so neither substitutes for the other.
    const { container } = render(<PrivacyPolicyPage />);
    expect(
      screen.getByRole("heading", { name: /if you were sent an upload link/i }),
    ).toBeInTheDocument();

    const text = (container.textContent ?? "").replace(/\s+/g, " ");
    // The three collection facts the portal notice summarizes must be findable here.
    expect(text).toMatch(/you do not need an account/i);
    expect(text).toMatch(/your file is stored, and it is read automatically/i);
    expect(text).toMatch(/the page sets the analytics cookie/i);
    // …and the reader is told who actually controls the record they care about.
    expect(text).toMatch(/that business decides what to do with the document/i);
    // The other reader keeps their own paragraph — this section did not replace it.
    expect(text).toMatch(/if your information appears inside a document/i);
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

  it("Terms are drafted to reach a portal uploader — so no record may claim otherwise (#404)", () => {
    // ADR 0054 declines to link /terms from the portal. Its FIRST rationale was
    // "the Terms bind a customer; the vendor is not one" — falsified by this
    // page, which accepts on "or using" and whose acceptable-use clause governs
    // uploading, the one act the portal vendor performs. A recorded rejection
    // does not get re-examined, so a false reason on file is a durable defect;
    // the decision now stands on RELEVANCE and the binding question is routed
    // to counsel as CLM-5 (iv).
    const { container } = render(<TermsOfServicePage />);
    const text = (container.textContent ?? "").replace(/\s+/g, " ");

    // (1) Acceptance is not account-gated.
    expect(
      text,
      "the Terms no longer accept on mere USE — ADR 0054 Option E and counsel item CLM-5 (iv) both rest on this clause and need re-reading",
    ).toMatch(/creating an account or using [^.]*, you agree to them/i);
    // (2) …and what they govern includes the vendor's act.
    expect(
      text,
      "the Terms' acceptable-use clause no longer governs uploading — same two records to re-read",
    ).toMatch(/upload content you don't have the right to upload/i);

    // The ADR must not carry the retired reason. Both forms it took — and the
    // hits, not the 20 KB document, are what a failure prints.
    const adr = readFileSync(
      resolve(__dirname, "..", "..", "..", "docs", "adr", "0054-portal-gives-notice-at-collection.md"),
      "utf8",
    ).replace(/\s+/g, " ");
    const retired = [
      /The Terms bind a \*customer\*; the vendor is not one/i,
      /The vendor is not entering a subscription agreement/i,
    ];
    expect(
      retired.filter((claim) => claim.test(adr)).map(String),
      "ADR 0054 states a reason for not linking /terms that the shipped Terms contradict — a recorded rejection does not get re-examined, so the reason on file has to be the true one",
    ).toEqual([]);
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
