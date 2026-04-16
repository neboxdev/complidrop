import Link from "next/link";
import { ShieldCheck } from "lucide-react";

export default function AuthLayout({ children }: { children: React.ReactNode }) {
  return (
    <div className="min-h-screen flex flex-col bg-sky-50/50">
      <header className="border-b border-sky-100 bg-white">
        <div className="max-w-6xl mx-auto px-6 py-4 flex items-center justify-between">
          <Link href="/" className="flex items-center gap-2 font-semibold text-sky-900">
            <ShieldCheck className="w-6 h-6 text-sky-500" />
            <span>CompliDrop</span>
          </Link>
          <div className="flex items-center gap-4 text-sm text-slate-500">
            <Link href="/login" className="hover:text-sky-700">Log in</Link>
            <Link href="/register" className="hover:text-sky-700">Sign up</Link>
          </div>
        </div>
      </header>
      <main className="flex-1 flex items-center justify-center px-4 py-12">
        <div className="w-full max-w-md">{children}</div>
      </main>
    </div>
  );
}
