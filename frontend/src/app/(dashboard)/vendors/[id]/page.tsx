"use client";

import { useParams } from "next/navigation";
import Link from "next/link";
import { toast } from "sonner";
import { ArrowLeft, Copy, Link as LinkIcon, XCircle } from "lucide-react";
import { Card, CardContent } from "@/components/ui/card";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { Badge } from "@/components/ui/badge";
import {
  useVendor,
  useGeneratePortalLink,
  useRevokePortalLink,
  useUpdateVendor,
} from "@/hooks/useVendors";
import { useQuery } from "@tanstack/react-query";
import { api } from "@/lib/api";
import { useState, useEffect } from "react";

type TemplateSummary = { id: string; name: string; isSystemTemplate: boolean };

export default function VendorDetailPage() {
  const params = useParams<{ id: string }>();
  const vendor = useVendor(params.id);
  const update = useUpdateVendor(params.id);
  const generate = useGeneratePortalLink(params.id);
  const revoke = useRevokePortalLink(params.id);

  const templates = useQuery<TemplateSummary[]>({
    queryKey: ["templates"],
    queryFn: () => api.get<TemplateSummary[]>("/api/compliance/templates"),
  });

  const [form, setForm] = useState({
    name: "",
    contactEmail: "",
    contactPhone: "",
    category: "",
    complianceTemplateId: "",
  });

  useEffect(() => {
    if (vendor.data) {
      setForm({
        name: vendor.data.name,
        contactEmail: vendor.data.contactEmail ?? "",
        contactPhone: vendor.data.contactPhone ?? "",
        category: vendor.data.category ?? "",
        complianceTemplateId: vendor.data.complianceTemplateId ?? "",
      });
    }
  }, [vendor.data]);

  if (vendor.isLoading || !vendor.data) {
    return <div className="p-8 text-sm text-slate-400">Loading vendor…</div>;
  }

  return (
    <div className="max-w-5xl mx-auto px-6 py-8 space-y-6">
      <Link href="/vendors" className="inline-flex items-center gap-1 text-sm text-sky-700">
        <ArrowLeft className="w-4 h-4" /> All vendors
      </Link>

      <Card>
        <CardContent className="p-6 space-y-4">
          <h1 className="text-xl font-semibold text-sky-900">{vendor.data.name}</h1>
          <div className="grid grid-cols-2 gap-4 text-sm">
            <LabeledInput label="Name" value={form.name} onChange={(v) => setForm({ ...form, name: v })} />
            <LabeledInput label="Contact email" value={form.contactEmail} onChange={(v) => setForm({ ...form, contactEmail: v })} />
            <LabeledInput label="Contact phone" value={form.contactPhone} onChange={(v) => setForm({ ...form, contactPhone: v })} />
            <LabeledInput label="Category" value={form.category} onChange={(v) => setForm({ ...form, category: v })} />
            <div>
              <label className="text-xs text-slate-500">Compliance template</label>
              <select
                value={form.complianceTemplateId}
                onChange={(e) => setForm({ ...form, complianceTemplateId: e.target.value })}
                className="mt-1 w-full border border-slate-200 rounded-md h-9 px-2 text-sm"
              >
                <option value="">— none —</option>
                {(templates.data ?? []).map((t) => (
                  <option key={t.id} value={t.id}>{t.name}</option>
                ))}
              </select>
            </div>
          </div>
          <div className="flex justify-end">
            <Button
              onClick={async () => {
                try {
                  await update.mutateAsync({
                    name: form.name.trim(),
                    contactEmail: form.contactEmail || null,
                    contactPhone: form.contactPhone || null,
                    category: form.category || null,
                    complianceTemplateId: form.complianceTemplateId || null,
                  });
                  toast.success("Vendor updated");
                } catch (err) {
                  toast.error(err instanceof Error ? err.message : "Failed to update vendor");
                }
              }}
              disabled={update.isPending}
            >
              Save changes
            </Button>
          </div>
        </CardContent>
      </Card>

      <Card>
        <CardContent className="p-6 space-y-4">
          <div className="flex items-center justify-between">
            <div>
              <h2 className="font-semibold text-slate-800">Portal upload links</h2>
              <p className="text-xs text-slate-500">Share a link with {vendor.data.name} — they upload with no login.</p>
            </div>
            <Button
              onClick={async () => {
                try {
                  const link = await generate.mutateAsync();
                  await navigator.clipboard.writeText(link.url);
                  toast.success("Link copied to clipboard");
                } catch (err) {
                  toast.error(err instanceof Error ? err.message : "Failed to generate link");
                }
              }}
              disabled={generate.isPending}
            >
              <LinkIcon className="w-4 h-4 mr-1" /> Generate upload link
            </Button>
          </div>

          {vendor.data.portalLinks.length === 0 ? (
            <p className="text-sm text-slate-500">No links yet — generate one above.</p>
          ) : (
            <ul className="space-y-2">
              {vendor.data.portalLinks.map((l) => (
                <li key={l.id} className="flex items-center gap-3 px-3 py-2 rounded-md border border-slate-100 bg-slate-50/50">
                  <Input value={l.fullUrl} readOnly className="flex-1 h-8 font-mono text-xs" />
                  <Button
                    size="sm"
                    variant="outline"
                    onClick={async () => {
                      await navigator.clipboard.writeText(l.fullUrl);
                      toast.success("Copied");
                    }}
                  >
                    <Copy className="w-3 h-3" />
                  </Button>
                  <Badge className={l.isActive ? "bg-emerald-100 text-emerald-700 border-transparent" : "bg-slate-100 text-slate-600 border-transparent"}>
                    {l.isActive ? "active" : "inactive"}
                  </Badge>
                  <span className="text-xs text-slate-500">{l.uploadCount}/{l.maxUploads} uploads</span>
                  {l.isActive && (
                    <Button size="sm" variant="ghost" onClick={() => revoke.mutate(l.id)}>
                      <XCircle className="w-4 h-4 text-slate-400 hover:text-rose-600" />
                    </Button>
                  )}
                </li>
              ))}
            </ul>
          )}
        </CardContent>
      </Card>
    </div>
  );
}

function LabeledInput({ label, value, onChange }: { label: string; value: string; onChange: (v: string) => void }) {
  return (
    <div>
      <label className="text-xs text-slate-500">{label}</label>
      <Input value={value} onChange={(e) => onChange(e.target.value)} className="mt-1" />
    </div>
  );
}
