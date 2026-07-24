"use client";

import { useEffect, useId, useMemo, useRef, useState } from "react";
import Link from "next/link";
import { useQuery, useMutation, useQueryClient } from "@tanstack/react-query";
import { toast } from "sonner";
import { AlertTriangle, Check, Pencil, Plus, RotateCw, ShieldCheck, Trash2, X } from "lucide-react";
import { api, friendly, GENERIC_FALLBACK_MESSAGE } from "@/lib/api";
import {
  REQUIREMENT_GROUPS,
  REQUIREMENT_TYPES,
  MONEY_PRESETS,
  formatMoney,
  parseMoneyInput,
  isSuspiciouslyLowMoney,
  requirementSentence,
  requirementErrorMessage,
  findRequirementType,
  type RequirementType,
} from "@/lib/requirements";
import { ConfirmDialog } from "@/components/ConfirmDialog";
import { PageTip } from "@/components/onboarding/PageTip";
import { TIP_IDS } from "@/lib/onboarding";
import { useMe } from "@/hooks/useAuth";
import { Card, CardContent } from "@/components/ui/card";
import { Skeleton } from "@/components/ui/skeleton";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { Badge } from "@/components/ui/badge";

// Lower-case noun per document type for the per-type compliance summary (#319 FP-083):
// "Each certificate of insurance must prove…". The engine scopes each rule to its type.
function docTypeNoun(type: string): string {
  switch (type.toLowerCase()) {
    case "coi": return "certificate of insurance";
    case "license": return "license";
    case "permit": return "permit";
    case "certification": return "certification";
    case "contract": return "contract";
    default: return "document";
  }
}

type TemplateSummary = {
  id: string;
  name: string;
  description: string | null;
  isSystemTemplate: boolean;
  ruleCount: number;
  vendorCount: number;
};

type TemplateRule = {
  id: string;
  documentType: string;
  fieldName: string | null;
  operator: string;
  expectedValue: string | null;
  errorMessage: string | null;
  sortOrder: number;
};

type TemplateDetail = {
  id: string;
  name: string;
  description: string | null;
  isSystemTemplate: boolean;
  rules: TemplateRule[];
};

// The {documentType, fieldName, operator, expectedValue, errorMessage} a chosen
// requirement maps onto — exactly the existing rules endpoint's shape (#192).
type RulePayload = {
  documentType: string;
  fieldName: string;
  operator: string;
  expectedValue: string | null;
  errorMessage: string;
};

// The catalog entry for the additional-insured requirement (#400). This requirement is
// deliberately NOT seeded onto the suggested checklists — a venue names ITSELF, so the value is
// per-tenant — so a venue that clones (say) the Caterer checklist gets GL / expiration / workers'
// comp / liquor but no additional-insured check. The nudge below points them at it with their own
// venue name, replacing seeding it with a one-size-fits-nobody placeholder.
//
// Kept OPTIONAL (no non-null assertion): every other findRequirementType call site handles
// `RequirementType | undefined`, and a force-unwrap here meant a renamed/removed catalog entry
// would turn a localized nudge into a whole-page crash (ChecklistEditor dereferencing undefined).
// The nudge simply doesn't render when the entry is absent — see the guarded call site below.
const ADDITIONAL_INSURED = findRequirementType({
  documentType: "coi",
  fieldName: "additional_insured",
  operator: "contains",
});

// Requirement types whose "+ Add a requirement" MENU entry is gated behind the server's
// TemplateCorrections flag (`me.features.correctedChecklists`, #416 / ADR 0036 Amendment 3) —
// hidden until the G1 legal/insurance sign-off flips it. Deliberately a MENU-only cut: the entries
// stay in the REQUIREMENT_TYPES catalog itself so `findRequirementType` still resolves them, and an
// EXISTING rule (authored while the flag was on, or after a future flip) keeps rendering its
// curated sentence + Edit pencil instead of degrading to the raw-token FP-085 fallback.
const CORRECTIONS_GATED_MENU_KEYS: ReadonlyArray<string> = ["liquor_liability"];

