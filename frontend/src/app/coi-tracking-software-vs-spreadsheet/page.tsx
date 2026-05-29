import type { Metadata } from "next";
import { Check, X } from "lucide-react";
import { MarketingHeader } from "@/components/marketing/site-header";
import { MarketingFooter } from "@/components/marketing/site-footer";
import { Breadcrumbs, ContentCta, ContentH1, Lead } from "@/components/marketing/content";
import { JsonLd } from "@/components/JsonLd";
import { breadcrumbLd, faqPageLd, type FaqItem } from "@/lib/structured-data";
import { pageMetadata } from "@/lib/seo";
import { PLANS } from "@/lib/plans";

export const metadata: Metadata = pageMetadata({
  title: "COI Tracking Software vs. a Spreadsheet",
  description:
    "Most small businesses track certificates of insurance in a spreadsheet. Here's exactly where that breaks — and what COI tracking software like CompliDrop does instead.",
  path: "/coi-tracking-software-vs-spreadsheet",
});

interface ComparisonRow {
  capability: string;
  spreadsheet: string;
  complidrop: string;
}

const ROWS: readonly ComparisonRow[] = [
  {
    capability: "Reads the certificate for you",
    spreadsheet: "No — you type every field by hand",
    complidrop: "Yes — pulls dates, limits, and coverage automatically",
  },
  {
    capability: "Knows when a policy expires",
    spreadsheet: "Only if you remember to look",
    complidrop: "Tracks every expiration date for you",
  },
  {
    capability: "Sends renewal reminders",
    spreadsheet: "No — manual calendar entries",
    complidrop: "Automatic, to you and the vendor",
  },
  {
    capability: "Lets vendors upload their own docs",
    spreadsheet: "No — email back-and-forth",
    complidrop: "Yes — a no-login upload link",
  },
  {
    capability: "Flags coverage that's too low",
    spreadsheet: "You compare limits by eye",
    complidrop: "A rules engine flags the gaps",
  },
  {
    capability: "Audit-ready export",
    spreadsheet: "Assemble it by hand",
    complidrop: "One click (Pro)",
  },
  {
    capability: "Cost",
    spreadsheet: "“Free” — plus the hours you spend on it",
    complidrop: `Free up to 5 docs, then ${PLANS.pro.monthlyPriceLabel}/mo`,
  },
];

const FAQ_ITEMS: readonly FaqItem[] = [
  {
    question: "Is a spreadsheet enough to track certificates of insurance?",
    answer:
      "For two or three vendors that never change, maybe. The trouble is that a spreadsheet is passive: it can't read a certificate, it doesn't know a policy lapsed, and it never reminds anyone. The moment you're tracking a dozen vendors with different renewal dates, the gaps it can't catch are exactly the ones that cause a shutdown or a coverage claim.",
  },
  {
    question: "How much does CompliDrop cost compared to a spreadsheet?",
    answer: `A spreadsheet is free in dollars but costs you hours of manual entry and chasing. CompliDrop is free for your first 5 documents, then ${PLANS.pro.monthlyPriceLabel}/month for unlimited documents with automatic extraction, reminders, the vendor portal, and audit exports — far less than one missed expiration can cost.`,
  },
];

