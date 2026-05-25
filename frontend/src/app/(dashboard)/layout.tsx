"use client";

import Link from "next/link";
import { usePathname, useRouter } from "next/navigation";
import { useEffect } from "react";
import { LayoutDashboard, FileText, Users, Settings, LogOut, Bell, ClipboardList, Download } from "lucide-react";
import { useLogout, useMe } from "@/hooks/useAuth";
import { cn } from "@/lib/utils";
import { Badge } from "@/components/ui/badge";
import { Logo } from "@/components/Logo";

const NAV = [
  { href: "/dashboard", label: "Dashboard", icon: LayoutDashboard },
  { href: "/documents", label: "Documents", icon: FileText },
  { href: "/vendors", label: "Vendors", icon: Users },
  { href: "/rules", label: "Compliance rules", icon: ClipboardList },
  { href: "/reminders", label: "Reminders", icon: Bell },
  { href: "/export", label: "Export", icon: Download },
  { href: "/settings", label: "Settings", icon: Settings },
];

export default function DashboardLayout({ children }: { children: React.ReactNode }) {
  const me = useMe();
  const logout = useLogout();
  const router = useRouter();
  const pathname = usePathname();

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

  return (
    <div className="min-h-screen grid" style={{ gridTemplateColumns: "240px 1fr" }}>
      <aside className="bg-sky-950 text-sky-50 flex flex-col">
        <div className="px-6 py-5 flex items-center border-b border-sky-900">
          <Logo variant="reverse" height={32} />
        </div>
        <nav className="flex-1 py-4 px-2 space-y-1">
          {NAV.map(({ href, label, icon: Icon }) => {
            const active = pathname === href || pathname.startsWith(`${href}/`);
            return (
              <Link
                key={href}
                href={href}
                className={cn(
                  "flex items-center gap-3 px-4 py-2 rounded-md text-sm transition",
                  active ? "bg-sky-900 text-white" : "text-sky-200 hover:bg-sky-900/60 hover:text-white",
                )}
              >
                <Icon className="w-4 h-4" />
                {label}
              </Link>
            );
          })}
        </nav>
        <div className="px-4 py-4 border-t border-sky-900 text-sm">
          <p className="font-medium text-sky-100 truncate">{me.data.organizationName}</p>
          <p className="text-xs text-sky-300 truncate">{me.data.email}</p>
          <div className="mt-2 flex items-center justify-between">
            <Badge className="bg-sky-800 text-sky-100 border-transparent capitalize">{me.data.plan}</Badge>
            <button
              onClick={() => logout.mutate(undefined, { onSuccess: () => router.push("/login") })}
              className="flex items-center gap-1 text-xs text-sky-300 hover:text-white"
            >
              <LogOut className="w-3 h-3" /> Log out
            </button>
          </div>
        </div>
      </aside>
      <main className="bg-slate-50 min-h-screen">{children}</main>
    </div>
  );
}