export default function RulesPage() {
  const qc = useQueryClient();
  // The #416 corrected-checklist surfaces (liquor add-menu option, additional-insured nudge) are
  // gated on the server flag carried by the me-payload (ADR 0036 Amendment 3). Strict `=== true`
  // so a loading/undefined me defaults to HIDDEN — the safe (flag-off, prod-identical) posture.
  const { data: me } = useMe();
  // Optional-chain THROUGH features, not just me: during a rolling deploy a fresh JS bundle can
  // receive a me-payload from an old backend instance that predates the additive `features` field.
  // The gate must fall back to hidden (the safe default), never crash the page (pass-3 review).
  const correctedChecklistsEnabled = me?.features?.correctedChecklists === true;
  // #396 (CLM-1): the corrected additional-insured claim wording. Same strict `=== true` / optional-
  // chain-through-features posture — a loading/undefined/old-backend me defaults to the LEGACY copy
  // (the safe, prod-identical flag-off state). Distinct flag from correctedChecklists (ADR 0043).
  const correctedAdditionalInsuredWording = me?.features?.correctedAdditionalInsuredWording === true;
  const [selectedId, setSelectedId] = useState<string | null>(null);
  // On phones the editor renders ~2 screens below the rail, so tapping a checklist
  // looked like nothing happened (#319 FP-080). Scroll the editor into view on
  // selection below md, where the two columns stack. Desktop (md+) is side-by-side,
  // so it skips the scroll — guarded on the same breakpoint the grid uses.
  const editorRef = useRef<HTMLDivElement>(null);
  useEffect(() => {
    if (!selectedId || !editorRef.current) return;
    if (typeof window !== "undefined" && window.matchMedia?.("(max-width: 767px)").matches) {
      editorRef.current.scrollIntoView({ behavior: "smooth", block: "start" });
    }
  }, [selectedId]);

  const templates = useQuery<TemplateSummary[]>({
    queryKey: ["templates"],
    queryFn: ({ signal }) => api.get<TemplateSummary[]>("/api/compliance/templates", { signal }),
  });

  const detail = useQuery<TemplateDetail>({
    queryKey: ["templates", selectedId],
    queryFn: ({ signal }) => api.get<TemplateDetail>(`/api/compliance/templates/${selectedId}`, { signal }),
    enabled: !!selectedId,
  });

  const createChecklist = useMutation({
    mutationFn: (name: string) => api.post<{ id: string }>("/api/compliance/templates", { name }),
    onSuccess: (res) => {
      qc.invalidateQueries({ queryKey: ["templates"] });
      setSelectedId(res.id);
      toast.success("Checklist created");
    },
    meta: { errorToast: true },
  });

  // "Use this" clones a suggested (system) checklist into an editable, org-owned
  // copy by replaying its rules through the existing POST endpoints — no clone
  // endpoint / migration needed for the MVP (#192).
  const cloneChecklist = useMutation({
    mutationFn: async (source: { id: string; name: string; description: string | null }) => {
      const full = await api.get<TemplateDetail>(`/api/compliance/templates/${source.id}`);
      const created = await api.post<{ id: string }>("/api/compliance/templates", {
        name: source.name,
        description: source.description,
      });
      try {
        for (const r of [...full.rules].sort((a, b) => a.sortOrder - b.sortOrder)) {
          await api.post(`/api/compliance/templates/${created.id}/rules`, {
            documentType: r.documentType,
            fieldName: r.fieldName,
            operator: r.operator,
            expectedValue: r.expectedValue,
            errorMessage: r.errorMessage,
            sortOrder: r.sortOrder,
          });
        }
      } catch (replayError) {
        // Roll back the partial clone so the user is never left with an orphaned,
        // half-populated checklist (best effort — the rollback delete itself may fail).
        try {
          await api.delete(`/api/compliance/templates/${created.id}`);
        } catch {
          /* swallow — the original error is what we surface */
        }
        throw replayError;
      }
      return created.id;
    },
    onSuccess: (newId) => {
      qc.invalidateQueries({ queryKey: ["templates"] });
      setSelectedId(newId);
      toast.success("Checklist added — edit it to fit your vendors");
    },
    onError: (err) => toast.error(friendly(err)),
  });

  const deleteChecklist = useMutation({
    mutationFn: (id: string) => api.delete<void>(`/api/compliance/templates/${id}`),
    onSuccess: () => {
      qc.invalidateQueries({ queryKey: ["templates"] });
      setSelectedId(null);
      toast.success("Checklist removed");
    },
    meta: { errorToast: true },
  });

  const upsertRule = useMutation({
    mutationFn: (vars: { templateId: string; rule: Partial<TemplateRule> }) =>
      api.post<{ id: string }>(`/api/compliance/templates/${vars.templateId}/rules`, vars.rule),
    // Prefix-invalidate ['templates'] refreshes BOTH the list (rule counts) and the
    // selected-template detail observer with one call (see useVendors for the
    // TanStack prefix-match rationale).
    onSuccess: () => qc.invalidateQueries({ queryKey: ["templates"] }),
    meta: { errorToast: true },
  });

  const deleteRule = useMutation({
    mutationFn: (vars: { templateId: string; ruleId: string }) =>
      api.delete<void>(`/api/compliance/templates/${vars.templateId}/rules/${vars.ruleId}`),
    onSuccess: () => qc.invalidateQueries({ queryKey: ["templates"] }),
    meta: { errorToast: true },
  });

  const all = useMemo(() => templates.data ?? [], [templates.data]);
  const yours = all.filter((t) => !t.isSystemTemplate);
  const suggested = all.filter((t) => t.isSystemTemplate);
  // Heal the #237 seam (#239 delta 2): a system template assigned to a vendor used to
  // leave "Your checklists — None yet" reading as "you have nothing", with the assigned
  // checklist invisible. Acknowledge any in-use suggested checklist so Pat can find it.
  const suggestedInUse = suggested.some((t) => t.vendorCount > 0);

  return (
    <div className="max-w-6xl mx-auto px-6 py-8 space-y-6">
      <header>
        <h1 className="text-2xl font-semibold text-sky-900">Vendor requirements</h1>
        <p className="text-slate-500">
          Set what each kind of vendor must prove. We check every document you upload against the
          list automatically.
        </p>
      </header>

      <PageTip id={TIP_IDS.rules} title="Build a checklist in plain English">
        Each checklist is what one kind of vendor must prove. Add requirements by picking sentences —
        no codes or field names. Then assign the checklist to a vendor.
      </PageTip>

      <div className="grid grid-cols-1 md:grid-cols-[300px_1fr] gap-6">
        <aside className="space-y-5">
          <NewChecklistForm
            onCreate={(name) => createChecklist.mutate(name)}
            pending={createChecklist.isPending}
          />

          {/* FP-082: a backend outage must not render as "None yet" + zero suggested
              checklists. Skeletons while loading, an error card + Retry on failure. */}
          {templates.isLoading ? (
            <div className="space-y-2" role="status" aria-label="Loading checklists">
              {[0, 1, 2].map((i) => (
                <Skeleton key={i} className="h-12 w-full rounded-md" />
              ))}
            </div>
          ) : templates.isError ? (
            <div
              className="rounded-md border border-rose-100 bg-rose-50/40 px-3 py-4 text-center"
              role="alert"
            >
              <AlertTriangle className="mx-auto h-6 w-6 text-rose-500" />
              <p className="mt-2 text-sm font-medium text-slate-800">Couldn&apos;t load checklists.</p>
              <p className="text-xs text-slate-500">
                {templates.error?.message?.trim() || GENERIC_FALLBACK_MESSAGE}
              </p>
              <Button
                variant="outline"
                size="sm"
                className="mt-3"
                onClick={() => templates.refetch()}
                disabled={templates.isFetching}
              >
                <RotateCw className={`w-3.5 h-3.5 mr-1 ${templates.isFetching ? "animate-spin" : ""}`} />
                Retry
              </Button>
            </div>
          ) : (
            <>
              <div className="space-y-1.5">
                <h2 className="text-xs font-semibold uppercase tracking-wide text-slate-500">
                  Your checklists
                </h2>
                {yours.length === 0 ? (
                  <p className="text-sm text-slate-500">
                    {suggestedInUse
                      ? "You’re using a suggested checklist (below). Create your own above, or open it and click “Use this” for an editable copy."
                      : "None yet — create one above, or start from a suggested checklist below."}
                  </p>
                ) : (
                  yours.map((t) => (
                    <ChecklistButton
                      key={t.id}
                      template={t}
                      selected={selectedId === t.id}
                      onSelect={() => setSelectedId(t.id)}
                    />
                  ))
                )}
              </div>

              {suggested.length > 0 && (
                <div className="space-y-1.5">
                  <h2 className="text-xs font-semibold uppercase tracking-wide text-slate-500">
                    Suggested checklists
                  </h2>
                  {suggested.map((t) => (
                    <SuggestedChecklist
                      key={t.id}
                      template={t}
                      selected={selectedId === t.id}
                      onPreview={() => setSelectedId(t.id)}
                      onUse={() => cloneChecklist.mutate(t)}
                      using={cloneChecklist.isPending}
                    />
                  ))}
                </div>
              )}
            </>
          )}
        </aside>

        <section ref={editorRef}>
          {!selectedId ? (
            <Card>
              <CardContent className="p-8 text-sm text-slate-500">
                Pick a checklist on the left, or create one, to set what a vendor must prove.
              </CardContent>
            </Card>
          ) : detail.isError ? (
            <Card>
              <CardContent className="p-8 text-center" role="alert">
                <AlertTriangle className="mx-auto h-7 w-7 text-rose-500" />
                <p className="mt-2 text-sm font-medium text-slate-800">Couldn&apos;t load this checklist.</p>
                <p className="text-xs text-slate-500">
                  {detail.error?.message?.trim() || GENERIC_FALLBACK_MESSAGE}
                </p>
                <Button
                  variant="outline"
                  size="sm"
                  className="mt-3"
                  onClick={() => detail.refetch()}
                  disabled={detail.isFetching}
                >
                  <RotateCw className={`w-3.5 h-3.5 mr-1 ${detail.isFetching ? "animate-spin" : ""}`} />
                  Retry
                </Button>
              </CardContent>
            </Card>
          ) : detail.isLoading || !detail.data ? (
            <Card>
              <CardContent className="p-8 space-y-3" role="status" aria-label="Loading checklist">
                <Skeleton className="h-5 w-48" />
                <Skeleton className="h-4 w-full" />
                <Skeleton className="h-4 w-3/4" />
                <Skeleton className="h-9 w-full" />
              </CardContent>
            </Card>
          ) : (
            <ChecklistEditor
              detail={detail.data}
              vendorCount={all.find((t) => t.id === detail.data!.id)?.vendorCount ?? 0}
              correctedChecklistsEnabled={correctedChecklistsEnabled}
              correctedAdditionalInsuredWording={correctedAdditionalInsuredWording}
              onAddRequirement={(rule) =>
                upsertRule.mutate({
                  templateId: detail.data!.id,
                  // Derive from max existing sortOrder, NOT the count — after a middle
                  // requirement is removed, length no longer equals the max and a new
                  // rule would collide with an existing one's sortOrder.
                  rule: {
                    ...rule,
                    sortOrder: detail.data!.rules.length
                      ? Math.max(...detail.data!.rules.map((r) => r.sortOrder)) + 1
                      : 1,
                  },
                })
              }
              onEditRequirement={(ruleId, rule, sortOrder) =>
                upsertRule.mutate({ templateId: detail.data!.id, rule: { id: ruleId, ...rule, sortOrder } })
              }
              onRemoveRequirement={(ruleId) =>
                deleteRule.mutate({ templateId: detail.data!.id, ruleId })
              }
              onDeleteChecklist={() => deleteChecklist.mutate(detail.data!.id)}
              onUse={() => cloneChecklist.mutate(detail.data!)}
              using={cloneChecklist.isPending}
            />
          )}
        </section>
      </div>
    </div>
  );
}

