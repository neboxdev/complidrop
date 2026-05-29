"use client";

/**
 * Shared marketing site header — a sticky nav reused by the homepage and every
 * content page (FAQ, glossary, comparisons, use-case pages).
 *
 * This is the ONLY client island on the marketing surface: it reads auth state
 * via `useMe` to swap the primary CTA between "Get started" (anonymous) and
 * "Go to dashboard" (signed in). Keeping it isolated lets the pages that embed
 * it stay server components — so their copy renders in the initial HTML for
 * crawlers and AI, and they ship no marketing JS (the INP win in [#176]).
 *
 * `skipRefresh` keeps an anonymous visitor's auth probe to a single round-trip
 * (no automatic POST /api/auth/refresh on the 401) — see useAuth.ts (#30).
 *
 * Colors use the design-system tokens from globals.css (`primary` = sky,
 * `accent` = CTA orange, `foreground` = navy) rather than hardcoded hex, so the
 * marketing chrome stays in sync with the rest of the app by construction.
 */
import Link from "next/link";
import { buttonVariants } from "@/components/ui/button";
import { Logo } from "@/components/Logo";
import { cn } from "@/lib/utils";
import { useMe } from "@/hooks/useAuth";

/** Secondary nav → the content pages, for discoverability + internal linking. Hidden below md to keep the bar uncluttered. */
const NAV_LINKS = [
  { href: "/coi-tracking-for-event-venues", label: "Event venues" },
  { href: "/faq", label: "FAQ" },
  { href: "/glossary", label: "Glossary" },
] as const;

const ctaClass = cn(
  buttonVariants(),
  "h-9 cursor-pointer rounded-lg bg-accent px-5 text-sm font-semibold text-accent-foreground shadow-md transition-all duration-200 hover:bg-accent/90 hover:shadow-lg",
);

export function MarketingHeader() {
  const { data: me } = useMe({ skipRefresh: true });
  const authed = !!me;

  return (
    <header className="sticky top-0 z-50 border-b border-border bg-white/80 backdrop-blur-lg">
      <div className="mx-auto flex h-16 max-w-6xl items-center justify-between px-4 sm:px-6">
        <Link href="/" className="flex items-center" aria-label="CompliDrop — home">
          <Logo variant="primary" height={36} decorative />
        </Link>

        <nav className="flex items-center gap-1 sm:gap-3">
          <div className="mr-2 hidden items-center gap-6 md:flex">
            {NAV_LINKS.map((link) => (
              <Link
                key={link.href}
                href={link.href}
                className="text-sm font-medium text-muted-foreground transition-colors hover:text-foreground"
              >
                {link.label}
              </Link>
            ))}
          </div>

          {authed ? (
            <Link href="/dashboard" className={ctaClass}>
              Go to dashboard
            </Link>
          ) : (
            <>
              <Link
                href="/login"
                className="rounded-lg px-3 py-2 text-sm font-semibold text-foreground transition-opacity duration-200 hover:opacity-70"
              >
                Log in
              </Link>
              <Link href="/register" className={ctaClass}>
                Get started
              </Link>
            </>
          )}
        </nav>
      </div>
    </header>
  );
}

export default MarketingHeader;
