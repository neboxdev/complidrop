/**
 * Compliance & insurance glossary content — the data behind `/glossary` and the
 * templated `/glossary/[slug]` pages.
 *
 * Why a glossary at all: definitional content ("what is a certificate of
 * insurance", "additional insured vs certificate holder") is cheap to produce,
 * ranks for the exact questions small-business buyers type, and is a strong
 * AI-citation magnet (see [#176]). Each entry leads with a tight, plain-English
 * `definition` — that's the line search engines and AI assistants lift — then
 * expands into sections.
 *
 * Accuracy matters: this content is what AI may quote *as fact about our
 * brand's domain*, so the definitions are written to be correct, not just
 * keyword-friendly. Keep it that way.
 */

interface GlossarySection {
  heading: string;
  /** One or more plain-text paragraphs. */
  paragraphs: string[];
}

export interface GlossaryTerm {
  slug: string;
  /** Display name, e.g. "Certificate of Insurance (COI)". */
  term: string;
  /** Short, search-shaped page title, e.g. "What is a certificate of insurance (COI)?". */
  title: string;
  /**
   * The direct answer — one or two sentences, plain text. Used verbatim as the
   * meta description, the DefinedTerm schema description, and the page's lead
   * paragraph, so the visible content and the structured data always match.
   */
  definition: string;
  sections: GlossarySection[];
  /** Slugs of related terms, rendered as cross-links. */
  related: string[];
}

