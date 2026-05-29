import type { Metadata } from "next";
import Link from "next/link";
import { ArticleShell, Breadcrumbs, ContentCta, ContentH1, Lead } from "@/components/marketing/content";
import { JsonLd } from "@/components/JsonLd";
import { breadcrumbLd, faqPageLd, type FaqItem } from "@/lib/structured-data";
import { pageMetadata } from "@/lib/seo";
import { PLANS } from "@/lib/plans";

export const metadata: Metadata = pageMetadata({
  title: "Frequently Asked Questions",
  description:
    "Answers to common questions about CompliDrop — what COI tracking software does, what it costs, how it's different from a spreadsheet, and how vendor uploads and reminders work.",
  path: "/faq",
});

// One array drives both the visible Q&A and the FAQPage structured data, so the
// rendered text and the schema match exactly (a Google requirement, and the
// format AI assistants quote most). Prices come from `lib/plans.ts` so the
// answers can never disagree with the pricing cards.
const FAQ_ITEMS: readonly FaqItem[] = [
  {
    question: "What is CompliDrop?",
    answer:
      "CompliDrop is COI tracking software for small businesses. You upload a certificate of insurance, license, or permit; it reads the dates and coverage, checks them against your requirements, and reminds you — and your vendor — before anything expires. It replaces the spreadsheet and the calendar reminders you're keeping by hand.",
  },
  {
    question: "What does it cost?",
    answer: `CompliDrop is free for your first 5 documents, with full automatic extraction and email reminders. Pro is ${PLANS.pro.monthlyPriceLabel}/month for unlimited documents, the vendor upload portal, the compliance rules engine, and audit-ready exports. Annual is ${PLANS.annual.monthlyPriceLabel}/month billed yearly. No contracts, no minimums, and you can cancel anytime.`,
  },
  {
    question: "How is this different from tracking COIs in a spreadsheet?",
    answer:
      "A spreadsheet stores what you type; it doesn't read the document, it doesn't know a policy expired, and it never chases the vendor for a renewal. CompliDrop extracts the data for you, flags coverage that falls short of your requirements, and sends the reminders automatically — so a missed expiration can't slip through a row you forgot to check.",
  },
  {
    question: "Do my vendors need an account to send their documents?",
    answer:
      "No. Each vendor gets a private upload link — no login, no password, no account to create. They open the link, drop in their certificate, and you see it in your dashboard. Removing that friction is the difference between getting the updated document and chasing it for two weeks.",
  },
  {
    question: "What kinds of documents can CompliDrop read?",
    answer:
      "Certificates of insurance (including the standard ACORD 25 form), business licenses, and permits — uploaded as a PDF, a photo, or a phone scan. CompliDrop pulls out the names, effective and expiration dates, coverage types, and limits automatically.",
  },
  {
    question: "What if the automatic reading gets something wrong?",
    answer:
      "You always review and confirm what CompliDrop extracted before it counts — it's a head start you approve, not a black box you have to trust blindly. When a document is unclear, CompliDrop flags it for your review rather than guessing silently.",
  },
  {
    question: "Can I get an audit-ready record of who's compliant?",
    answer:
      "Yes. On the Pro plan you can export an audit-ready report of your documents, their coverage, and their status — the record an insurer, auditor, or client asks for, without you assembling it by hand.",
  },
  {
    question: "Is my data secure?",
    answer:
      "Your documents are transmitted over an encrypted connection and stored privately. Each account's data is isolated from every other account, and every change is written to an audit log. We don't sell or share your data.",
  },
];

// Topical cross-links rendered separately from the answer text (so the schema
// answer text stays an exact match of the visible answer).
const LEARN_MORE = [
  { href: "/coi-tracking-software-vs-spreadsheet", label: "CompliDrop vs. a spreadsheet" },
  { href: "/glossary/certificate-of-insurance", label: "What is a certificate of insurance?" },
  { href: "/coi-tracking-for-event-venues", label: "COI tracking for event venues" },
] as const;

export default function FaqPage() {
  return (
    <ArticleShell>
      <JsonLd
        data={[
          faqPageLd(FAQ_ITEMS),
          breadcrumbLd([
            { name: "Home", path: "/" },
            { name: "FAQ", path: "/faq" },
          ]),
        ]}
      />

      <Breadcrumbs items={[{ name: "Home", href: "/" }, { name: "FAQ" }]} />

      <ContentH1>Frequently asked questions</ContentH1>
      <Lead>
        Everything a small business usually wants to know about tracking
        certificates of insurance, licenses, and permits with CompliDrop.
      </Lead>

      <div className="mt-12 space-y-10">
        {FAQ_ITEMS.map((item) => (
          <section key={item.question} className="border-t border-border pt-8">
            <h2 className="text-xl font-semibold text-foreground">{item.question}</h2>
            <p className="mt-3 text-base leading-relaxed text-muted-foreground">{item.answer}</p>
          </section>
        ))}
      </div>

      <section className="mt-12 border-t border-border pt-8">
        <h2 className="text-sm font-bold uppercase tracking-[0.15em] text-muted-foreground">
          Keep reading
        </h2>
        <ul className="mt-4 space-y-2">
          {LEARN_MORE.map((link) => (
            <li key={link.href}>
              <Link href={link.href} className="font-medium text-primary hover:underline">
                {link.label}
              </Link>
            </li>
          ))}
        </ul>
      </section>

      <ContentCta />
    </ArticleShell>
  );
}
