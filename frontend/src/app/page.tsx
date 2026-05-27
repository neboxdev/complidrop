"use client";

import Link from "next/link";
import { buttonVariants } from "@/components/ui/button";
import { Badge } from "@/components/ui/badge";
import { Card, CardContent } from "@/components/ui/card";
import { Logo } from "@/components/Logo";
import { cn } from "@/lib/utils";
import { useMe } from "@/hooks/useAuth";
import { PLANS } from "@/lib/plans";
import {
  Upload,
  ShieldCheck,
  BellRing,
  Clock,
  DollarSign,
  ScanSearch,
  ArrowRight,
} from "lucide-react";

/* ── Colors (from design system) ──────────────────────────────────── */
const C = {
  sky: "#0EA5E9",
  skyHover: "#0284C7",
  skyLight: "#38BDF8",
  cta: "#F97316",
  ctaHover: "#EA580C",
  bg: "#F0F9FF",
  text: "#0C4A6E",
  muted: "#64748B",
  dark: "#082F49",
  border: "#E0F2FE",
  white: "#ffffff",
} as const;

/* ── Section Label ────────────────────────────────────────────────── */
function SectionLabel({ children }: { children: React.ReactNode }) {
  return (
    <p
      className="text-center text-xs font-bold uppercase tracking-[0.2em]"
      style={{ color: C.sky }}
    >
      {children}
    </p>
  );
}

