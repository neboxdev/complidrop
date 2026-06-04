import type { Metadata } from "next";
import Link from "next/link";
import { buttonVariants } from "@/components/ui/button";
import { Badge } from "@/components/ui/badge";
import { Card, CardContent } from "@/components/ui/card";
import { Logo } from "@/components/Logo";
import { MarketingHeader } from "@/components/marketing/site-header";
import { MarketingFooter } from "@/components/marketing/site-footer";
import { JsonLd } from "@/components/JsonLd";
import { softwareApplicationLd } from "@/lib/structured-data";
import { pageMetadata } from "@/lib/seo";
import { SITE_DESCRIPTION } from "@/lib/site";
import { cn } from "@/lib/utils";
import { PLANS } from "@/lib/plans";
import {
  Upload,
  ShieldCheck,
  BellRing,
  Clock,
  DollarSign,
  FileWarning,
  ArrowRight,
  Check,
  Quote,
} from "lucide-react";

// Keyword-first <title> ("COI Tracking Software for Small Business | CompliDrop")
// — that's the line searchers see in results. The on-page H1 keeps the brand
// voice; the keyword saturates the title, eyebrow, subhead, and section H2s.
export const metadata: Metadata = pageMetadata({
  title: "COI Tracking Software for Small Business",
  description: SITE_DESCRIPTION,
  path: "/",
});

/* ── Shared CTA classes (CSS-only hover → works in a server component) ─────── */
const heroPrimaryCta = cn(
  buttonVariants({ size: "lg" }),
  "h-13 cursor-pointer rounded-xl bg-accent px-8 text-base font-semibold text-accent-foreground shadow-lg transition-all duration-200 hover:bg-accent/90 hover:shadow-xl",
);
const heroOutlineCta = cn(
  buttonVariants({ variant: "outline", size: "lg" }),
  "h-13 cursor-pointer rounded-xl border-2 border-primary px-8 text-base font-semibold text-primary transition-all duration-200 hover:bg-primary/5",
);

/* ── Section eyebrow label ─────────────────────────────────────────────────── */
function SectionLabel({ children }: { children: React.ReactNode }) {
  return (
    <p className="text-center text-xs font-bold uppercase tracking-[0.2em] text-primary">
      {children}
    </p>
  );
}

/* ── Product preview ────────────────────────────────────────────────────────
   A coded mock of the document-detail view (extracted fields + a green
   Compliant badge + an expiry countdown) so a cold-email visitor SEES the
   product in the hero. Built in markup rather than a binary screenshot so it
   stays crisp, responsive, theme-synced, and never goes stale. (#195) */
const PREVIEW_FIELDS = [
  { label: "Policyholder", value: "Acme Catering LLC" },
  { label: "Policy number", value: "GL-4471902" },
  { label: "General liability", value: "$2,000,000" },
  { label: "Expiration date", value: "Mar 14, 2027" },
] as const;

