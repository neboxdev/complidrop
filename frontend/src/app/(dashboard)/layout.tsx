"use client";

import Link from "next/link";
import { usePathname, useRouter } from "next/navigation";
import { useEffect, useState } from "react";
import { LayoutDashboard, FileText, Users, Settings, LogOut, Bell, ClipboardList, Download, Menu, X, AlertTriangle, RotateCw } from "lucide-react";
import { useLogout, useMe, type Me } from "@/hooks/useAuth";
import { GENERIC_FALLBACK_MESSAGE } from "@/lib/api";
import { cn } from "@/lib/utils";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { Logo } from "@/components/Logo";
import { EmailVerificationBanner } from "@/components/EmailVerificationBanner";
import { Sheet, SheetClose, SheetContent, SheetTitle, SheetTrigger } from "@/components/ui/sheet";

const NAV = [
  { href: "/dashboard", label: "Dashboard", icon: LayoutDashboard },
  { href: "/documents", label: "Documents", icon: FileText },
  { href: "/vendors", label: "Vendors", icon: Users },
  { href: "/rules", label: "Requirements", icon: ClipboardList },
  { href: "/reminders", label: "Reminders", icon: Bell },
  { href: "/export", label: "Export", icon: Download },
  { href: "/settings", label: "Settings", icon: Settings },
];

// Hoisted to module scope (NOT declared inside DashboardLayout's render body)
// per the `react-hooks/static-components` rule — an inline component would
// reset state on every parent render and break DevTools. Both the desktop
// `<aside>` and the mobile drawer render the SAME data-driven nav from this
// single source, so they can never drift. `onNavigate` lets the drawer close
// itself when a link is tapped (the desktop sidebar passes nothing). (#181)
function SidebarNav({
  pathname,
  onNavigate,
}: {
  pathname: string;
  onNavigate?: () => void;
}) {
  return (
    <nav aria-label="Primary" className="flex-1 overflow-y-auto py-4 px-2 space-y-1">
      {NAV.map(({ href, label, icon: Icon }) => {
        const active = pathname === href || pathname.startsWith(`${href}/`);
        return (
          <Link
            key={href}
            href={href}
            onClick={onNavigate}
            aria-current={active ? "page" : undefined}
            className={cn(
              "flex items-center gap-3 px-4 py-2.5 rounded-md text-sm transition pointer-coarse:min-h-11",
              active ? "bg-sky-900 text-white" : "text-sky-200 hover:bg-sky-900/60 hover:text-white",
            )}
          >
            <Icon className="w-4 h-4 shrink-0" />
            {label}
          </Link>
        );
      })}
    </nav>
  );
}

// Full-screen neutral placeholder shown while the session is still loading,
// or while an explicit logout/expiry redirect to /login is in flight. Hoisted
// to module scope per the `react-hooks/static-components` rule.
function ShellLoading() {
  return (
    <div className="min-h-screen flex items-center justify-center text-slate-500 text-sm">
      Loading your workspace…
    </div>
  );
}

// Shown when the `/me` probe fails TRANSIENTLY (backend 5xx / network blip)
// with no cached session — a logged-in user must NOT be evicted to /login on
// a server outage, which would mask the outage as an auth problem and is
// scary for a compliance tool (#182). Keeps the user in the app with an
// in-shell Retry instead of redirecting. `message` is already jargon-free
// (api.ts sanitizes every ApiError to the server copy or GENERIC_FALLBACK_
// MESSAGE — never raw statusText / status codes / TypeErrors, per the
// frontend error-message policy in CLAUDE.md).
function ShellUnreachable({
  message,
  onRetry,
  isRetrying,
}: {
  message: string;
  onRetry: () => void;
  isRetrying: boolean;
}) {
  return (
    <div className="min-h-screen flex items-center justify-center bg-slate-50 px-6">
      <div className="text-center max-w-sm" role="alert">
        <AlertTriangle className="w-8 h-8 mx-auto text-rose-500" />
        <p className="mt-3 text-sm font-medium text-slate-800">Couldn&apos;t reach the server.</p>
        <p className="mt-1 text-xs text-slate-500">{message}</p>
        <Button
          variant="outline"
          size="sm"
          className="mt-4"
          onClick={onRetry}
          disabled={isRetrying}
        >
          <RotateCw className={cn("w-3.5 h-3.5 mr-1", isRetrying && "animate-spin")} />
          Retry
        </Button>
      </div>
    </div>
  );
}

function SidebarFooter({ me, onLogout }: { me: Me; onLogout: () => void }) {
  return (
    <div className="px-4 py-4 border-t border-sky-900 text-sm">
      <p className="font-medium text-sky-100 truncate">{me.organizationName}</p>
      <p className="text-xs text-sky-300 truncate">{me.email}</p>
      <div className="mt-2 flex items-center justify-between">
        <Badge className="bg-sky-800 text-sky-100 border-transparent capitalize">{me.plan}</Badge>
        <button
          onClick={onLogout}
          className="flex items-center gap-1 text-xs text-sky-300 hover:text-white pointer-coarse:min-h-11"
        >
          <LogOut className="w-3 h-3" /> Log out
        </button>
      </div>
    </div>
  );
}