// --------------------------------------------------------------------------
// Left rail
// --------------------------------------------------------------------------

function ChecklistButton({
  template,
  selected,
  onSelect,
}: {
  template: TemplateSummary;
  selected: boolean;
  onSelect: () => void;
}) {
  return (
    <button
      onClick={onSelect}
      aria-current={selected ? "true" : undefined}
      className={`w-full text-left px-3 py-2 rounded-md text-sm border ${
        selected ? "border-sky-500 bg-sky-50" : "border-slate-200 hover:border-sky-300"
      }`}
    >
      <span className="font-medium text-slate-800">{template.name}</span>
      <p className="text-xs text-slate-500">
        {template.ruleCount} requirement{template.ruleCount === 1 ? "" : "s"} · used by{" "}
        {template.vendorCount} vendor{template.vendorCount === 1 ? "" : "s"}
      </p>
    </button>
  );
}

function SuggestedChecklist({
  template,
  selected,
  onPreview,
  onUse,
  using,
}: {
  template: TemplateSummary;
  selected: boolean;
  onPreview: () => void;
  onUse: () => void;
  using: boolean;
}) {
  return (
    <div
      className={`rounded-md border ${selected ? "border-sky-500 bg-sky-50" : "border-slate-200"} px-3 py-2`}
    >
      <button onClick={onPreview} className="w-full text-left" aria-current={selected ? "true" : undefined}>
        <span className="flex items-center gap-2">
          <span className="font-medium text-slate-800">{template.name}</span>
          {template.vendorCount > 0 && (
            <Badge className="bg-emerald-100 text-emerald-700 border-transparent text-[10px] uppercase tracking-wide">
              In use
            </Badge>
          )}
        </span>
        <p className="text-xs text-slate-500">
          {template.ruleCount} requirement{template.ruleCount === 1 ? "" : "s"}
          {template.vendorCount > 0 && (
            <> · used by {template.vendorCount} vendor{template.vendorCount === 1 ? "" : "s"}</>
          )}
        </p>
      </button>
      <Button size="sm" variant="outline" className="mt-2 w-full" onClick={onUse} disabled={using}>
        {using ? "Adding…" : "Use this"}
      </Button>
    </div>
  );
}

