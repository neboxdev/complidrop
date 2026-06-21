"use client";

import { useParams } from "next/navigation";
import Link from "next/link";
import { toast } from "sonner";
import { ArrowLeft, AlertTriangle, Check, Copy, Link as LinkIcon, Mail, XCircle } from "lucide-react";
import { Card, CardContent } from "@/components/ui/card";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { Badge } from "@/components/ui/badge";
import { requirementSentence } from "@/lib/requirements";
import {
  useVendor,
  useGeneratePortalLink,
  useEmailPortalLink,
  useRevokePortalLink,
  useUpdateVendor,
  type VendorDetail,
} from "@/hooks/useVendors";
import { useSubscription } from "@/hooks/useSubscription";
import { useQuery } from "@tanstack/react-query";
import { api, GENERIC_FALLBACK_MESSAGE } from "@/lib/api";
import { useId, useState } from "react";

type TemplateSummary = { id: string; name: string; isSystemTemplate: boolean };

type TemplateRule = {
  id: string;
  documentType: string;
  fieldName: string | null;
  operator: string;
  expectedValue: string | null;
  errorMessage: string | null;
  sortOrder: number;
};

type TemplateDetail = { id: string; name: string; isSystemTemplate: boolean; rules: TemplateRule[] };

export default function VendorDetailPage() {
  const params = useParams<{ id: string }>();
  const vendor = useVendor(params.id);

  if (vendor.isLoading || !vendor.data) {
    return <div className="p-8 text-sm text-slate-500">Loading vendor…</div>;
  }

  // Re-keying on id resets child useState when navigating between vendors, so
  // we can initialise form state from props directly — no useEffect+setState.
  return <VendorDetailContent key={vendor.data.id} vendor={vendor.data} vendorId={params.id} />;
}