function ProductPreview() {
  return (
    <div className="relative mx-auto mt-16 max-w-3xl">
      <div className="overflow-hidden rounded-2xl border border-border bg-white text-left shadow-2xl">
        {/* window chrome */}
        <div className="flex items-center gap-1.5 border-b border-border bg-slate-50 px-4 py-3">
          <span className="h-3 w-3 rounded-full bg-rose-300" aria-hidden />
          <span className="h-3 w-3 rounded-full bg-amber-300" aria-hidden />
          <span className="h-3 w-3 rounded-full bg-emerald-300" aria-hidden />
          <span className="ml-3 truncate text-xs font-medium text-muted-foreground">
            Acme-Catering-COI.pdf
          </span>
        </div>
        <div className="p-6 sm:p-8">
          <div className="flex flex-wrap items-center justify-between gap-3">
            <div>
              <p className="text-xs font-medium uppercase tracking-wide text-muted-foreground">
                Certificate of insurance
              </p>
              <p className="mt-1 text-lg font-semibold text-foreground">Acme Catering LLC</p>
            </div>
            <div className="flex flex-wrap items-center gap-2.5">
              <Badge className="inline-flex items-center gap-1 border-transparent bg-emerald-100 text-emerald-700">
                <ShieldCheck className="h-3.5 w-3.5" aria-hidden /> Compliant
              </Badge>
              <span className="inline-flex items-center gap-1 rounded-full bg-amber-50 px-2.5 py-1 text-xs font-medium text-amber-700">
                <Clock className="h-3.5 w-3.5" aria-hidden /> Expires in 23 days
              </span>
            </div>
          </div>
          <div className="mt-6 grid grid-cols-1 gap-3 sm:grid-cols-2">
            {PREVIEW_FIELDS.map((f) => (
              <div key={f.label} className="rounded-lg border border-border/70 bg-slate-50/60 p-3">
                <p className="text-xs font-medium uppercase tracking-wide text-muted-foreground">
                  {f.label}
                </p>
                <p className="mt-0.5 flex items-center gap-1.5 text-sm font-medium text-foreground">
                  <Check className="h-3.5 w-3.5 shrink-0 text-emerald-600" aria-hidden />
                  {f.value}
                </p>
              </div>
            ))}
          </div>
        </div>
      </div>
      <p className="mt-3 text-center text-xs text-muted-foreground">
        A real certificate, read and checked in seconds — names, dates, and limits pulled
        out for you.
      </p>
    </div>
  );
}

/* ── Social proof ───────────────────────────────────────────────────────────
   Honest pre-launch framing — a founder's promise, NOT a fabricated customer
   quote (faking a named testimonial would be deceptive). Fills the silence a
   skeptical buyer notices, and invites the first real customers. Swap in real
   named testimonials once we have them. (#195) */
function SocialProof() {
  return (
    <section className="bg-white py-20 sm:py-28">
      <div className="mx-auto max-w-4xl px-4 text-center sm:px-6">
        <SectionLabel>Why teams switch</SectionLabel>
        <h2 className="mt-3 text-3xl font-bold tracking-tight text-foreground sm:text-4xl">
          Built by people who&rsquo;ve chased a certificate at 4&nbsp;a.m.
        </h2>
        <figure className="mx-auto mt-10 max-w-2xl rounded-2xl border border-border bg-secondary/40 p-8 sm:p-10">
          <Quote className="mx-auto h-7 w-7 text-primary" aria-hidden />
          <blockquote className="mt-4 text-lg leading-relaxed text-foreground">
            &ldquo;Every vendor folder we&rsquo;ve seen is a spreadsheet held together by
            sticky notes and dread. We built CompliDrop to be the tool we wished we&rsquo;d
            had&nbsp;&mdash; read the document, check the coverage, chase the renewal, so
            you don&rsquo;t have to.&rdquo;
          </blockquote>
          <figcaption className="mt-5 text-sm font-medium text-muted-foreground">
            &mdash; The CompliDrop team
          </figcaption>
        </figure>
        <p className="mt-6 text-sm text-muted-foreground">
          We&rsquo;re just getting started.{" "}
          <Link href="/register" className="font-semibold text-primary hover:underline">
            Be one of our first customers
          </Link>{" "}
          &mdash; your story could be here.
        </p>
      </div>
    </section>
  );
}

/* ── Page data ─────────────────────────────────────────────────────────────── */
const PROBLEM_CARDS = [
  {
    icon: Clock,
    title: "The 4 a.m. spreadsheet panic",
    body: "You've got 30 subs and a folder full of certificates that all expire on different dates. Miss one row and a crew can't get on the job site Monday — and you're the one making phone calls at dawn.",
  },
  {
    icon: DollarSign,
    title: "Software that costs more than your truck payment",
    body: "Enterprise compliance tools want $10,000 a year, a two-year contract, and a month of onboarding. You just need to know if Tony's general liability policy is still current. You shouldn't need a procurement department to find out.",
  },
  {
    icon: FileWarning,
    title: "“Automation” you can't trust",
    body: "You tried another tool. It pulled the wrong expiration date, tagged the wrong coverage, and you re-checked every field by hand anyway. A document reader you have to double-check isn't saving you anything.",
  },
] as const;