function NewChecklistForm({
  onCreate,
  pending,
}: {
  onCreate: (name: string) => void;
  pending: boolean;
}) {
  const [name, setName] = useState("");
  const id = useId();
  const trimmed = name.trim();
  return (
    <Card>
      <CardContent className="p-4 space-y-2">
        <label htmlFor={id} className="text-xs font-medium text-slate-600">
          New checklist (e.g. Caterer, DJ, Florist)
        </label>
        <div className="flex gap-2">
          <Input
            id={id}
            value={name}
            onChange={(e) => setName(e.target.value)}
            placeholder="Vendor type"
            onKeyDown={(e) => {
              if (e.key === "Enter" && trimmed && !pending) {
                onCreate(trimmed);
                setName("");
              }
            }}
          />
          <Button
            size="sm"
            aria-label="Create checklist"
            disabled={!trimmed || pending}
            onClick={() => {
              onCreate(trimmed);
              setName("");
            }}
          >
            <Plus className="w-4 h-4" />
          </Button>
        </div>
      </CardContent>
    </Card>
  );
}

// --------------------------------------------------------------------------
// Main editor
// --------------------------------------------------------------------------

function ChecklistEditor({
  detail,
  vendorCount,
  correctedChecklistsEnabled,
  correctedAdditionalInsuredWording,
  onAddRequirement,
  onEditRequirement,
  onRemoveRequirement,
  onDeleteChecklist,
  onUse,
  using,
}: {
  detail: TemplateDetail;
  vendorCount: number;
  /** Server flag (#416): gates the liquor add-menu option + the additional-insured nudge. */
  correctedChecklistsEnabled: boolean;
  /** Server flag (#396 / CLM-1): swaps the additional-insured sentence + stored errorMessage to the
   *  corrected "certificate indicates…" wording. Independent of correctedChecklistsEnabled. */
  correctedAdditionalInsuredWording: boolean;
  onAddRequirement: (rule: RulePayload) => void;
  onEditRequirement: (ruleId: string, rule: RulePayload, sortOrder: number) => void;
  onRemoveRequirement: (ruleId: string) => void;
  onDeleteChecklist: () => void;
  onUse: () => void;
  using: boolean;
}) {
  const readOnly = detail.isSystemTemplate;
  const rules = [...detail.rules].sort((a, b) => a.sortOrder - b.sortOrder);
  // Nudge the additional-insured requirement (#400) only on an EDITABLE checklist that already
  // governs certificates of insurance but hasn't added the (deliberately-unseeded) additional-
  // insured check yet — so a venue that cloned a suggested checklist is reminded to name itself.
  const governsCoi = rules.some((r) => r.documentType === "coi");
  // Guarded on the catalog entry existing (ADDITIONAL_INSURED is now optional): a
  // renamed/removed entry degrades to "no nudge", never a crash dereferencing undefined.
  const hasAdditionalInsured =
    ADDITIONAL_INSURED != null &&
    rules.some(
      (r) =>
        r.documentType === ADDITIONAL_INSURED.documentType &&
        r.fieldName === ADDITIONAL_INSURED.fieldName &&
        r.operator === ADDITIONAL_INSURED.operator,
    );

  return (
    <div className="space-y-4">
      <div className="flex items-start justify-between gap-3">
        <div>
          <h2 className="text-xl font-semibold text-slate-900">{detail.name}</h2>
          <p className="text-sm text-slate-500">
            {readOnly
              ? "A suggested starting point. Use it to create your own editable copy."
              : "Add or remove requirements below. They apply to every document a vendor on this checklist sends."}
          </p>
        </div>
        {readOnly ? (
          <Button size="sm" onClick={onUse} disabled={using}>
            {using ? "Adding…" : "Use this"}
          </Button>
        ) : (
          <ConfirmDialog
            title={`Delete ${detail.name}?`}
            description={
              "This removes the checklist and all of its requirements." +
              (vendorCount > 0
                ? ` ${vendorCount} vendor${vendorCount === 1 ? "" : "s"} assigned to it will no longer be checked against it.`
                : " No vendors are assigned to it.")
            }
            confirmLabel="Delete"
            destructive
            onConfirm={onDeleteChecklist}
            trigger={
              <Button variant="outline" size="sm">
                Delete checklist
              </Button>
            }
          />
        )}
      </div>

      <Card>
        <CardContent className="p-5 space-y-3">
          {rules.length === 0 ? (
            <p className="text-sm text-slate-500">
              No requirements yet.{" "}
              {readOnly ? "" : "Add the first one below — each is a plain-English sentence."}
            </p>
          ) : (
            <ul className="space-y-2">
              {rules.map((r) => (
                <RequirementRow
                  key={r.id}
                  rule={r}
                  readOnly={readOnly}
                  correctedAdditionalInsuredWording={correctedAdditionalInsuredWording}
                  onEdit={(payload) => onEditRequirement(r.id, payload, r.sortOrder)}
                  onRemove={() => onRemoveRequirement(r.id)}
                />
              ))}
            </ul>
          )}

          {!readOnly && (
            <AddRequirement
              onAdd={onAddRequirement}
              existingRules={rules}
              correctedChecklistsEnabled={correctedChecklistsEnabled}
              correctedAdditionalInsuredWording={correctedAdditionalInsuredWording}
            />
          )}
        </CardContent>
      </Card>

      {/* The nudge is additionally gated on the #416 server flag (ADR 0036 Amendment 3): it ships
          merged but stays invisible until the G1 legal sign-off flips correctedChecklists on. */}
      {correctedChecklistsEnabled &&
        !readOnly &&
        governsCoi &&
        ADDITIONAL_INSURED != null &&
        !hasAdditionalInsured && (
          <AdditionalInsuredNudge
            type={ADDITIONAL_INSURED}
            onAdd={onAddRequirement}
            correctedAdditionalInsuredWording={correctedAdditionalInsuredWording}
          />
        )}

      {rules.length > 0 && (
        <ComplianceSummary
          name={detail.name}
          rules={rules}
          correctedAdditionalInsuredWording={correctedAdditionalInsuredWording}
        />
      )}

      {/* Editor dead-ended after authoring — the tip promises assignment but offered no
          path to it (#319 FP-086). Surface the next step for an editable checklist. */}
      {!readOnly && rules.length > 0 && (
        <p className="text-sm text-slate-600">
          {vendorCount > 0 ? (
            <>
              Used by {vendorCount} vendor{vendorCount === 1 ? "" : "s"}.{" "}
              <Link href="/vendors" className="font-medium text-sky-700 hover:underline">
                Assign it to more →
              </Link>
            </>
          ) : (
            <>
              Ready?{" "}
              <Link href="/vendors" className="font-medium text-sky-700 hover:underline">
                Assign this checklist to a vendor →
              </Link>
            </>
          )}
        </p>
      )}
    </div>
  );
}