export const GLOSSARY_TERMS: readonly GlossaryTerm[] = [
  {
    slug: "certificate-of-insurance",
    term: "Certificate of Insurance (COI)",
    title: "What is a certificate of insurance (COI)?",
    definition:
      "A certificate of insurance (COI) is a one-page document from an insurer or broker that proves a business carries insurance. It summarizes the policy types, coverage limits, and effective and expiration dates — but it is a snapshot, not the policy itself, and it can fall out of date the moment a policy changes.",
    sections: [
      {
        heading: "What a COI shows",
        paragraphs: [
          "A typical COI lists the insured business, its insurer(s), the kinds of coverage in force (general liability, auto, umbrella, workers' compensation), the dollar limits on each, and the dates the coverage starts and ends. It also names a certificate holder — the business that asked for the proof.",
          "In the United States this information almost always arrives on a standardized ACORD form, which is why one COI looks much like the next.",
        ],
      },
      {
        heading: "Why businesses ask for one",
        paragraphs: [
          "If you hire vendors, lease space, or let contractors onto a property, a COI is how you confirm they carry enough insurance to cover the risk they bring. If something goes wrong and they are not properly covered, the liability can land on you.",
        ],
      },
      {
        heading: "The catch: a COI expires",
        paragraphs: [
          "A certificate is only true on the day it is issued. Policies get cancelled, lapse for non-payment, or renew with different limits — and the certificate you filed last year does not update itself. That is why collecting COIs once is not enough; you have to track expiration dates and re-collect them, which is exactly the job COI tracking software exists to automate.",
        ],
      },
    ],
    related: ["acord-25", "additional-insured", "certificate-holder"],
  },
  {
    slug: "acord-25",
    term: "ACORD 25",
    title: "What is an ACORD 25 form?",
    definition:
      "ACORD 25 is the standard Certificate of Liability Insurance form used across the U.S. insurance industry. When someone asks for \"a COI,\" they almost always mean an ACORD 25 — it lays out general liability, auto, umbrella, and workers' compensation coverage in one fixed, familiar format.",
    sections: [
      {
        heading: "What's on the form",
        paragraphs: [
          "The ACORD 25 has a row for each type of liability coverage, the policy number and dates, and the limits for each. A box at the bottom names the certificate holder, and the \"Description of Operations\" field is where brokers note things like additional insured status or a waiver of subrogation.",
        ],
      },
      {
        heading: "ACORD 25 vs. other ACORD forms",
        paragraphs: [
          "ACORD 25 covers liability. Property coverage shows up on different forms — ACORD 27 and ACORD 28 — which a landlord or lender may request instead. If you are tracking vendors, the ACORD 25 is the one you will see most.",
        ],
      },
      {
        heading: "Why the standard format helps",
        paragraphs: [
          "Because every ACORD 25 puts the same data in the same place, software can read it reliably — pulling the limits, dates, and coverage types automatically instead of making you key them in by hand.",
        ],
      },
    ],
    related: ["certificate-of-insurance", "additional-insured-vs-certificate-holder"],
  },
  {
    slug: "additional-insured-vs-certificate-holder",
    term: "Additional Insured vs. Certificate Holder",
    title: "Additional insured vs. certificate holder: what's the difference?",
    definition:
      "A certificate holder simply receives a copy of the certificate of insurance. An additional insured is actually covered under the vendor's policy. The difference is the whole game: only an additional insured can be defended and paid under that policy — being listed only as a certificate holder gives you no coverage at all.",
    sections: [
      {
        heading: "Why people mix them up",
        paragraphs: [
          "Both names appear on the same certificate, so it is easy to assume that being \"on the COI\" means you are protected. You are not. The certificate holder box is just a mailing label; it confirms you were sent the document, nothing more.",
        ],
      },
      {
        heading: "How to actually get covered",
        paragraphs: [
          "To be an additional insured, the vendor's insurer has to add you to the policy with an endorsement — commonly CG 20 10 (for ongoing work) and CG 20 37 (for completed work) on a general liability policy. The certificate may say \"additional insured,\" but the coverage only truly exists if that endorsement is on the policy.",
        ],
      },
      {
        heading: "What to check",
        paragraphs: [
          "Confirm your business is named as an additional insured (not just the certificate holder), and ask for a copy of the endorsement when the stakes are high. Tracking software can flag certificates that list you only as a certificate holder so the gap does not slip through.",
        ],
      },
    ],
    related: ["additional-insured", "certificate-holder", "waiver-of-subrogation"],
  },
  {
    slug: "additional-insured",
    term: "Additional Insured",
    title: "What does it mean to be an additional insured?",
    definition:
      "An additional insured is a person or business added to someone else's insurance policy so they are covered by it. If a contractor names your company as an additional insured, their general liability policy can defend and pay claims that arise out of the contractor's work — shifting that risk off your own insurance.",
    sections: [
      {
        heading: "Why it matters",
        paragraphs: [
          "When a vendor's work causes an injury or damage, you can be pulled into the claim too. Being an additional insured on the vendor's policy means their insurer steps in for you, rather than you turning to your own coverage and risking a premium increase.",
        ],
      },
      {
        heading: "How someone becomes an additional insured",
        paragraphs: [
          "It takes a policy endorsement from the vendor's insurer — it is not automatic just because a contract requires it. The certificate of insurance should reflect the endorsement, and for higher-risk work it is worth requesting the endorsement document itself.",
        ],
      },
    ],
    related: ["additional-insured-vs-certificate-holder", "certificate-holder", "certificate-of-insurance"],
  },
  {
    slug: "certificate-holder",
    term: "Certificate Holder",
    title: "What is a certificate holder on a COI?",
    definition:
      "The certificate holder is the person or business that receives a certificate of insurance. Being the certificate holder means you are sent proof of the policy and, often, notice if it changes — but it does NOT mean you are covered by it. For coverage, you have to be named as an additional insured.",
    sections: [
      {
        heading: "The common misconception",
        paragraphs: [
          "Many business owners assume that requiring a vendor to list them as certificate holder protects them. It does not. The certificate holder box is essentially an address — it documents who requested the certificate and where notices go.",
        ],
      },
      {
        heading: "Certificate holder vs. additional insured",
        paragraphs: [
          "If you need the vendor's policy to actually respond on your behalf, you need additional insured status, which is a separate endorsement. Knowing the difference is the single most valuable thing a small business can learn about tracking vendor insurance.",
        ],
      },
    ],
    related: ["additional-insured-vs-certificate-holder", "additional-insured", "certificate-of-insurance"],
  },
  {
    slug: "waiver-of-subrogation",
    term: "Waiver of Subrogation",
    title: "What is a waiver of subrogation?",
    definition:
      "A waiver of subrogation is a clause that stops an insurer from coming after a third party to recover money it paid on a claim. In vendor and lease agreements, it means the vendor's insurer cannot turn around and sue you to recoup a payout — a common requirement in contracts and leases.",
    sections: [
      {
        heading: "What subrogation is",
        paragraphs: [
          "Subrogation is an insurer's right to \"step into the shoes\" of the party it paid and pursue whoever was actually at fault. A waiver gives up that right against a named party — usually you.",
        ],
      },
      {
        heading: "When it's required and how it shows up",
        paragraphs: [
          "Contracts often require the vendor to carry a waiver of subrogation in your favor. It is noted on the certificate of insurance (typically in the description field) and backed by an endorsement on the policy, the same way additional insured status is.",
        ],
      },
    ],
    related: ["additional-insured", "certificate-of-insurance"],
  },
];

/** Map for O(1) slug lookups in the dynamic route. */
const BY_SLUG = new Map(GLOSSARY_TERMS.map((t) => [t.slug, t]));

export function getGlossaryTerm(slug: string): GlossaryTerm | undefined {
  return BY_SLUG.get(slug);
}
