"use client";

import { useId, useState } from "react";
import Link from "next/link";
import { toast } from "sonner";
import { Plus, ExternalLink, AlertTriangle, RotateCw } from "lucide-react";
import { Card, CardContent } from "@/components/ui/card";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { Badge } from "@/components/ui/badge";
import { useVendors, useCreateVendor } from "@/hooks/useVendors";
import { cn } from "@/lib/utils";
import { GENERIC_FALLBACK_MESSAGE } from "@/lib/api";
import { isAuthError } from "@/lib/query-client";

export default function VendorsPage() {
  const vendors = useVendors();
  const createVendor = useCreateVendor();
  const [name, setName] = useState("");
  const [email, setEmail] = useState("");
  // a11y: wire each label to its input via htmlFor + id so screen
  // readers announce the field context (#76).
  const nameId = useId();
  const emailId = useId();

  return (
    <div className="max-w-6xl mx-auto px-6 py-8 space-y-6">
      <header>
        <h1 className="text-2xl font-semibold text-sky-900">Vendors</h1>
        <p className="text-slate-500">Manage subcontractors and their compliance documents.</p>
      </header>

      <Card>
        <CardContent className="p-5 flex flex-col sm:flex-row gap-3 sm:items-end">
          <div className="flex-1">
            <label htmlFor={nameId} className="text-xs text-slate-500">Name</label>
            <Input id={nameId} value={name} onChange={(e) => setName(e.target.value)} placeholder="Mike's Electrical" />
          </div>
          <div className="flex-1">
            <label htmlFor={emailId} className="text-xs text-slate-500">Contact email</label>
            <Input id={emailId} value={email} onChange={(e) => setEmail(e.target.value)} placeholder="mike@acme.com" />
          </div>
          <Button
            className="w-full sm:w-auto"
            disabled={!name || createVendor.isPending}
            onClick={async () => {
              try {
                await createVendor.mutateAsync({ name, contactEmail: email || null });
                toast.success("Vendor added");
                setName("");
                setEmail("");
              } catch (err) {
                toast.error(err instanceof Error ? err.message : "Failed to add vendor");
              }
            }}
          >
            <Plus className="w-4 h-4 mr-1" /> Add vendor
          </Button>
        </CardContent>
      </Card>

      <Card>
        <CardContent className="p-0 overflow-x-auto">
          <table className="stacked-table w-full text-sm">
            <thead className="bg-slate-50 text-xs uppercase text-slate-500">
              <tr>
                <th className="px-4 py-3 text-left">Vendor</th>
                <th className="px-4 py-3 text-left">Template</th>
                <th className="px-4 py-3 text-left">Docs</th>
                <th className="px-4 py-3 text-left">Active links</th>
                <th />
              </tr>
            </thead>
            <tbody>
              {vendors.isLoading ? (
                <tr><td colSpan={5} className="py-8 text-center text-slate-400">Loading…</td></tr>
              ) : vendors.isError && (vendors.data ?? []).length === 0 && !isAuthError(vendors.error) ? (
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
                  <td colSpan={5} className="py-12 text-center" role="alert">
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
              ) : (vendors.data ?? []).length === 0 ? (
                <tr><td colSpan={5} className="py-10 text-center text-slate-500">No vendors yet.</td></tr>
              ) : (vendors.data ?? []).map((v) => (
                <tr key={v.id} className="border-t border-slate-100">
                  <td className="px-4 py-3">
                    <Link href={`/vendors/${v.id}`} className="text-sky-700 font-medium hover:underline">{v.name}</Link>
                    {v.contactEmail && <p className="text-xs text-slate-500">{v.contactEmail}</p>}
                  </td>
                  <td data-label="Template" className="px-4 py-3 text-slate-600">{v.complianceTemplateName ?? "—"}</td>
                  <td data-label="Docs" className="px-4 py-3 text-slate-600">{v.documentCount}</td>
                  <td data-label="Active links" className="px-4 py-3">
                    {v.activePortalLinks > 0 ? (
                      <Badge className="bg-emerald-100 text-emerald-700 border-transparent">
                        {v.activePortalLinks} active
                      </Badge>
                    ) : (
                      <span className="text-xs text-slate-400">None</span>
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
    </div>
  );
}