// The additional-insured nudge (#400). Hoisted to module scope per the react-hooks/static-components
// rule. "Add it now" expands the SAME value form the add-requirement menu uses, pre-set to the
// additional-insured requirement, so the venue types its exact legal name and the rule is created
// through the existing onAdd path. Self-dismisses once the requirement is on the checklist.
function AdditionalInsuredNudge({
  type,
  onAdd,
  correctedAdditionalInsuredWording,
}: {
  type: RequirementType;
  onAdd: (rule: RulePayload) => void;
  correctedAdditionalInsuredWording: boolean;
}) {
  const [expanded, setExpanded] = useState(false);

  if (expanded) {
    return (
      <div className="rounded-lg border border-sky-200 bg-sky-50/60 p-4">
        <RequirementValueForm
          type={type}
          initialValue={null}
          submitLabel="Add requirement"
          correctedAdditionalInsuredWording={correctedAdditionalInsuredWording}
          onSubmit={(payload) => {
            onAdd(payload);
            setExpanded(false);
          }}
          onCancel={() => setExpanded(false)}
        />
      </div>
    );
  }

  return (
    <div className="flex flex-col gap-3 rounded-lg border border-sky-200 bg-sky-50/60 px-4 py-3 sm:flex-row sm:items-center sm:justify-between">
      <div className="flex items-start gap-2.5">
        <ShieldCheck className="mt-0.5 h-5 w-5 shrink-0 text-sky-600" aria-hidden="true" />
        <div className="min-w-0 text-sm">
          <p className="font-medium text-sky-900">Add your additional-insured requirement</p>
          <p className="mt-0.5 text-slate-600">
            Most venues require vendors to name them as an additional insured on the certificate.
            Enter your venue&apos;s exact legal name and we&apos;ll check every certificate for it.
          </p>
        </div>
      </div>
      <Button
        size="sm"
        variant="outline"
        className="shrink-0 self-start sm:self-auto"
        onClick={() => setExpanded(true)}
      >
        <Plus className="mr-1 h-4 w-4" /> Add it now
      </Button>
    </div>
  );
}