export default function DashboardLayout({ children }: { children: React.ReactNode }) {
  const me = useMe();
  const logout = useLogout();
  const router = useRouter();
  const pathname = usePathname();
  const [navOpen, setNavOpen] = useState(false);

  // Redirect to /login ONLY on the explicit logged-out signal. useMe maps a
  // genuine 401 (after the api-client refresh attempt) to `null`; a transient
  // /me 500 or network failure instead leaves `me.data === undefined` with
  // `me.isError` set. Guarding on `=== null` (not `!me.data`) means a backend
  // blip no longer evicts a valid session mid-task (#182).
  useEffect(() => {
    if (me.data === null) router.replace("/login");
  }, [me.data, router]);

  // Explicit logout/expiry → the redirect above is in flight; show the neutral
  // placeholder (not the error card) so there's no flash before /login.
  if (me.data === null) return <ShellLoading />;

  // Transient failure with NO cached session (first-load 5xx / offline): keep
  // the user in the app with a Retry rather than bouncing them to /login. A
  // background refetch that errors while a prior session is cached keeps
  // `me.data` populated (TanStack retains last-good data), so it falls through
  // to the shell below and is unaffected.
  if (me.isError && me.data === undefined) {
    return (
      <ShellUnreachable
        message={me.error?.message?.trim() || GENERIC_FALLBACK_MESSAGE}
        onRetry={() => void me.refetch()}
        isRetrying={me.isFetching}
      />
    );
  }

  // Still loading the first /me with nothing cached yet.
  if (!me.data) return <ShellLoading />;

  const onLogout = () => logout.mutate(undefined, { onSuccess: () => router.push("/login") });

  return (
    <div className="min-h-screen grid grid-cols-1 md:[grid-template-columns:240px_1fr]">
      {/* Skip link: the first focusable element, visually hidden until focused,
          jumps keyboard users past the nav straight to the page content (#189). */}
      <a
        href="#main-content"
        className="sr-only focus:not-sr-only focus:absolute focus:left-4 focus:top-4 focus:z-50 focus:rounded-md focus:bg-sky-900 focus:px-4 focus:py-2 focus:text-sm focus:font-medium focus:text-white"
      >
        Skip to content
      </a>
      {/* Desktop sidebar — hidden below md, where the mobile top bar + drawer
          take over. */}
      <aside className="hidden md:flex bg-sky-950 text-sky-50 flex-col">
        <div className="px-6 py-5 flex items-center border-b border-sky-900">
          <Logo variant="reverse" height={32} />
        </div>
        <SidebarNav pathname={pathname} />
        <SidebarFooter me={me.data} onLogout={onLogout} />
      </aside>

      {/* Mobile top bar — sticky so the hamburger stays reachable while the
          page scrolls; only rendered below md. */}
      <header className="md:hidden sticky top-0 z-40 flex items-center justify-between bg-sky-950 text-sky-50 px-4 h-14">
        <Link href="/dashboard" className="flex items-center" aria-label="CompliDrop — dashboard">
          <Logo variant="reverse" height={28} />
        </Link>
        <Sheet open={navOpen} onOpenChange={setNavOpen}>
          <SheetTrigger
            aria-label="Open navigation menu"
            className="inline-flex size-11 items-center justify-center rounded-md text-sky-100 hover:bg-sky-900"
          >
            <Menu className="w-6 h-6" />
          </SheetTrigger>
          <SheetContent side="left" className="bg-sky-950 text-sky-50">
            <SheetTitle className="sr-only">Navigation</SheetTitle>
            <div className="px-6 py-5 flex items-center justify-between border-b border-sky-900">
              <Logo variant="reverse" height={28} />
              <SheetClose
                aria-label="Close navigation menu"
                className="inline-flex size-11 items-center justify-center rounded-md text-sky-300 hover:bg-sky-900 hover:text-white"
              >
                <X className="w-5 h-5" />
              </SheetClose>
            </div>
            <SidebarNav pathname={pathname} onNavigate={() => setNavOpen(false)} />
            <SidebarFooter me={me.data} onLogout={onLogout} />
          </SheetContent>
        </Sheet>
      </header>

      <main
        id="main-content"
        tabIndex={-1}
        className="bg-slate-50 min-h-screen min-w-0 focus:outline-none"
      >
        {/* Persistent until the signup email is confirmed (#184) — shown on
            every dashboard route so reminders can't silently dead-letter. */}
        {!me.data.emailVerified && <EmailVerificationBanner email={me.data.email} />}
        {children}
      </main>
    </div>
  );
}