const STEPS = [
  {
    step: "1",
    icon: Upload,
    title: "Drop it",
    body: "Upload a certificate of insurance, license, or permit — PDF, photo, or a scan from your phone. CompliDrop reads the document and pulls out the names, dates, coverage types, and limits for you. No templates, no reformatting.",
  },
  {
    step: "2",
    icon: ShieldCheck,
    title: "Check it",
    body: "CompliDrop matches what it found against your requirements. Need every sub to carry $1M in general liability and name you as additional insured? It flags the gaps before you go looking. Review, confirm with one click, done.",
  },
  {
    step: "3",
    icon: BellRing,
    title: "Forget it",
    body: "Reminders go out at 60, 30, and 7 days before expiration — to you and straight to your vendor. They get a simple upload link (no login, no account) and send the new document themselves. You stop being the bad guy.",
  },
] as const;

interface AudienceCard {
  title: string;
  body: string;
  /** Optional link to a dedicated use-case page. */
  href?: string;
}

const AUDIENCE_CARDS: readonly AudienceCard[] = [
  {
    title: "Event venues",
    body: "Every caterer, DJ, and rental vendor needs a current COI — with the right limits and your venue named as additional insured — before the event. CompliDrop collects and checks them so you're never the reason a booking falls through.",
    href: "/coi-tracking-for-event-venues",
  },
  {
    title: "Property management",
    body: "Vendor insurance, tenant certs, lease dates — one coverage gap on a property and you're holding the liability. Stop cross-referencing three spreadsheets to find out who's current.",
  },
  {
    title: "Construction & contractors",
    body: "Juggling COIs for 40 subs, and one expired policy means a crew sitting in the parking lot instead of on the job site. CompliDrop tracks every sub so Monday morning isn't a fire drill.",
  },
  {
    title: "Healthcare practices",
    body: "DEA renewals, state licenses, hospital privileges, malpractice certs — all expiring on different dates across every provider. One lapse and a clinician can't see patients until it's fixed.",
  },
  {
    title: "Transportation & trucking",
    body: "DOT medical cards, CDLs, vehicle inspections, cargo insurance — every driver is a stack of documents with a deadline. Miss one and that truck doesn't move.",
  },
  {
    title: "Professional services",
    body: "Liability insurance, professional certifications, signed agreements — your clients expect proof on demand. “Let me dig through my files” isn't the answer they're looking for.",
  },
];