function joinClauses(clauses: string[]): string {
  return clauses.length === 1
    ? clauses[0]
    : `${clauses.slice(0, -1).join("; ")}; and ${clauses[clauses.length - 1]}`;
}

function ComplianceSummary({
  name,
  rules,
  correctedAdditionalInsuredWording,
}: {
  name: string;
  rules: TemplateRule[];
  correctedAdditionalInsuredWording: boolean;
}) {
  // Group by document type (#319 FP-083): the engine scopes each rule to its type, so a
  // certificate of insurance is NEVER checked for a license requirement. The old "every
  // document proves: [all rules]" taught a false model (a single COI can't satisfy a
  // license rule). One truthful sentence per type, in first-appearance order.
  const order: string[] = [];
  const byType = new Map<string, string[]>();
  for (const r of rules) {
    if (!byType.has(r.documentType)) {
      byType.set(r.documentType, []);
      order.push(r.documentType);
    }
    byType.get(r.documentType)!.push(requirementSentence(r, { correctedAdditionalInsuredWording }));
  }
  return (
    <div className="space-y-1.5 rounded-lg border border-emerald-100 bg-emerald-50/60 px-4 py-3 text-sm text-emerald-900">
      <p className="font-medium">For a {name} to be covered:</p>
      <ul className="space-y-1">
        {order.map((type) => (
          <li key={type}>
            Each {docTypeNoun(type)} must prove: {joinClauses(byType.get(type)!)}.
          </li>
        ))}
      </ul>
    </div>
  );
}