export default function CoiVsSpreadsheetPage() {
  return (
    <>
      <JsonLd
        data={[
          faqPageLd(FAQ_ITEMS),
          breadcrumbLd([
            { name: "Home", path: "/" },
            { name: "CompliDrop vs. a spreadsheet", path: "/coi-tracking-software-vs-spreadsheet" },
          ]),
        ]}
      />
      <MarketingHeader />

      <main className="bg-white">
        <div className="mx-auto max-w-4xl px-4 py-14 sm:px-6 sm:py-20">
          <Breadcrumbs items={[{ name: "Home", href: "/" }, { name: "vs. a spreadsheet" }]} />

          <ContentH1>COI tracking software vs. a spreadsheet</ContentH1>
          <Lead>
            A spreadsheet records what you type. COI tracking software reads the
            certificate, checks the coverage against your requirements, and
            chases the renewal for you. Here&rsquo;s exactly where the
            spreadsheet breaks &mdash; and what changes when software does the
            watching.
          </Lead>

          {/* Comparison table — kept as a real <table> so it's accessible and
              easy for search engines and AI to extract. */}
          <div className="mt-12 overflow-x-auto">
            <table className="w-full border-collapse text-left text-sm">
              <caption className="sr-only">
                Tracking certificates of insurance in a spreadsheet versus with CompliDrop
              </caption>
              <thead>
                <tr className="border-b border-border">
                  <th scope="col" className="py-3 pr-4 font-semibold text-foreground">
                    Capability
                  </th>
                  <th scope="col" className="py-3 pr-4 font-semibold text-foreground">
                    Spreadsheet
                  </th>
                  <th scope="col" className="py-3 font-semibold text-foreground">
                    CompliDrop
                  </th>
                </tr>
              </thead>
              <tbody>
                {ROWS.map((row) => (
                  <tr key={row.capability} className="border-b border-border align-top">
                    <th scope="row" className="py-4 pr-4 font-medium text-foreground">
                      {row.capability}
                    </th>
                    <td className="py-4 pr-4 text-muted-foreground">
                      <span className="flex gap-2">
                        <X aria-hidden="true" className="mt-0.5 size-4 shrink-0 text-muted-foreground/60" />
                        <span>{row.spreadsheet}</span>
                      </span>
                    </td>
                    <td className="py-4 text-foreground">
                      <span className="flex gap-2">
                        <Check aria-hidden="true" className="mt-0.5 size-4 shrink-0 text-primary" />
                        <span>{row.complidrop}</span>
                      </span>
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>

          <section>
            <h2 className="mt-14 text-2xl font-bold tracking-tight text-foreground">
              When a spreadsheet is fine
            </h2>
            <p className="mt-4 text-base leading-relaxed text-muted-foreground">
              If you track a couple of vendors whose policies never change, a
              spreadsheet works. We&rsquo;d rather tell you that than sell you
              something you don&rsquo;t need. The problem isn&rsquo;t the
              spreadsheet itself &mdash; it&rsquo;s what the spreadsheet
              can&rsquo;t do as the list grows.
            </p>
          </section>

          <section>
            <h2 className="mt-10 text-2xl font-bold tracking-tight text-foreground">
              Where it breaks
            </h2>
            <p className="mt-4 text-base leading-relaxed text-muted-foreground">
              A spreadsheet only knows what you remembered to type. It can&rsquo;t
              read a certificate, so a fat-fingered expiration date sits there
              looking correct. It can&rsquo;t tell that a vendor&rsquo;s policy
              was cancelled last month. And it never emails anyone &mdash; so the
              renewal you meant to chase in March is still missing in June, the
              day you actually need the coverage.
            </p>
          </section>

          <section>
            <h2 className="mt-10 text-2xl font-bold tracking-tight text-foreground">
              What CompliDrop does instead
            </h2>
            <p className="mt-4 text-base leading-relaxed text-muted-foreground">
              You upload a certificate of insurance, license, or permit and
              CompliDrop reads it &mdash; the names, dates, coverage types, and
              limits &mdash; then checks them against the requirements you set.
              It flags anything short, reminds you and the vendor before
              expiration, and gives the vendor a no-login link to send the new
              document themselves. The watching stops being your job.
            </p>
          </section>

          <section className="mt-14">
            <h2 className="text-2xl font-bold tracking-tight text-foreground">Common questions</h2>
            <div className="mt-6 space-y-8">
              {FAQ_ITEMS.map((item) => (
                <div key={item.question}>
                  <h3 className="text-lg font-semibold text-foreground">{item.question}</h3>
                  <p className="mt-3 text-base leading-relaxed text-muted-foreground">{item.answer}</p>
                </div>
              ))}
            </div>
          </section>

          <ContentCta />
        </div>
      </main>

      <MarketingFooter />
    </>
  );
}
