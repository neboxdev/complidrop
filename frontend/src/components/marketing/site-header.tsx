"use client";

/**
 * Shared marketing site header — a sticky nav reused by the homepage and every
 * content page (FAQ, glossary, comparisons, use-case pages).
 *
 * This is the ONLY client island on the marketing surface: it reads auth state
 * via `useMe` to swap the primary CTA between "Get started" (anonymous) and
 * "Go to dashboard" (signed in). Keeping it isolated lets the pages that embed
 * it stay server components — so their copy renders in the initial HTML for
 * crawlers and AI, and they add no *additional* client island (the INP win in
 * [#176]).
 *
 * Since [#181] this island also hosts the mobile-nav drawer (a Base UI
 * `Dialog`-backed `Sheet`) so FAQ / Glossary / Pricing stay reachable from a
 * phone. That pulls the Dialog runtime into this one island's bundle — a few KB
 * gzipped of app-shared code already loaded by the dashboard shell and HTTP-
 * cached across routes. It does NOT mount until the hamburger is tapped
 * (`Dialog.Portal` is gated on `open`), so INP is unaffected; the cost is First
 * Load JS only. If that ever matters, the Sheet can be `next/dynamic`-imported
 * so Dialog stays out of the marketing First Load entirely.
 *
 * `skipRefresh` keeps an anonymous visitor's auth probe to a single round-trip
 * (no automatic POST /api/auth/refresh on the 401) — see useAuth.ts (#30).
 *
 * Colors use the design-system tokens from globals.css (`primary` = sky,
 * `accent` = CTA orange, `foreground` = navy) rather than hardcoded hex, so the
 * marketing chrome stays in sync with the rest of the app by construction.
 */
import { useState } from "react";
import Link from "next/link";
import { Menu, X } from "lucide-react";
import { buttonVariants } from "@/components/ui/button";
import { Logo } from "@/components/Logo";
import { cn } from "@/lib/utils";
import { useMe } from "@/hooks/useAuth";
import {
  Sheet,
  SheetClose,
  SheetContent,
  SheetTitle,
  SheetTrigger,
} from "@/components/ui/sheet";

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
  // Drives the mobile menu drawer (below md). Desktop keeps the inline nav and
  // never opens the Sheet. (#181)
  const [menuOpen, setMenuOpen] = useState(false);

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
              {/* Log in collapses into the mobile drawer below md to keep the
                  bar from overflowing a ~390px phone; Get started (the
                  conversion CTA) stays visible at every width. */}
              <Link
                href="/login"
                className="hidden rounded-lg px-3 py-2 text-sm font-semibold text-foreground transition-opacity duration-200 hover:opacity-70 md:inline-block"
              >
                Log in
              </Link>
              <Link href="/register" className={ctaClass}>
                Get started
              </Link>
            </>
          )}

          {/* Mobile menu — exposes the secondary nav (and Log in) that are
              hidden below md, so a cold-email click on a phone can still reach
              FAQ / Glossary / Pricing and sign in. (#181) */}
          <Sheet open={menuOpen} onOpenChange={setMenuOpen}>
            <SheetTrigger
              aria-label="Open menu"
              className="inline-flex size-11 items-center justify-center rounded-lg text-foreground hover:bg-muted md:hidden"
            >
              <Menu className="h-6 w-6" />
            </SheetTrigger>
            <SheetContent side="right" className="p-6">
              <div className="flex items-center justify-between">
                <SheetTitle className="text-base font-semibold text-foreground">
                  Menu
                </SheetTitle>
                <SheetClose
                  aria-label="Close menu"
                  className="inline-flex size-11 items-center justify-center rounded-lg text-muted-foreground hover:bg-muted hover:text-foreground"
                >
                  <X className="h-5 w-5" />
                </SheetClose>
              </div>

              <nav aria-label="Site" className="mt-6 flex flex-col gap-1">
                {NAV_LINKS.map((link) => (
                  <Link
                    key={link.href}
                    href={link.href}
                    onClick={() => setMenuOpen(false)}
                    className="rounded-lg px-3 py-3 text-base font-medium text-foreground hover:bg-muted"
                  >
                    {link.label}
                  </Link>
                ))}
              </nav>

              <div className="mt-6 flex flex-col gap-3 border-t border-border pt-6">
                {authed ? (
                  <Link
                    href="/dashboard"
                    onClick={() => setMenuOpen(false)}
                    className={cn(ctaClass, "w-full")}
                  >
                    Go to dashboard
                  </Link>
                ) : (
                  <>
                    <Link
                      href="/login"
                      onClick={() => setMenuOpen(false)}
                      className="rounded-lg px-3 py-3 text-center text-base font-semibold text-foreground hover:bg-muted"
                    >
                      Log in
                    </Link>
                    <Link
                      href="/register"
                      onClick={() => setMenuOpen(false)}
                      className={cn(ctaClass, "w-full")}
                    >
                      Get started
                    </Link>
                  </>
                )}
              </div>
            </SheetContent>
          </Sheet>
        </nav>
      </div>
    </header>
  );
}

export default MarketingHeader;