function RequirementRow({
  rule,
  readOnly,
  correctedAdditionalInsuredWording,
  onEdit,
  onRemove,
}: {
  rule: TemplateRule;
  readOnly: boolean;
  /** Server flag (#396 / CLM-1): renders the corrected additional-insured sentence when on. */
  correctedAdditionalInsuredWording: boolean;
  onEdit: (payload: RulePayload) => void;
  onRemove: () => void;
}) {
  const [editing, setEditing] = useState(false);
  const type = findRequirementType(rule);
  // Compute once so the visible sentence and both aria-labels stay in lockstep (incl. the #396 copy).
  const sentence = requirementSentence(rule, { correctedAdditionalInsuredWording });

  if (editing && type) {
    return (
      <li className="rounded-md border border-sky-200 bg-sky-50/50 p-3">
        <RequirementValueForm
          type={type}
          initialValue={rule.expectedValue}
          submitLabel="Save"
          correctedAdditionalInsuredWording={correctedAdditionalInsuredWording}
          onSubmit={(payload) => {
            onEdit(payload);
            setEditing(false);
          }}
          onCancel={() => setEditing(false)}
        />
      </li>
    );
  }

  return (
    <li className="flex items-start gap-2.5 rounded-md border border-slate-100 px-3 py-2.5">
      <Check className="mt-0.5 h-4 w-4 shrink-0 text-emerald-600" aria-hidden="true" />
      <span className="min-w-0 flex-1 text-sm text-slate-800">{sentence}</span>
      {!readOnly && (
        <div className="flex shrink-0 items-center gap-0.5">
          {type && type.valueKind !== "none" && (
            <Button
              size="sm"
              variant="ghost"
              aria-label={`Edit requirement: ${sentence}`}
              onClick={() => setEditing(true)}
            >
              <Pencil className="h-4 w-4 text-slate-500 hover:text-sky-600" />
            </Button>
          )}
          <Button
            size="sm"
            variant="ghost"
            aria-label={`Remove requirement: ${sentence}`}
            onClick={onRemove}
          >
            <Trash2 className="h-4 w-4 text-slate-500 hover:text-rose-600" />
          </Button>
        </div>
      )}
    </li>
  );
}

// --------------------------------------------------------------------------
// Add a requirement (the menu + value form)
// --------------------------------------------------------------------------

function buildPayload(
  type: RequirementType,
  rawValue: string,
  correctedAdditionalInsuredWording: boolean,
): RulePayload {
  let expectedValue: string | null = null;
  if (type.valueKind === "money") {
    const n = parseMoneyInput(rawValue);
    expectedValue = n != null ? String(n) : null;
  } else if (type.valueKind === "text") {
    expectedValue = rawValue.trim();
  }
  return {
    documentType: type.documentType,
    fieldName: type.fieldName,
    operator: type.operator,
    expectedValue,
    // #396 (CLM-1): the additional-insured errorMessage is staged corrected copy behind the flag;
    // every other requirement's message is flag-independent (requirementErrorMessage handles both).
    errorMessage: requirementErrorMessage(type, expectedValue, { correctedAdditionalInsuredWording }),
  };
}

function AddRequirement({
  onAdd,
  existingRules,
  correctedChecklistsEnabled,
  correctedAdditionalInsuredWording,
}: {
  onAdd: (rule: RulePayload) => void;
  existingRules: TemplateRule[];
  /** Server flag (#416): while false, CORRECTIONS_GATED_MENU_KEYS entries are hidden from the menu. */
  correctedChecklistsEnabled: boolean;
  /** Server flag (#396 / CLM-1): forwarded to the value form so a new/edited additional-insured
   *  rule stores the corrected errorMessage when on. */
  correctedAdditionalInsuredWording: boolean;
}) {
  const [open, setOpen] = useState(false);
  const [picked, setPicked] = useState<RequirementType | null>(null);

  // A requirement type is already on the checklist when a rule shares its
  // (documentType, fieldName, operator) — the same key the backend dedupes on
  // (#319 FP-081). Gray those out with an "edit it instead" hint rather than
  // letting the user add a confusing duplicate.
  const isAdded = (t: RequirementType) =>
    existingRules.some(
      (r) => r.documentType === t.documentType && r.fieldName === t.fieldName && r.operator === t.operator,
    );

  // MENU-only flag cut (#416 — see CORRECTIONS_GATED_MENU_KEYS): a gated entry disappears from the
  // picker while the flag is off, but the catalog keeps it so an existing rule still renders + edits.
  const isVisible = (t: RequirementType) =>
    correctedChecklistsEnabled || !CORRECTIONS_GATED_MENU_KEYS.includes(t.key);

  if (!open) {
    return (
      <Button variant="outline" size="sm" onClick={() => setOpen(true)}>
        <Plus className="mr-1 h-4 w-4" /> Add a requirement
      </Button>
    );
  }

  if (picked) {
    return (
      <div className="rounded-md border border-sky-200 bg-sky-50/50 p-3">
        <RequirementValueForm
          type={picked}
          initialValue={null}
          submitLabel="Add"
          correctedAdditionalInsuredWording={correctedAdditionalInsuredWording}
          onSubmit={(payload) => {
            onAdd(payload);
            setPicked(null);
            setOpen(false);
          }}
          onCancel={() => setPicked(null)}
        />
      </div>
    );
  }

  return (
    <div className="rounded-md border border-slate-200 p-3 space-y-3">
      <div className="flex items-center justify-between">
        <p className="text-sm font-medium text-slate-700">Add a requirement</p>
        <Button size="sm" variant="ghost" aria-label="Close add-requirement menu" onClick={() => setOpen(false)}>
          <X className="h-4 w-4" />
        </Button>
      </div>
      {REQUIREMENT_GROUPS.map((group) => (
        <div key={group}>
          <p className="text-xs font-semibold uppercase tracking-wide text-slate-500">{group}</p>
          <div className="mt-1 space-y-1">
            {REQUIREMENT_TYPES.filter((t) => t.group === group && isVisible(t)).map((t) =>
              isAdded(t) ? (
                <div
                  key={t.key}
                  aria-disabled="true"
                  className="flex items-center justify-between gap-2 rounded px-2 py-1.5 text-left text-sm text-slate-400"
                >
                  <span>{t.menuLabel}</span>
                  <span className="shrink-0 text-xs">Already added — edit it instead</span>
                </div>
              ) : (
                <button
                  key={t.key}
                  onClick={() => setPicked(t)}
                  className="block w-full rounded px-2 py-1.5 text-left text-sm text-slate-700 hover:bg-sky-50 pointer-coarse:min-h-11"
                >
                  {t.menuLabel}
                </button>
              ),
            )}
          </div>
        </div>
      ))}
    </div>
  );
}