/* ── Page ──────────────────────────────────────────────────────────────────── */
export default function Home() {
  return (
    <>
      <JsonLd data={softwareApplicationLd()} />
      <MarketingHeader />

      <main>
        {/* ── Hero ──────────────────────────────────────────────── */}
        <section className="relative overflow-hidden bg-gradient-to-br from-secondary via-white to-secondary">
          {/* Decorative blobs */}
          <div className="pointer-events-none absolute -left-40 -top-40 h-[500px] w-[500px] rounded-full bg-[#38BDF8] opacity-20 blur-3xl" />
          <div className="pointer-events-none absolute -right-40 top-20 h-[400px] w-[400px] rounded-full bg-accent opacity-15 blur-3xl" />

          <div className="relative mx-auto max-w-4xl px-4 py-24 text-center sm:px-6 sm:py-36">
            <div className="mb-8 flex justify-center sm:mb-10">
              <Logo variant="twotone" height={48} />
            </div>

            <p className="text-xs font-bold uppercase tracking-[0.2em] text-primary">
              COI, license &amp; permit tracking
            </p>

            <h1 className="mt-4 text-4xl font-extrabold leading-[1.1] tracking-tight text-foreground sm:text-5xl lg:text-6xl">
              Stop chasing certificates of insurance.
              <br />
              <span className="text-primary">Start dropping docs.</span>
            </h1>

            <p className="mx-auto mt-6 max-w-2xl text-lg leading-relaxed text-muted-foreground sm:text-xl">
              CompliDrop is COI tracking software for small businesses. Upload a
              certificate of insurance, license, or permit and it reads the
              dates, checks the coverage, and warns you&nbsp;&mdash; and your
              vendor&nbsp;&mdash; before anything expires. {PLANS.pro.monthlyPriceLabel}/month. No demo,
              no contract.
            </p>

            <div className="mt-10 flex flex-col items-center gap-4 sm:flex-row sm:justify-center">
              <Link href="/register" className={heroPrimaryCta}>
                Get started free
                <ArrowRight className="ml-2 size-4" />
              </Link>
              <a href="#how-it-works" className={heroOutlineCta}>
                See how it works&nbsp;↓
              </a>
            </div>
            <p className="mt-5 text-sm text-muted-foreground">
              Free for your first 5 documents. No credit card.
            </p>

            <ProductPreview />
          </div>
        </section>

        {/* ── Problem ───────────────────────────────────────────── */}
        <section className="bg-white py-24 sm:py-32">
          <div className="mx-auto max-w-6xl px-4 sm:px-6">
            <SectionLabel>The problem</SectionLabel>
            <h2 className="mt-3 text-center text-3xl font-bold tracking-tight text-foreground sm:text-4xl">
              Your spreadsheet is a ticking time bomb.
            </h2>
            <p className="mx-auto mt-4 max-w-2xl text-center text-lg text-muted-foreground">
              You&rsquo;re tracking dozens of vendors, hundreds of documents, and
              one missed expiration away from a shutdown. Sound familiar?
            </p>

            <div className="mt-16 grid gap-6 md:grid-cols-3">
              {PROBLEM_CARDS.map((card) => (
                <Card
                  key={card.title}
                  className="border-0 bg-gradient-to-b from-white to-sky-50/50 shadow-sm transition-all duration-300 hover:-translate-y-1 hover:shadow-lg"
                >
                  <CardContent className="p-7">
                    <div className="mb-4 flex h-11 w-11 items-center justify-center rounded-xl bg-primary/10 text-primary">
                      <card.icon className="size-5" />
                    </div>
                    <h3 className="text-lg font-semibold text-foreground">
                      {card.title}
                    </h3>
                    <p className="mt-3 text-sm leading-relaxed text-muted-foreground">
                      {card.body}
                    </p>
                  </CardContent>
                </Card>
              ))}
            </div>
          </div>
        </section>

        {/* ── How It Works ──────────────────────────────────────── */}
        <section id="how-it-works" className="bg-secondary py-24 sm:py-32">
          <div className="mx-auto max-w-6xl px-4 sm:px-6">
            <SectionLabel>How it works</SectionLabel>
            <h2 className="mt-3 text-center text-3xl font-bold tracking-tight text-foreground sm:text-4xl">
              Three steps. Thirty seconds. Done.
            </h2>
            <p className="mx-auto mt-4 max-w-2xl text-center text-lg text-muted-foreground">
              Here&rsquo;s how CompliDrop works: upload a document, confirm what
              it found, and let the reminders run.
            </p>

            <div className="mt-16 grid gap-8 md:grid-cols-3">
              {STEPS.map((s) => (
                <div
                  key={s.step}
                  className="rounded-2xl bg-white p-8 shadow-sm transition-all duration-300 hover:-translate-y-1 hover:shadow-lg"
                >
                  <div className="flex items-center gap-4">
                    <div className="flex h-12 w-12 shrink-0 items-center justify-center rounded-full bg-primary text-lg font-bold text-white shadow-md">
                      {s.step}
                    </div>
                    <s.icon className="size-6 text-muted-foreground" />
                  </div>
                  <h3 className="mt-5 text-xl font-semibold text-foreground">
                    {s.title}
                  </h3>
                  <p className="mt-3 text-sm leading-relaxed text-muted-foreground">
                    {s.body}
                  </p>
                </div>
              ))}
            </div>
          </div>
        </section>

        {/* ── Social proof ──────────────────────────────────────── */}
        <SocialProof />

        {/* ── Pricing ───────────────────────────────────────────── */}
        {/* id="pricing" backs the /#pricing deep-link the register page's
            plan-banner "Change" affordance uses (#31). */}
        <section id="pricing" className="bg-white py-24 sm:py-32">
          <div className="mx-auto max-w-6xl px-4 sm:px-6">
            <SectionLabel>Pricing</SectionLabel>
            <h2 className="mt-3 text-center text-3xl font-bold tracking-tight text-foreground sm:text-4xl">
              Enterprise accuracy. Small-business pricing.
            </h2>
            <p className="mx-auto mt-4 max-w-2xl text-center text-lg text-muted-foreground">
              No annual contracts. No minimums. No sales calls. Cancel anytime.
            </p>

            <div className="mt-16 grid gap-6 md:grid-cols-3">
              {/* Free */}
              <Card className="border-0 shadow-sm transition-all duration-300 hover:-translate-y-1 hover:shadow-lg">
                <CardContent className="flex flex-col p-7">
                  <h3 className="text-lg font-semibold text-foreground">Free</h3>
                  <p className="mt-2 text-foreground">
                    <span className="text-4xl font-extrabold">{PLANS.free.monthlyPriceLabel}</span>
                    <span className="text-base font-normal text-muted-foreground">/month</span>
                  </p>
                  <p className="mt-5 flex-1 text-sm leading-relaxed text-muted-foreground">
                    Track up to 5 documents. Full automatic extraction. Email
                    reminders. Perfect for testing it on your most troublesome
                    vendor first.
                  </p>
                  <Link
                    href="/register?plan=free"
                    className={cn(
                      buttonVariants({ variant: "outline" }),
                      "mt-6 w-full cursor-pointer rounded-xl border-primary py-5 font-semibold text-primary transition-all duration-200 hover:bg-primary/5",
                    )}
                  >
                    Start free&nbsp;&rarr;
                  </Link>
                </CardContent>
              </Card>

              {/* Pro */}
              <Card className="relative overflow-visible border-2 border-primary shadow-lg transition-all duration-300 hover:-translate-y-1 hover:shadow-xl">
                <CardContent className="flex flex-col p-7">
                  <Badge className="absolute -top-3 left-6 rounded-full bg-accent px-3 py-1 text-xs font-bold text-accent-foreground shadow-md">
                    Most popular
                  </Badge>
                  <h3 className="text-lg font-semibold text-foreground">Pro</h3>
                  <p className="mt-2 text-foreground">
                    <span className="text-4xl font-extrabold">{PLANS.pro.monthlyPriceLabel}</span>
                    <span className="text-base font-normal text-muted-foreground">/month</span>
                  </p>
                  <p className="mt-5 flex-1 text-sm leading-relaxed text-muted-foreground">
                    Unlimited documents. A no-login link your vendors use to send their
                    own certificates. Email reminders at 60, 30, and 7 days. Automatic
                    checks against the coverage you require. One-click reports for audits.
                    Everything you need, nothing you don&rsquo;t.
                  </p>
                  <Link
                    href="/register?plan=pro"
                    className={cn(
                      buttonVariants(),
                      "mt-6 w-full cursor-pointer rounded-xl bg-accent py-5 text-base font-semibold text-accent-foreground shadow-md transition-all duration-200 hover:bg-accent/90 hover:shadow-lg",
                    )}
                  >
                    Get started&nbsp;&rarr;
                  </Link>
                </CardContent>
              </Card>

              {/* Annual */}
              <Card className="border-0 shadow-sm transition-all duration-300 hover:-translate-y-1 hover:shadow-lg">
                <CardContent className="flex flex-col p-7">
                  <h3 className="text-lg font-semibold text-foreground">Annual</h3>
                  <p className="mt-2 text-foreground">
                    <span className="text-4xl font-extrabold">{PLANS.annual.monthlyPriceLabel}</span>
                    <span className="text-base font-normal text-muted-foreground">/month</span>
                  </p>
                  <p className="mt-1 text-sm font-semibold text-primary">
                    {PLANS.annual.annualBilledLabel} &mdash; {PLANS.annual.annualSavingsLabel}
                  </p>
                  <p className="mt-4 flex-1 text-sm leading-relaxed text-muted-foreground">
                    Everything in Pro. Commit for the year and keep two
                    months&rsquo; worth in your pocket &mdash; less than one hour
                    of your office manager&rsquo;s time per month.
                  </p>
                  <Link
                    href="/register?plan=annual"
                    className={cn(
                      buttonVariants({ variant: "outline" }),
                      "mt-6 w-full cursor-pointer rounded-xl border-primary py-5 font-semibold text-primary transition-all duration-200 hover:bg-primary/5",
                    )}
                  >
                    Get started&nbsp;&rarr;
                  </Link>
                </CardContent>
              </Card>
            </div>
          </div>
        </section>

        {/* ── Who It's For ──────────────────────────────────────── */}
        <section className="bg-[#082F49] py-24 sm:py-32">
          <div className="mx-auto max-w-6xl px-4 sm:px-6">
            <SectionLabel>Who it&rsquo;s for</SectionLabel>
            <h2 className="mt-3 text-center text-3xl font-bold tracking-tight text-white sm:text-4xl">
              If you&rsquo;ve ever lost sleep over an expired certificate, this is
              for you.
            </h2>

            <div className="mt-16 grid gap-6 md:grid-cols-2 lg:grid-cols-3">
              {AUDIENCE_CARDS.map((card) => (
                <Card
                  key={card.title}
                  className="border-0 shadow-sm transition-all duration-300 hover:-translate-y-1 hover:shadow-lg"
                  style={{ backgroundColor: "rgba(255,255,255,0.06)" }}
                >
                  <CardContent className="flex h-full flex-col p-7">
                    <div className="mb-1 h-1 w-10 rounded-full bg-primary" />
                    <h3 className="mt-4 text-lg font-semibold text-white">
                      {card.title}
                    </h3>
                    <p className="mt-3 flex-1 text-sm leading-relaxed text-sky-100/75">
                      {card.body}
                    </p>
                    {card.href ? (
                      <Link
                        href={card.href}
                        className="mt-4 inline-flex items-center text-sm font-semibold text-primary hover:text-sky-300"
                      >
                        See it for venues&nbsp;&rarr;
                      </Link>
                    ) : null}
                  </CardContent>
                </Card>
              ))}
            </div>
          </div>
        </section>

        {/* ── Final CTA ─────────────────────────────────────────── */}
        <section className="relative overflow-hidden bg-[#082F49] py-24 sm:py-32">
          <div className="pointer-events-none absolute left-1/2 top-0 h-[600px] w-[600px] -translate-x-1/2 -translate-y-1/2 rounded-full bg-primary opacity-10 blur-3xl" />

          <div className="relative mx-auto max-w-2xl px-4 text-center sm:px-6">
            <SectionLabel>Get started</SectionLabel>
            <h2 className="mt-3 text-3xl font-bold tracking-tight text-white sm:text-4xl">
              Drop your first document in under a minute.
            </h2>
            <p className="mt-4 text-lg text-sky-200/80">
              Free for your first 5 documents&nbsp;&mdash; full automatic
              extraction and reminders, no credit card. Upgrade to Pro when
              you&rsquo;re ready.
            </p>

            <div className="mt-10 flex flex-col items-center justify-center gap-4 sm:flex-row">
              <Link href="/register" className={heroPrimaryCta}>
                Get started free
                <ArrowRight className="ml-2 size-4" />
              </Link>
              <Link
                href="/coi-tracking-for-event-venues"
                className={cn(
                  buttonVariants({ variant: "outline", size: "lg" }),
                  "h-13 cursor-pointer rounded-xl border-2 px-8 text-base font-semibold text-white transition-all duration-200 hover:opacity-90",
                )}
                style={{ borderColor: "rgba(255,255,255,0.25)", backgroundColor: "transparent" }}
              >
                Run a venue? Start here
              </Link>
            </div>

            <p className="mt-5 text-sm text-sky-300/60">No credit card. Cancel anytime.</p>
          </div>
        </section>
      </main>

      <MarketingFooter />
    </>
  );
}