function VendorDetailContent({ vendor, vendorId }: { vendor: VendorDetail; vendorId: string }) {
  const update = useUpdateVendor(vendorId);
  const generate = useGeneratePortalLink(vendorId);
  const emailLink = useEmailPortalLink(vendorId);
  const revoke = useRevokePortalLink(vendorId);

  const templates = useQuery<TemplateSummary[]>({
    queryKey: ["templates"],
    queryFn: ({ signal }) => api.get<TemplateSummary[]>("/api/compliance/templates", { signal }),
  });

  // Gate the "Email link" button on the SAVED contact email (vendor.contactEmail),
  // not the editable form field — the server emails the persisted address, so an
  // unsaved edit must not enable the button. The detail re-renders with a fresh
  // vendor prop after a save invalidates the query, flipping this on once saved.
  const savedEmail = vendor.contactEmail?.trim() ?? "";
  const hasContactEmail = savedEmail.length > 0;
  const linkActionPending = generate.isPending || emailLink.isPending;

  // Plan gate (#261): upload links are a Pro entitlement — the server 403s link
  // generation/emailing for plans without it. Gate the affordances proactively so
  // a Free user gets an upgrade path instead of a rejection toast. Only an explicit
  // `false` gates: while the subscription is loading (undefined) the buttons stay
  // enabled — the server is the real fence, and briefly-enabled is better than
  // flashing a "Pro feature" notice at every Pro user on a cold cache.
  const subscription = useSubscription();
  const portalGated = subscription.data?.hasVendorPortal === false;

  // Resolve the link to act on: reuse the vendor's most recent ACTIVE link if one exists
  // (portalLinks is ordered newest-first by the server), otherwise mint a fresh one. Reusing
  // avoids spawning a throwaway link on every Email/Copy click, and an active link always has
  // remaining quota — the portal auto-deactivates a link once it hits MaxUploads, so a still-
  // active link is never exhausted.
  async function resolveLink(): Promise<{ id: string; url: string }> {
    const active = vendor.portalLinks.find((l) => l.isActive);
    if (active) return { id: active.id, url: active.fullUrl };
    const created = await generate.mutateAsync();
    return { id: created.id, url: created.url };
  }

  // "Email link to {vendor}" = resolve the upload link, then email it in one click.
  async function emailLinkToVendor() {
    try {
      const link = await resolveLink();
      await emailLink.mutateAsync(link.id);
      toast.success(`Upload link emailed to ${savedEmail}`);
    } catch (err) {
      toast.error(err instanceof Error ? err.message : GENERIC_FALLBACK_MESSAGE);
    }
  }

  // Secondary path: copy. Pat lives in email, so the toast nudges them to paste it into a
  // message rather than implying the job is done (#190).
  async function copyLinkToClipboard() {
    let link: { id: string; url: string };
    try {
      link = await resolveLink(); // API error → friendly server message (api.ts guarantees it)
    } catch (err) {
      toast.error(err instanceof Error ? err.message : GENERIC_FALLBACK_MESSAGE);
      return;
    }
    try {
      await navigator.clipboard.writeText(link.url);
      toast.success(`Link copied — now paste it into an email to ${vendor.name}.`);
    } catch {
      // Clipboard rejections are raw browser errors (TypeError "not focused", denied
      // permission) — never user-friendly — so emit the generic fallback rather than the
      // raw message, per the CLAUDE.md frontend error-message policy.
      toast.error(GENERIC_FALLBACK_MESSAGE);
    }
  }

  const [form, setForm] = useState({
    name: vendor.name,
    contactEmail: vendor.contactEmail ?? "",
    contactPhone: vendor.contactPhone ?? "",
    category: vendor.category ?? "",
    complianceTemplateId: vendor.complianceTemplateId ?? "",
  });
  // a11y: each form control gets its own id so labels can wire via
  // htmlFor (#76). LabeledInput owns its own useId; the select below
  // needs its id at this level so the label can reference it.
  const templateSelectId = useId();

  // Show what the chosen checklist checks AT DECISION TIME (#239 delta 1) — the
  // highest-leverage gap the #237 audit found: Pat used to assign a checklist on
  // faith, with nothing on the path ever revealing what it requires. Keyed on the
  // (possibly unsaved) selected id so it updates the moment she picks, and shares
  // the ["templates", id] cache with the /rules detail view.
  const selectedTemplate = useQuery<TemplateDetail>({
    queryKey: ["templates", form.complianceTemplateId],
    queryFn: ({ signal }) =>
      api.get<TemplateDetail>(`/api/compliance/templates/${form.complianceTemplateId}`, { signal }),
    enabled: form.complianceTemplateId !== "",
  });

  return (
    <div className="max-w-5xl mx-auto px-6 py-8 space-y-6">
      <Link href="/vendors" className="inline-flex items-center gap-1 text-sm text-sky-700">
        <ArrowLeft className="w-4 h-4" /> All vendors
      </Link>

      <Card>
        <CardContent className="p-6 space-y-4">
          <h1 className="text-xl font-semibold text-sky-900">{vendor.name}</h1>
          <div className="grid grid-cols-1 sm:grid-cols-2 gap-4 text-sm">
            <LabeledInput label="Name" value={form.name} onChange={(v) => setForm({ ...form, name: v })} />
            <LabeledInput label="Contact email" value={form.contactEmail} onChange={(v) => setForm({ ...form, contactEmail: v })} />
            <LabeledInput label="Contact phone" value={form.contactPhone} onChange={(v) => setForm({ ...form, contactPhone: v })} />
            <LabeledInput label="Category" value={form.category} onChange={(v) => setForm({ ...form, category: v })} />
            <div className="sm:col-span-2">
              <label htmlFor={templateSelectId} className="text-xs text-slate-500">What this vendor must prove</label>
              <select
                id={templateSelectId}
                value={form.complianceTemplateId}
                onChange={(e) => setForm({ ...form, complianceTemplateId: e.target.value })}
                className="mt-1 w-full border border-input rounded-md h-9 px-2 text-sm focus-visible:border-ring focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring"
              >
                <option value="">— No requirements set —</option>
                {(templates.data ?? []).map((t) => (
                  <option key={t.id} value={t.id}>{t.name}</option>
                ))}
              </select>
              <p className="mt-1 text-xs text-slate-500">
                Pick the checklist for their type — we check every document against it.
              </p>
              {form.complianceTemplateId === "" ? (
                <p
                  role="status"
                  className="mt-2 flex items-start gap-1.5 rounded-md border border-amber-200 bg-amber-50 px-2.5 py-2 text-xs text-amber-800"
                >
                  <AlertTriangle className="mt-0.5 h-3.5 w-3.5 shrink-0" aria-hidden="true" />
                  <span>
                    No requirements set — this vendor&apos;s documents won&apos;t be marked covered
                    or not until you choose one.
                  </span>
                </p>
              ) : (
                <div
                  role="status"
                  className="mt-2 rounded-md border border-emerald-100 bg-emerald-50/60 px-3 py-2.5 text-xs text-emerald-900"
                >
                  {selectedTemplate.isLoading || !selectedTemplate.data ? (
                    <span className="text-emerald-800/70">Loading what this checklist requires…</span>
                  ) : selectedTemplate.data.rules.length === 0 ? (
                    <span>
                      This checklist has no requirements yet — add some on the{" "}
                      <Link href="/rules" className="font-medium underline">
                        requirements page
                      </Link>
                      .
                    </span>
                  ) : (
                    <>
                      <p className="font-medium">We&apos;ll check every document for:</p>
                      <ul className="mt-1 space-y-1">
                        {[...selectedTemplate.data.rules]
                          .sort((a, b) => a.sortOrder - b.sortOrder)
                          .map((r) => (
                            <li key={r.id} className="flex gap-1.5">
                              <Check className="mt-0.5 h-3 w-3 shrink-0 text-emerald-600" aria-hidden="true" />
                              <span>{requirementSentence(r)}</span>
                            </li>
                          ))}
                      </ul>
                    </>
                  )}
                </div>
              )}
              {templates.data && templates.data.length === 0 && (
                <p className="mt-2 text-xs">
                  <Link href="/rules" className="text-sky-700 hover:underline">
                    Create a requirement checklist →
                  </Link>
                </p>
              )}
            </div>
          </div>
          <div className="flex items-center justify-end gap-3">
            {/* A blank name would render an invisible, unclickable row in the vendors
                list (the name is the row's link) — block it client-side with a visible
                reason; the server enforces the same 400 (#264 / FP-074). */}
            {!form.name.trim() && (
              <p role="status" className="text-xs text-rose-600">
                Vendor name is required.
              </p>
            )}
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
              disabled={update.isPending || !form.name.trim()}
              title={form.name.trim() ? undefined : "Vendor name is required."}
            >
              Save changes
            </Button>
          </div>
        </CardContent>
      </Card>

      <Card>
        <CardContent className="p-6 space-y-4">
          <div className="flex flex-col gap-3 sm:flex-row sm:items-center sm:justify-between">
            <div>
              <h2 className="font-semibold text-slate-800">Portal upload links</h2>
              <p className="text-xs text-slate-500">Share a link with {vendor.name} — they upload with no login.</p>
            </div>
            <div className="flex flex-col items-stretch gap-2 sm:flex-row sm:items-center">
              {/* No title tooltips on the disabled states: the Button base applies
                  disabled:pointer-events-none, so a disabled button never receives the
                  hover that would show one — the visible notes below carry the reason
                  instead (#261 review). */}
              <Button
                onClick={emailLinkToVendor}
                disabled={portalGated || !hasContactEmail || linkActionPending}
              >
                <Mail className="w-4 h-4 mr-1" /> Email link to {vendor.name}
              </Button>
              <Button
                variant="outline"
                onClick={copyLinkToClipboard}
                disabled={portalGated || linkActionPending}
              >
                <LinkIcon className="w-4 h-4 mr-1" /> Copy link
              </Button>
            </div>
          </div>
          {portalGated && (
            <p role="status" className="text-xs text-amber-700">
              Vendor upload links are a Pro feature.{" "}
              <Link href="/settings" className="font-medium text-sky-700 hover:underline">
                Upgrade your plan
              </Link>{" "}
              to collect documents straight from {vendor.name}.
            </p>
          )}
          {!portalGated && !hasContactEmail && (
            <p className="text-xs text-amber-700">
              Add a contact email above and save to email the upload link to {vendor.name}.
            </p>
          )}

          {vendor.portalLinks.length === 0 ? (
            <p className="text-sm text-slate-500">No links yet — generate one above.</p>
          ) : (
            <ul className="space-y-2">
              {vendor.portalLinks.map((l) => (
                <li key={l.id} className="flex items-center gap-3 px-3 py-2 rounded-md border border-slate-100 bg-slate-50/50">
                  <Input value={l.fullUrl} readOnly className="flex-1 h-8 font-mono text-xs" />
                  {/* Gated with the top-level actions (#261 review): a lapsed org
                      hand-copying a row URL would distribute a link the portal
                      answers 404 for. Revoke stays enabled — killing a link is a
                      safety action, not a portal feature. */}
                  <Button
                    size="sm"
                    variant="outline"
                    aria-label="Copy upload link"
                    disabled={portalGated}
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
                    <Button size="sm" variant="ghost" aria-label="Revoke link" onClick={() => revoke.mutate(l.id)}>
                      <XCircle className="w-4 h-4 text-slate-500 hover:text-rose-600" />
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
  // a11y: per-instance id wires label→input via htmlFor (#76). useId
  // gives a stable id per LabeledInput instance, so two inputs with
  // the same `label` prop on the same page each get their own id.
  const id = useId();
  return (
    <div>
      <label htmlFor={id} className="text-xs text-slate-500">{label}</label>
      <Input id={id} value={value} onChange={(e) => onChange(e.target.value)} className="mt-1" />
    </div>
  );
}