// Stacked, fully-labeled value form (gap #21): one fill-in, numeric keyboard for
// money, a money formatter the user never has to learn, and a disabled-Add reason.
function RequirementValueForm({
  type,
  initialValue,
  submitLabel,
  correctedAdditionalInsuredWording,
  onSubmit,
  onCancel,
}: {
  type: RequirementType;
  initialValue: string | null;
  submitLabel: string;
  /** Server flag (#396 / CLM-1): folded into the built payload's errorMessage for additional insured. */
  correctedAdditionalInsuredWording: boolean;
  onSubmit: (payload: RulePayload) => void;
  onCancel: () => void;
}) {
  const fieldId = useId();
  const [value, setValue] = useState(() => {
    if (initialValue == null) return "";
    if (type.valueKind === "money") {
      const n = parseMoneyInput(initialValue);
      return n != null ? formatMoney(n) : initialValue;
    }
    return initialValue;
  });

  const moneyAmount = type.valueKind === "money" ? parseMoneyInput(value) : null;
  const ready =
    type.valueKind === "none" ||
    (type.valueKind === "money" ? moneyAmount != null && moneyAmount > 0 : value.trim().length > 0);
  const disabledReason =
    type.valueKind === "money"
      ? "Enter a coverage amount"
      : type.valueKind === "text"
        ? `Enter ${type.valueLabel?.toLowerCase() ?? "a value"}`
        : "";

  const submit = () => {
    if (!ready) return;
    onSubmit(buildPayload(type, value, correctedAdditionalInsuredWording));
  };

  return (
    <div className="space-y-3">
      <p className="text-sm font-medium text-slate-800">{type.menuLabel}</p>

      {type.valueKind === "money" && (
        <div className="space-y-2">
          <label htmlFor={fieldId} className="block text-xs font-medium text-slate-600">
            {type.valueLabel ?? "Amount"}
          </label>
          <div className="flex flex-wrap gap-1.5">
            {MONEY_PRESETS.map((p) => (
              <Button
                key={p}
                type="button"
                size="sm"
                variant={moneyAmount === p ? "default" : "outline"}
                onClick={() => setValue(formatMoney(p))}
              >
                {formatMoney(p)}
              </Button>
            ))}
          </div>
          <Input
            id={fieldId}
            inputMode="numeric"
            value={value}
            onChange={(e) => setValue(e.target.value)}
            onBlur={() => moneyAmount != null && setValue(formatMoney(moneyAmount))}
            placeholder="$1,000,000"
          />
          {/* FP-084: a tiny amount is almost always a typo — "$2" passes every COI. Nudge
              (non-blocking) toward the millions, and hint the k/m shorthand. */}
          {isSuspiciouslyLowMoney(moneyAmount) && (
            <p role="status" className="flex items-start gap-1.5 text-xs text-amber-700">
              <AlertTriangle className="mt-0.5 h-3.5 w-3.5 shrink-0" aria-hidden="true" />
              <span>
                That&apos;s only {formatMoney(moneyAmount!)} — coverage limits are usually in the
                millions. You can type shorthand like <strong>2M</strong> or <strong>500k</strong>.
              </span>
            </p>
          )}
        </div>
      )}

      {type.valueKind === "text" && (
        <div className="space-y-1.5">
          <label htmlFor={fieldId} className="block text-xs font-medium text-slate-600">
            {type.valueLabel ?? "Value"}
          </label>
          <Input
            id={fieldId}
            value={value}
            onChange={(e) => setValue(e.target.value)}
            placeholder={type.valuePlaceholder}
          />
        </div>
      )}

      {type.helper && <p className="text-xs text-slate-500">{type.helper}</p>}

      <div className="flex items-center gap-2">
        <Button size="sm" onClick={submit} disabled={!ready} title={!ready ? disabledReason : undefined}>
          {submitLabel}
        </Button>
        <Button size="sm" variant="ghost" onClick={onCancel}>
          Cancel
        </Button>
        {!ready && disabledReason && <span className="text-xs text-slate-500">{disabledReason}</span>}
      </div>
    </div>
  );
}
