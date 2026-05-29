/**
 * Shared marketing footer (server component) reused across the marketing pages.
 *
 * Beyond branding, the link columns do real SEO work: they internal-link every
 * content page from every other page, which is how a low-authority site spreads
 * crawl equity and signals topical structure (the internal-linking lever called
 * out in [#176]). The brand `Logo` here is non-decorative (`role="img"`) — the
 * homepage test pins that the mark renders in the hero AND footer.
 */
import Link from "next/link";
import { Logo } from "@/components/Logo";

const PRODUCT_LINKS = [
  { href: "/#how-it-works", label: "How it works" },
  { href: "/#pricing", label: "Pricing" },
  { href: "/coi-tracking-for-event-venues", label: "For event venues" },
  { href: "/coi-tracking-software-vs-spreadsheet", label: "vs. a spreadsheet" },
] as const;

const RESOURCE_LINKS = [
  { href: "/faq", label: "FAQ" },
  { href: "/glossary", label: "Compliance glossary" },
  { href: "/glossary/certificate-of-insurance", label: "What is a COI?" },
  { href: "/glossary/additional-insured-vs-certificate-holder", label: "Additional insured vs. certificate holder" },
] as const;

// No "Log in" link here by design: the sticky header already carries it for
// anonymous visitors, and a footer that tells a signed-in user to log in is
// just noise. (The homepage test also pins that /login only appears in the
// header's logged-out branch.)
const GET_STARTED_LINKS = [
  { href: "/register", label: "Create a free account" },
  { href: "/#pricing", label: "See pricing" },
] as const;

const COLUMNS = [
  { heading: "Product", links: PRODUCT_LINKS },
  { heading: "Resources", links: RESOURCE_LINKS },
  { heading: "Get started", links: GET_STARTED_LINKS },
] as const;

export function MarketingFooter() {
  return (
    <footer className="border-t border-border bg-white">
      <div className="mx-auto max-w-6xl px-4 py-12 sm:px-6">
        <div className="grid gap-10 sm:grid-cols-2 md:grid-cols-4">
          <div className="space-y-3">
            <Logo variant="primary" height={32} />
            <p className="max-w-xs text-sm text-muted-foreground">
              COI, license, and permit tracking for small businesses — automatic
              extraction, compliance checks, and expiration reminders.
            </p>
          </div>

          {COLUMNS.map((column) => (
            <nav key={column.heading} aria-label={column.heading}>
              <h2 className="text-xs font-bold uppercase tracking-[0.15em] text-foreground">
                {column.heading}
              </h2>
              <ul className="mt-4 space-y-2.5">
                {column.links.map((link) => (
                  <li key={link.href}>
                    <Link
                      href={link.href}
                      className="text-sm text-muted-foreground transition-colors hover:text-foreground"
                    >
                      {link.label}
                    </Link>
                  </li>
                ))}
              </ul>
            </nav>
          ))}
        </div>

        <div className="mt-10 border-t border-border pt-6 text-sm text-muted-foreground">
          Drop your docs. Stay compliant. &copy; {new Date().getFullYear()} CompliDrop
        </div>
      </div>
    </footer>
  );
}

export default MarketingFooter;
