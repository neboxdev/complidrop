"use client";

import Link from "next/link";
import { usePathname, useRouter } from "next/navigation";
import { useEffect, useState } from "react";
import { LayoutDashboard, FileText, Users, Settings, LogOut, Bell, ClipboardList, Download, Menu, X } from "lucide-react";
import { useLogout, useMe, type Me } from "@/hooks/useAuth";
import { cn } from "@/lib/utils";
import { Badge } from "@/components/ui/badge";
import { Logo } from "@/components/Logo";
import { Sheet, SheetClose, SheetContent, SheetTitle, SheetTrigger } from "@/components/ui/sheet";

const NAV = [
  { href: "/dashboard", label: "Dashboard", icon: LayoutDashboard },
  { href: "/documents", label: "Documents", icon: FileText },
  { href: "/vendors", label: "Vendors", icon: Users },
  { href: "/rules", label: "Compliance rules", icon: ClipboardList },
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

  useEffect(() => {
    if (me.isLoading) return;
    if (!me.data) router.replace("/login");
  }, [me.isLoading, me.data, router]);

  if (me.isLoading || !me.data) {
    return (
      <div className="min-h-screen flex items-center justify-center text-slate-400 text-sm">
        Loading your workspace…
      </div>
    );
  }

  const onLogout = () => logout.mutate(undefined, { onSuccess: () => router.push("/login") });

  return (
    <div className="min-h-screen grid grid-cols-1 md:[grid-template-columns:240px_1fr]">
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

      <main className="bg-slate-50 min-h-screen min-w-0">{children}</main>
    </div>
  );
}