/* ── Page ─────────────────────────────────────────────────────────── */
export default function Home() {
  // skipRefresh keeps the anonymous-visitor cost to a single auth round-trip
  // (no automatic POST /api/auth/refresh on the 401). This hook is opt-in
  // because authenticated routes — dashboard layout, settings, etc. — still
  // need refresh-on-401 to survive an expired cd_session. See useAuth.ts (#30).
  const { data: me } = useMe({ skipRefresh: true });
  const authed = !!me;

  return (
    <>
      {/* ── Sticky Nav ──────────────────────────────────────────── */}
      <header className="sticky top-0 z-50 border-b bg-white/80 backdrop-blur-lg transition-all duration-300" style={{ borderColor: C.border }}>
        <div className="mx-auto flex h-16 max-w-6xl items-center justify-between px-4 sm:px-6">
          <Link href="/" className="flex items-center" aria-label="CompliDrop — home">
            <Logo variant="primary" height={36} decorative />
          </Link>
          <nav className="flex items-center gap-1 sm:gap-3">
            {authed ? (
              <Link
                href="/dashboard"
                className={cn(
                  buttonVariants(),
                  "h-9 cursor-pointer rounded-lg px-5 text-sm font-semibold shadow-md transition-all duration-200 hover:shadow-lg"
                )}
                style={{ backgroundColor: C.cta, color: C.white }}
                onMouseEnter={(e) => (e.currentTarget.style.backgroundColor = C.ctaHover)}
                onMouseLeave={(e) => (e.currentTarget.style.backgroundColor = C.cta)}
              >
                Go to dashboard
              </Link>
            ) : (
              <>
                <Link
                  href="/login"
                  className="rounded-lg px-3 py-2 text-sm font-semibold transition-opacity duration-200 hover:opacity-70"
                  style={{ color: C.text }}
                >
                  Log in
                </Link>
                <Link
                  href="/register"
                  className={cn(
                    buttonVariants(),
                    "h-9 cursor-pointer rounded-lg px-5 text-sm font-semibold shadow-md transition-all duration-200 hover:shadow-lg"
                  )}
                  style={{ backgroundColor: C.cta, color: C.white }}
                  onMouseEnter={(e) => (e.currentTarget.style.backgroundColor = C.ctaHover)}
                  onMouseLeave={(e) => (e.currentTarget.style.backgroundColor = C.cta)}
                >
                  Get started
                </Link>
              </>
            )}
          </nav>
        </div>
      </header>

      <main>
        {/* ── Hero ──────────────────────────────────────────────── */}
        <section
          className="relative overflow-hidden"
          style={{
            background: `linear-gradient(135deg, ${C.bg} 0%, #ffffff 50%, ${C.bg} 100%)`,
          }}
        >
          {/* Decorative blobs */}
          <div
            className="pointer-events-none absolute -left-40 -top-40 h-[500px] w-[500px] rounded-full opacity-20 blur-3xl"
            style={{ backgroundColor: C.skyLight }}
          />
          <div
            className="pointer-events-none absolute -right-40 top-20 h-[400px] w-[400px] rounded-full opacity-15 blur-3xl"
            style={{ backgroundColor: C.cta }}
          />

          <div className="relative mx-auto max-w-4xl px-4 py-24 text-center sm:px-6 sm:py-36">
            <div className="mb-8 flex justify-center sm:mb-10">
              <Logo variant="twotone" height={48} />
            </div>
            <h1
              className="text-4xl font-extrabold leading-[1.1] tracking-tight sm:text-5xl lg:text-6xl"
              style={{ color: C.text }}
            >
              Stop Chasing Paper.
              <br />
              <span style={{ color: C.sky }}>Start Dropping Docs.</span>
            </h1>

            <p
              className="mx-auto mt-6 max-w-2xl text-lg leading-relaxed sm:text-xl"
              style={{ color: C.muted }}
            >
              CompliDrop reads your COIs, licenses, and permits in
              seconds&nbsp;&mdash; pulls the dates, checks the coverage, and
              alerts you before anything expires. {PLANS.pro.monthlyPriceLabel}/month. No contracts.
            </p>

            <div className="mt-10 flex flex-col items-center gap-4 sm:flex-row sm:justify-center">
              <Link
                href="/register"
                className={cn(
                  buttonVariants({ size: "lg" }),
                  "h-13 cursor-pointer rounded-xl px-8 text-base font-semibold shadow-lg transition-all duration-200 hover:shadow-xl"
                )}
                style={{ backgroundColor: C.cta, color: C.white }}
                onMouseEnter={(e) =>
                  (e.currentTarget.style.backgroundColor = C.ctaHover)
                }
                onMouseLeave={(e) =>
                  (e.currentTarget.style.backgroundColor = C.cta)
                }
              >
                Get started free
                <ArrowRight className="ml-2 size-4" />
              </Link>
              <a
                href="#how-it-works"
                className={cn(
                  buttonVariants({ variant: "outline", size: "lg" }),
                  "h-13 cursor-pointer rounded-xl border-2 px-8 text-base font-semibold transition-all duration-200"
                )}
                style={{ borderColor: C.sky, color: C.sky }}
              >
                See How It Works&nbsp;↓
              </a>
            </div>
          </div>
        </section>

        {/* ── Problem ───────────────────────────────────────────── */}
        <section className="py-24 sm:py-32" style={{ backgroundColor: C.white }}>
          <div className="mx-auto max-w-6xl px-4 sm:px-6">
            <SectionLabel>The Problem</SectionLabel>
            <h2
              className="mt-3 text-center text-3xl font-bold tracking-tight sm:text-4xl"
              style={{ color: C.text }}
            >
              Your spreadsheet is a ticking time bomb.
            </h2>
            <p
              className="mx-auto mt-4 max-w-2xl text-center text-lg"
              style={{ color: C.muted }}
            >
              You&rsquo;re tracking dozens of vendors, hundreds of documents,
              and one missed expiration away from a shutdown. Sound familiar?
            </p>

            <div className="mt-16 grid gap-6 md:grid-cols-3">
              {(
                [
                  {
                    icon: Clock,
                    title: "The 4 AM Spreadsheet Panic",
                    body: "You've got 30 subs and a manila folder's worth of COIs that all expire at different times. One missed row means a crew can't get on the job site Monday morning — and you're the one making phone calls at dawn.",
                  },
                  {
                    icon: DollarSign,
                    title: "Software That Costs More Than Your Truck Payment",
                    body: "Enterprise compliance tools want $10,000 a year, a two-year contract, and a month-long onboarding. You just need to know if Tony's GL policy is current. You shouldn't need a procurement department to find out.",
                  },
                  {
                    icon: ScanSearch,
                    title: "OCR That Can't Read a PDF",
                    body: 'You tried the other tools. Their "smart" document scanner misread the date, marked the wrong coverage type, and you ended up re-entering everything by hand anyway. So much for automation.',
                  },
                ] as const
              ).map((card) => (
                <Card
                  key={card.title}
                  className="group cursor-pointer border-0 bg-gradient-to-b from-white to-sky-50/50 shadow-sm transition-all duration-300 hover:-translate-y-1 hover:shadow-lg"
                  style={{ borderColor: C.border }}
                >
                  <CardContent className="p-7">
                    <div
                      className="mb-4 flex h-11 w-11 items-center justify-center rounded-xl transition-colors duration-200"
                      style={{ backgroundColor: `${C.sky}15`, color: C.sky }}
                    >
                      <card.icon className="size-5" />
                    </div>
                    <h3
                      className="text-lg font-semibold"
                      style={{ color: C.text }}
                    >
                      &ldquo;{card.title}&rdquo;
                    </h3>
                    <p
                      className="mt-3 text-sm leading-relaxed"
                      style={{ color: C.muted }}
                    >
                      {card.body}
                    </p>
                  </CardContent>
                </Card>
              ))}
            </div>
          </div>
        </section>

        {/* ── How It Works ──────────────────────────────────────── */}
        <section id="how-it-works" className="py-24 sm:py-32" style={{ backgroundColor: C.bg }}>
          <div className="mx-auto max-w-6xl px-4 sm:px-6">
            <SectionLabel>How It Works</SectionLabel>
            <h2
              className="mt-3 text-center text-3xl font-bold tracking-tight sm:text-4xl"
              style={{ color: C.text }}
            >
              Three steps. Thirty seconds. Done.
            </h2>

            <div className="mt-16 grid gap-8 md:grid-cols-3">
              {(
                [
                  {
                    step: "1",
                    icon: Upload,
                    title: "Drop It",
                    body: "Upload a COI, license, or permit — PDF, photo, even a scan from your phone. CompliDrop's AI reads the document and pulls out the names, dates, coverage types, and limits automatically. No templates. No reformatting.",
                  },
                  {
                    step: "2",
                    icon: ShieldCheck,
                    title: "Check It",
                    body: "CompliDrop instantly matches what it found against your requirements. Need every sub to carry $1M in general liability? It flags the gaps before you have to go looking. Review the extraction, confirm with one click, and you're done.",
                  },
                  {
                    step: "3",
                    icon: BellRing,
                    title: "Forget About It",
                    body: "Automated reminders go out at 60, 30, and 7 days before expiration — to you and directly to your vendor. They get a simple upload link (no login, no account creation) so they can send the updated doc themselves. You stop being the bad guy.",
                  },
                ] as const
              ).map((s) => (
                <div
                  key={s.step}
                  className="group rounded-2xl bg-white p-8 shadow-sm transition-all duration-300 hover:-translate-y-1 hover:shadow-lg"
                >
                  <div className="flex items-center gap-4">
                    <div
                      className="flex h-12 w-12 shrink-0 items-center justify-center rounded-full text-lg font-bold text-white shadow-md"
                      style={{ backgroundColor: C.sky }}
                    >
                      {s.step}
                    </div>
                    <s.icon className="size-6" style={{ color: C.muted }} />
                  </div>
                  <h3
                    className="mt-5 text-xl font-semibold"
                    style={{ color: C.text }}
                  >
                    {s.title}
                  </h3>
                  <p
                    className="mt-3 text-sm leading-relaxed"
                    style={{ color: C.muted }}
                  >
                    {s.body}
                  </p>
                </div>
              ))}
            </div>
          </div>
        </section>

        {/* ── Pricing ───────────────────────────────────────────── */}
        {/* id="pricing" backs the /#pricing deep-link the register page's
            plan-banner "Change" affordance uses to send a user who picked
            the wrong CTA back to the pricing cards (#31). */}
        <section id="pricing" className="py-24 sm:py-32" style={{ backgroundColor: C.white }}>
          <div className="mx-auto max-w-6xl px-4 sm:px-6">
            <SectionLabel>Pricing</SectionLabel>
            <h2
              className="mt-3 text-center text-3xl font-bold tracking-tight sm:text-4xl"
              style={{ color: C.text }}
            >
              Enterprise accuracy. Utility-company pricing.
            </h2>
            <p
              className="mx-auto mt-4 max-w-2xl text-center text-lg"
              style={{ color: C.muted }}
            >
              No annual contracts. No minimums. No sales calls. Cancel anytime.
            </p>

            <div className="mt-16 grid gap-6 md:grid-cols-3">
              {/* Free */}
              <Card className="border-0 shadow-sm transition-all duration-300 hover:-translate-y-1 hover:shadow-lg">
                <CardContent className="flex flex-col p-7">
                  <h3
                    className="text-lg font-semibold"
                    style={{ color: C.text }}
                  >
                    Free
                  </h3>
                  <p className="mt-2" style={{ color: C.text }}>
                    <span className="text-4xl font-extrabold">$0</span>
                    <span
                      className="text-base font-normal"
                      style={{ color: C.muted }}
                    >
                      /month
                    </span>
                  </p>
                  <p
                    className="mt-5 flex-1 text-sm leading-relaxed"
                    style={{ color: C.muted }}
                  >
                    Track up to 5 documents. Full AI extraction. Email
                    reminders. Perfect for testing it with your most annoying
                    vendor first.
                  </p>
                  <Link
                    href="/register?plan=free"
                    className={cn(
                      buttonVariants({ variant: "outline" }),
                      "mt-6 w-full cursor-pointer rounded-xl py-5 font-semibold transition-all duration-200"
                    )}
                    style={{ borderColor: C.sky, color: C.sky }}
                  >
                    Start Free&nbsp;→
                  </Link>
                </CardContent>
              </Card>

              {/* Pro */}
              <Card
                className="relative overflow-visible border-2 shadow-lg transition-all duration-300 hover:-translate-y-1 hover:shadow-xl"
                style={{ borderColor: C.sky }}
              >
                <CardContent className="flex flex-col p-7">
                  <Badge
                    className="absolute -top-3 left-6 rounded-full px-3 py-1 text-xs font-bold text-white shadow-md"
                    style={{ backgroundColor: C.cta }}
                  >
                    Most Popular
                  </Badge>
                  <h3
                    className="text-lg font-semibold"
                    style={{ color: C.text }}
                  >
                    Pro
                  </h3>
                  <p className="mt-2" style={{ color: C.text }}>
                    <span className="text-4xl font-extrabold">{PLANS.pro.monthlyPriceLabel}</span>
                    <span
                      className="text-base font-normal"
                      style={{ color: C.muted }}
                    >
                      /month
                    </span>
                  </p>
                  <p
                    className="mt-5 flex-1 text-sm leading-relaxed"
                    style={{ color: C.muted }}
                  >
                    Unlimited documents. Vendor upload portal. Multi-channel
                    reminders. Compliance rules engine. Audit-ready exports.
                    Everything you need, nothing you don&rsquo;t.
                  </p>
                  <Link
                    href="/register?plan=pro"
                    className={cn(
                      buttonVariants(),
                      "mt-6 w-full cursor-pointer rounded-xl py-5 text-base font-semibold shadow-md transition-all duration-200 hover:shadow-lg"
                    )}
                    style={{ backgroundColor: C.cta, color: C.white }}
                    onMouseEnter={(e) =>
                      (e.currentTarget.style.backgroundColor = C.ctaHover)
                    }
                    onMouseLeave={(e) =>
                      (e.currentTarget.style.backgroundColor = C.cta)
                    }
                  >
                    Get started&nbsp;→
                  </Link>
                </CardContent>
              </Card>

              {/* Annual */}
              <Card className="border-0 shadow-sm transition-all duration-300 hover:-translate-y-1 hover:shadow-lg">
                <CardContent className="flex flex-col p-7">
                  <h3
                    className="text-lg font-semibold"
                    style={{ color: C.text }}
                  >
                    Annual
                  </h3>
                  <p className="mt-2" style={{ color: C.text }}>
                    <span className="text-4xl font-extrabold">{PLANS.annual.monthlyPriceLabel}</span>
                    <span
                      className="text-base font-normal"
                      style={{ color: C.muted }}
                    >
                      /month
                    </span>
                  </p>
                  <p className="mt-1 text-sm font-semibold" style={{ color: C.sky }}>
                    {PLANS.annual.annualBilledLabel} &mdash; {PLANS.annual.annualSavingsLabel}
                  </p>
                  <p
                    className="mt-4 flex-1 text-sm leading-relaxed"
                    style={{ color: C.muted }}
                  >
                    Everything in Pro. Commit for the year, keep two
                    months&rsquo; worth in your pocket. That&rsquo;s less than
                    one hour of your office manager&rsquo;s time per month.
                  </p>
                  <Link
                    href="/register?plan=annual"
                    className={cn(
                      buttonVariants({ variant: "outline" }),
                      "mt-6 w-full cursor-pointer rounded-xl py-5 font-semibold transition-all duration-200"
                    )}
                    style={{ borderColor: C.sky, color: C.sky }}
                  >
                    Get started&nbsp;→
                  </Link>
                </CardContent>
              </Card>
            </div>
          </div>
        </section>

        {/* ── Who It's For ──────────────────────────────────────── */}
        <section className="py-24 sm:py-32" style={{ backgroundColor: C.dark }}>
          <div className="mx-auto max-w-6xl px-4 sm:px-6">
            <SectionLabel>Who It&rsquo;s For</SectionLabel>
            <h2
              className="mt-3 text-center text-3xl font-bold tracking-tight text-white sm:text-4xl"
            >
              If you&rsquo;ve ever lost sleep over an expired certificate,
              this is for you.
            </h2>

            <div className="mt-16 grid gap-6 md:grid-cols-2 lg:grid-cols-3">
              {(
                [
                  {
                    title: "Construction & General Contractors",
                    body: "You're juggling COIs for 40 subs and one expired policy means a crew sitting in the parking lot instead of on the job site. CompliDrop tracks every sub so Monday morning isn't a fire drill.",
                  },
                  {
                    title: "Property Management",
                    body: "Vendor insurance, tenant certs, lease expirations — one coverage gap on a property and you're holding the liability. Stop cross-referencing three spreadsheets to figure out who's current.",
                  },
                  {
                    title: "Healthcare Practices",
                    body: "DEA renewals, state licenses, hospital privileges, malpractice certs — all expiring on different dates across every provider. One lapse and a clinician can't see patients until it's fixed.",
                  },
                  {
                    title: "Transportation & Trucking",
                    body: "DOT medical cards, CDLs, vehicle inspections, cargo insurance — every driver is a stack of documents with a deadline. Miss one and that truck doesn't move.",
                  },
                  {
                    title: "Professional Services",
                    body: "Liability insurance, professional certifications, contractor agreements — your clients expect proof on demand. \"Let me dig through my files\" isn't the answer they're looking for.",
                  },
                ] as const
              ).map((card) => (
                <Card
                  key={card.title}
                  className="group border-0 shadow-sm transition-all duration-300 hover:-translate-y-1 hover:shadow-lg"
                  style={{ backgroundColor: "rgba(255,255,255,0.06)" }}
                >
                  <CardContent className="p-7">
                    <div
                      className="mb-1 h-1 w-10 rounded-full"
                      style={{ backgroundColor: C.sky }}
                    />
                    <h3
                      className="mt-4 text-lg font-semibold text-white"
                    >
                      {card.title}
                    </h3>
                    <p
                      className="mt-3 text-sm leading-relaxed"
                      style={{ color: "rgba(186,230,253,0.75)" }}
                    >
                      {card.body}
                    </p>
                  </CardContent>
                </Card>
              ))}
            </div>
          </div>
        </section>

        {/* ── Final CTA ─────────────────────────────────────────── */}
        <section
          className="relative overflow-hidden py-24 sm:py-32"
          style={{ backgroundColor: C.dark }}
        >
          {/* Decorative blob */}
          <div
            className="pointer-events-none absolute left-1/2 top-0 h-[600px] w-[600px] -translate-x-1/2 -translate-y-1/2 rounded-full opacity-10 blur-3xl"
            style={{ backgroundColor: C.sky }}
          />

          <div className="relative mx-auto max-w-2xl px-4 text-center sm:px-6">
            <SectionLabel>Get Started</SectionLabel>
            <h2 className="mt-3 text-3xl font-bold tracking-tight text-white sm:text-4xl">
              Drop your first doc in under a minute.
            </h2>
            <p className="mt-4 text-lg text-sky-200/80">
              Free for your first 5 documents&nbsp;&mdash; full AI extraction and
              reminders, no credit card. Upgrade to Pro when you&rsquo;re ready.
            </p>

            <div className="mt-10 flex flex-col items-center justify-center gap-4 sm:flex-row">
              {authed ? (
                <Link
                  href="/dashboard"
                  className={cn(
                    buttonVariants({ size: "lg" }),
                    "h-13 cursor-pointer rounded-xl px-8 text-base font-semibold shadow-lg transition-all duration-200 hover:shadow-xl"
                  )}
                  style={{ backgroundColor: C.cta, color: C.white }}
                  onMouseEnter={(e) => (e.currentTarget.style.backgroundColor = C.ctaHover)}
                  onMouseLeave={(e) => (e.currentTarget.style.backgroundColor = C.cta)}
                >
                  Go to dashboard
                  <ArrowRight className="ml-2 size-4" />
                </Link>
              ) : (
                <>
                  <Link
                    href="/register"
                    className={cn(
                      buttonVariants({ size: "lg" }),
                      "h-13 cursor-pointer rounded-xl px-8 text-base font-semibold shadow-lg transition-all duration-200 hover:shadow-xl"
                    )}
                    style={{ backgroundColor: C.cta, color: C.white }}
                    onMouseEnter={(e) => (e.currentTarget.style.backgroundColor = C.ctaHover)}
                    onMouseLeave={(e) => (e.currentTarget.style.backgroundColor = C.cta)}
                  >
                    Get started free
                    <ArrowRight className="ml-2 size-4" />
                  </Link>
                  <Link
                    href="/login"
                    className={cn(
                      buttonVariants({ variant: "outline", size: "lg" }),
                      "h-13 cursor-pointer rounded-xl border-2 px-8 text-base font-semibold transition-all duration-200 hover:opacity-90"
                    )}
                    style={{ borderColor: "rgba(255,255,255,0.25)", color: C.white, backgroundColor: "transparent" }}
                  >
                    Log in
                  </Link>
                </>
              )}
            </div>

            <p className="mt-5 text-sm text-sky-300/60">
              No credit card. Cancel anytime.
            </p>
          </div>
        </section>
      </main>

      {/* ── Footer ────────────────────────────────────────────── */}
      <footer
        className="border-t py-8"
        style={{ borderColor: C.border, backgroundColor: C.white }}
      >
        <div className="mx-auto flex max-w-6xl items-center justify-between px-4 sm:px-6">
          <Logo variant="primary" height={32} />

          <p className="text-sm" style={{ color: C.muted }}>
            Drop your docs. Stay compliant. &copy; 2026 CompliDrop
          </p>
        </div>
      </footer>
    </>
  );
}
