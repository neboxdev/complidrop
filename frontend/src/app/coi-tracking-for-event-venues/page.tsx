import type { Metadata } from "next";
import Link from "next/link";
import { Check } from "lucide-react";
import { MarketingHeader } from "@/components/marketing/site-header";
import { MarketingFooter } from "@/components/marketing/site-footer";
import { Breadcrumbs, ContentCta, ContentH1, Lead } from "@/components/marketing/content";
import { JsonLd } from "@/components/JsonLd";
import { breadcrumbLd, faqPageLd, type FaqItem } from "@/lib/structured-data";
import { pageMetadata } from "@/lib/seo";

export const metadata: Metadata = pageMetadata({
  title: "COI Tracking for Event Venues",
  description:
    "Every caterer, DJ, and rental vendor needs a current certificate of insurance before the event. CompliDrop collects and checks vendor COIs so a missing certificate never holds up a booking.",
  path: "/coi-tracking-for-event-venues",
});

// Common venue requirements. Phrased as what venues "typically" require — this
// is practical guidance, not legal advice, and exact requirements vary by
// venue, state, and event.
interface Requirement {
  label: string;
  detail: string;
  /** Optional cross-link to a glossary term for deeper explanation. */
  href?: string;
}

const REQUIREMENTS: readonly Requirement[] = [
  {
    label: "General liability coverage",
    detail: "Commonly $1,000,000 per occurrence and $2,000,000 aggregate.",
  },
  {
    label: "Your venue named as an additional insured",
    detail: "Not just the certificate holder — that distinction decides whether the vendor's policy actually covers you.",
    href: "/glossary/additional-insured-vs-certificate-holder",
  },
  {
    label: "Liquor liability",
    detail: "For any vendor serving or selling alcohol at the event.",
  },
  {
    label: "Workers' compensation",
    detail: "For vendors that bring their own staff, where required by your state.",
  },
  {
    label: "Coverage dates that include the event",
    detail: "A policy that expires the week before the wedding is the same as no policy at all.",
  },
];

const FAQ_ITEMS: readonly FaqItem[] = [
  {
    question: "What insurance should an event venue require from vendors?",
    answer:
      "Most venues require each vendor to carry general liability coverage (commonly $1M per occurrence / $2M aggregate), to name the venue as an additional insured, and to add liquor liability if they serve alcohol. Vendors with employees are usually asked for workers' compensation. The exact requirements vary, so confirm them with your own insurer or attorney.",
  },
  {
    question: "Do vendors need to name my venue as an additional insured?",
    answer:
      "Yes, if you want the vendor's policy to actually protect your venue. Being listed only as the certificate holder means you receive the certificate but aren't covered by it. Additional insured status — added by an endorsement on the vendor's policy — is what lets their insurer defend and pay a claim on your behalf.",
  },
  {
    question: "How do I collect certificates from every vendor before an event?",
    answer:
      "CompliDrop gives each vendor a private, no-login upload link. They send their certificate of insurance through it, CompliDrop reads and checks it against your requirements, and you see at a glance who's cleared and who still owes you a document — without chasing anyone by email.",
  },
];

export default function EventVenuesPage() {
  return (
    <>
      <JsonLd
        data={[
          faqPageLd(FAQ_ITEMS),
          breadcrumbLd([
            { name: "Home", path: "/" },
            { name: "COI tracking for event venues", path: "/coi-tracking-for-event-venues" },
          ]),
        ]}
      />
      <MarketingHeader />

      <main className="bg-white">
        <div className="mx-auto max-w-3xl px-4 py-14 sm:px-6 sm:py-20">
          <Breadcrumbs items={[{ name: "Home", href: "/" }, { name: "Event venues" }]} />

          <p className="mt-5 text-xs font-bold uppercase tracking-[0.2em] text-primary">
            For event venues
          </p>
          <ContentH1>COI tracking for event venues</ContentH1>
          <Lead>
            Every caterer, DJ, florist, and rental vendor that sets foot in your
            venue should carry a current certificate of insurance &mdash; with
            the right limits and your venue named as additional insured &mdash;
            before the event. CompliDrop collects those certificates, checks
            them, and reminds the stragglers, so a missing COI is never the
            reason a booking falls apart.
          </Lead>

          <section>
            <h2 className="mt-12 text-2xl font-bold tracking-tight text-foreground">
              Why a venue needs a COI from every vendor
            </h2>
            <p className="mt-4 text-base leading-relaxed text-muted-foreground">
              When a guest trips over a caterer&rsquo;s cabling or a bounce house
              tips over, the claim doesn&rsquo;t stop at the vendor &mdash; it
              reaches the venue. A certificate of insurance proves the vendor
              carries enough coverage to stand behind their own work, and naming
              your venue as an additional insured means their policy, not yours,
              responds first. Collect it up front and a bad night stays the
              vendor&rsquo;s problem instead of becoming yours.
            </p>
          </section>

          <section>
            <h2 className="mt-10 text-2xl font-bold tracking-tight text-foreground">
              What to require on a vendor&rsquo;s COI
            </h2>
            <p className="mt-4 text-base leading-relaxed text-muted-foreground">
              Requirements vary by venue and event, but most venues ask vendors
              for the following. (This is practical guidance, not legal advice
              &mdash; confirm your specifics with your insurer.)
            </p>
            <ul className="mt-6 space-y-4">
              {REQUIREMENTS.map((req) => (
                <li key={req.label} className="flex gap-3">
                  <Check aria-hidden="true" className="mt-1 size-5 shrink-0 text-primary" />
                  <span className="text-base leading-relaxed text-muted-foreground">
                    <span className="font-semibold text-foreground">{req.label}.</span>{" "}
                    {req.href ? (
                      <>
                        {req.detail}{" "}
                        <Link href={req.href} className="font-medium text-primary hover:underline">
                          Why that distinction matters&nbsp;&rarr;
                        </Link>
                      </>
                    ) : (
                      req.detail
                    )}
                  </span>
                </li>
              ))}
            </ul>
          </section>

          <section>
            <h2 className="mt-10 text-2xl font-bold tracking-tight text-foreground">
              How CompliDrop works for venues
            </h2>
            <p className="mt-4 text-base leading-relaxed text-muted-foreground">
              Send each vendor a no-login upload link. They drop in their
              certificate; CompliDrop reads the dates, limits, and coverage and
              checks them against your venue&rsquo;s requirements &mdash; flagging
              anyone who&rsquo;s short or who listed you as certificate holder
              instead of additional insured. Reminders go out automatically
              before a certificate expires, so by the day of the event
              you&rsquo;re looking at a clean list, not a phone in your hand.
            </p>
          </section>

          <section className="mt-12">
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

          <ContentCta
            heading="Never chase a vendor COI the week of an event again."
            body="CompliDrop collects, checks, and renews your vendors' certificates of insurance automatically. Free for your first 5 documents — no credit card."
          />
        </div>
      </main>

      <MarketingFooter />
    </>
  );
}
