"use client";

import { useId, useMemo, useState } from "react";
import Link from "next/link";
import { toast } from "sonner";
import {
  Plus,
  ExternalLink,
  AlertTriangle,
  RotateCw,
  Search,
  ChevronLeft,
  ChevronRight,
} from "lucide-react";
import { Card, CardContent } from "@/components/ui/card";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { Badge } from "@/components/ui/badge";
import { Skeleton } from "@/components/ui/skeleton";
import { useVendors, useCreateVendor } from "@/hooks/useVendors";
import { VendorCoverageBadge } from "@/components/VendorCoverageBadge";
import { cn } from "@/lib/utils";
import { GENERIC_FALLBACK_MESSAGE } from "@/lib/api";
import { isAuthError } from "@/lib/query-client";
import { PageTip } from "@/components/onboarding/PageTip";
import { TIP_IDS } from "@/lib/onboarding";

const VENDOR_PAGE_SIZE = 25;

export default function VendorsPage() {
  const vendors = useVendors();
  const createVendor = useCreateVendor();
  const [name, setName] = useState("");
  const [email, setEmail] = useState("");
  // a11y: wire each label to its input via htmlFor + id so screen
  // readers announce the field context (#76).
  const nameId = useId();
  const emailId = useId();
  const emailErrId = useId();

  // Client-side search + pagination over the loaded vendor list (#187).
  // The /api/vendors endpoint returns the full array — kept that way because
  // the #186 VendorPicker type-ahead relies on having every vendor in hand —
  // and at SMB scale (tens of vendors) filtering/paging in the client is plenty.
  const [query, setQuery] = useState("");
  const [page, setPage] = useState(1);

  const all = useMemo(() => vendors.data ?? [], [vendors.data]);
  const filtered = useMemo(() => {
    const q = query.trim().toLowerCase();
    if (!q) return all;
    return all.filter(
      (v) =>
        v.name.toLowerCase().includes(q) ||
        (v.contactEmail?.toLowerCase().includes(q) ?? false),
    );
  }, [all, query]);

  const totalPages = Math.max(1, Math.ceil(filtered.length / VENDOR_PAGE_SIZE));
  // Derive the in-range page during render (no setState-in-effect): if the
  // loaded list shrank below the current page, `safePage` clamps it for the
  // slice + pager, and the next Prev/Next click re-bases from it.
  const safePage = Math.min(page, totalPages);
  const pageItems = filtered.slice((safePage - 1) * VENDOR_PAGE_SIZE, safePage * VENDOR_PAGE_SIZE);

  // Add-vendor form validation (FP-076 / FP-073): block an obviously-malformed email,
  // and warn (non-blocking) when the name duplicates an existing vendor.
  const trimmedName = name.trim();
  const trimmedEmail = email.trim();
  const emailInvalid = trimmedEmail !== "" && !/^[^\s@]+@[^\s@]+\.[^\s@]+$/.test(trimmedEmail);
  const duplicateName =
    trimmedName !== "" && all.some((v) => v.name.trim().toLowerCase() === trimmedName.toLowerCase());

  return (
    <div className="max-w-6xl mx-auto px-6 py-8 space-y-6">
      <header>
        <h1 className="text-2xl font-semibold text-sky-900">Vendors</h1>
        <p className="text-slate-500">Manage your vendors and their compliance documents.</p>
      </header>

      <PageTip id={TIP_IDS.vendors} title="Start with a vendor">
        Add each business you work with, then open it to pick what it must prove. We check every
        document it sends against that checklist.
      </PageTip>

      <Card>
        <CardContent className="p-5">
          {/* A real <form> so Enter submits (FP-076); type="email" + a light format check
              so a typo'd address isn't accepted silently, and a duplicate-name hint (FP-073). */}
          <form
            className="flex flex-col gap-3 sm:flex-row sm:items-end"
            onSubmit={async (e) => {
              e.preventDefault();
              if (!trimmedName || emailInvalid) return;
              try {
                await createVendor.mutateAsync({ name: trimmedName, contactEmail: trimmedEmail || null });
                toast.success("Vendor added");
                setName("");
                setEmail("");
              } catch (err) {
                toast.error(err instanceof Error ? err.message : "Failed to add vendor");
              }
            }}
          >
            <div className="flex-1">
              <label htmlFor={nameId} className="text-xs text-slate-500">Name</label>
              <Input id={nameId} value={name} onChange={(e) => setName(e.target.value)} placeholder="Acme Catering" autoComplete="organization" />
              {duplicateName && (
                <p className="mt-1 text-xs text-amber-700">You already have a vendor named “{trimmedName}”.</p>
              )}
            </div>
            <div className="flex-1">
              <label htmlFor={emailId} className="text-xs text-slate-500">Contact email</label>
              <Input
                id={emailId}
                type="email"
                inputMode="email"
                autoComplete="email"
                value={email}
                onChange={(e) => setEmail(e.target.value)}
                placeholder="ops@acmecatering.com"
                aria-invalid={emailInvalid || undefined}
                aria-describedby={emailInvalid ? emailErrId : undefined}
              />
              {emailInvalid && (
                <p id={emailErrId} className="mt-1 text-xs text-red-600">Enter a valid email address.</p>
              )}
            </div>
            <Button type="submit" className="w-full sm:w-auto" disabled={!trimmedName || emailInvalid || createVendor.isPending}>
              <Plus className="w-4 h-4 mr-1" /> Add vendor
            </Button>
          </form>
        </CardContent>
      </Card>

      {all.length > 0 && (
        <div className="relative max-w-sm">
          <Search className="pointer-events-none absolute left-2.5 top-1/2 h-4 w-4 -translate-y-1/2 text-slate-400" />
          <Input
            aria-label="Search vendors"
            placeholder="Search by name or email…"
            value={query}
            onChange={(e) => {
              setQuery(e.target.value);
              setPage(1);
            }}
            className="pl-8"
          />
        </div>
      )}

      <Card>
        <CardContent className="p-0 overflow-x-auto">
          <table className="stacked-table w-full text-sm">
            <thead className="bg-slate-50 text-xs uppercase text-slate-500">
              <tr>
                <th className="px-4 py-3 text-left">Vendor</th>
                <th className="px-4 py-3 text-left">Requirements</th>
                <th className="px-4 py-3 text-left">Status</th>
                <th className="px-4 py-3 text-left">Docs</th>
                <th className="px-4 py-3 text-left">Active links</th>
                <th />
              </tr>
            </thead>
            <tbody>
              {vendors.isLoading ? (
                // Skeleton rows that mirror the table layout so the page reserves
                // its height instead of shifting when vendors land. (#197)
                [0, 1, 2].map((i) => (
                  <tr
                    key={i}
                    data-testid={i === 0 ? "vendors-loading" : undefined}
                    className="border-t border-slate-100"
                  >
                    <td className="px-4 py-4"><Skeleton className="h-4 w-40" /></td>
                    <td className="px-4 py-4"><Skeleton className="h-4 w-28" /></td>
                    <td className="px-4 py-4"><Skeleton className="h-4 w-20" /></td>
                    <td className="px-4 py-4"><Skeleton className="h-4 w-8" /></td>
                    <td className="px-4 py-4"><Skeleton className="h-4 w-8" /></td>
                    <td className="px-4 py-4"><Skeleton className="h-4 w-16" /></td>
                  </tr>
                ))
              ) : vendors.isError && all.length === 0 && !isAuthError(vendors.error) ? (
                // Error state distinct from empty so a backend outage is
                // not mistaken for an org with zero vendors (#80). Gate
                // on `length === 0` so a transient failure does NOT
                // clobber a previously-populated list. (Symmetric with
                // documents/page.tsx — see #80 followup review.) The
                // `!isAuthError` guard routes an expired-session 401 to the
                // global redirect (lib/query-client.ts) instead of this card.
                //
                // `err.message` is the human server message; api.ts's
                // GENERIC_FALLBACK_MESSAGE kicks in when the body is
                // non-JSON or fetch rejected (#77).
                //
                // role="alert" gets the error announced by assistive
                // tech the moment isError flips true, matching the
                // convention in frontend/src/test/example.test.tsx.
                <tr>
                  <td colSpan={6} className="py-12 text-center" role="alert">
                    <AlertTriangle className="w-8 h-8 mx-auto text-rose-500" />
                    <p className="mt-2 text-sm font-medium text-slate-800">
                      Couldn&apos;t load vendors.
                    </p>
                    <p className="text-xs text-slate-500">
                      {vendors.error?.message?.trim() || GENERIC_FALLBACK_MESSAGE}
                    </p>
                    <Button
                      variant="outline"
                      size="sm"
                      className="mt-3"
                      onClick={() => vendors.refetch()}
                      disabled={vendors.isFetching}
                    >
                      <RotateCw
                        className={cn(
                          "w-3.5 h-3.5 mr-1",
                          vendors.isFetching && "animate-spin",
                        )}
                      />
                      Retry
                    </Button>
                  </td>
                </tr>
              ) : all.length === 0 ? (
                <tr><td colSpan={6} className="py-10 text-center text-slate-500">No vendors yet — add your first one above to start tracking their COIs and licenses.</td></tr>
              ) : filtered.length === 0 ? (
                <tr><td colSpan={6} className="py-10 text-center text-slate-500">No vendors match your search.</td></tr>
              ) : pageItems.map((v) => (
                <tr key={v.id} className="border-t border-slate-100">
                  <td className="px-4 py-3">
                    <span className="inline-flex items-center gap-2">
                      <Link href={`/vendors/${v.id}`} className="text-sky-700 font-medium hover:underline">{v.name}</Link>
                      {v.isSample && (
                        <Badge className="bg-sky-100 text-sky-700 border-transparent text-[10px] uppercase tracking-wide">
                          Sample
                        </Badge>
                      )}
                    </span>
                    {v.contactEmail && (
                      <p className="text-xs text-slate-500">
                        {v.contactEmail}
                        {/* #340: a dead/opted-out contact email is scannable in the list — reminders to it
                            are paused. The detail page explains it and how to resume. */}
                        {v.contactEmailStatus && (
                          <Badge className="ml-2 bg-amber-100 text-amber-800 border-transparent text-[10px] uppercase tracking-wide">
                            {v.contactEmailStatus === "complained" ? "Spam report" : "Bounced"}
                          </Badge>
                        )}
                      </p>
                    )}
                  </td>
                  <td data-label="Requirements" className="px-4 py-3 text-slate-600">
                    {v.complianceTemplateName ?? (
                      <Link href={`/vendors/${v.id}`} className="text-sky-700 hover:underline">
                        Set requirements
                      </Link>
                    )}
                  </td>
                  <td data-label="Status" className="px-4 py-3">
                    {/* FP-074: who is NOT ok? Covered / Action needed / Missing: <types>. For a
                        vendor with no checklist the Requirements column already links to set one. */}
                    {v.coverage.status === "NoRequirements" ? (
                      <span className="text-xs text-slate-400">—</span>
                    ) : (
                      <VendorCoverageBadge coverage={v.coverage} />
                    )}
                  </td>
                  <td data-label="Docs" className="px-4 py-3 text-slate-600">
                    {/* FP-071: the count links to this vendor's filtered documents. */}
                    {v.documentCount > 0 ? (
                      <Link href={`/documents?vendor=${v.id}`} className="text-sky-700 hover:underline">
                        {v.documentCount}
                      </Link>
                    ) : (
                      v.documentCount
                    )}
                  </td>
                  <td data-label="Active links" className="px-4 py-3">
                    {v.activePortalLinks > 0 ? (
                      <Badge className="bg-emerald-100 text-emerald-700 border-transparent">
                        {v.activePortalLinks} active
                      </Badge>
                    ) : (
                      <span className="text-xs text-slate-500">None</span>
                    )}
                  </td>
                  <td className="px-4 py-3 text-right">
                    <Link href={`/vendors/${v.id}`} className="text-sm text-sky-700 hover:underline inline-flex items-center">
                      Manage <ExternalLink className="w-3 h-3 ml-1" />
                    </Link>
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        </CardContent>
      </Card>

      {filtered.length > 0 && (
        <div className="flex items-center justify-between text-sm text-slate-600">
          <span>
            Page {safePage} of {totalPages} · {filtered.length} vendor{filtered.length === 1 ? "" : "s"}
          </span>
          <div className="flex items-center gap-2">
            <Button
              type="button"
              variant="outline"
              size="sm"
              disabled={safePage <= 1}
              onClick={() => setPage(Math.max(1, safePage - 1))}
            >
              <ChevronLeft className="mr-1 h-4 w-4" /> Prev
            </Button>
            <Button
              type="button"
              variant="outline"
              size="sm"
              disabled={safePage >= totalPages}
              onClick={() => setPage(Math.min(totalPages, safePage + 1))}
            >
              Next <ChevronRight className="ml-1 h-4 w-4" />
            </Button>
          </div>
        </div>
      )}
    </div>
  );
}
