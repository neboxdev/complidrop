"use client";

import { useId, useMemo, useState } from "react";
import { useQuery, useMutation, useQueryClient } from "@tanstack/react-query";
import { toast } from "sonner";
import { Check, Pencil, Plus, Trash2, X } from "lucide-react";
import { api, GENERIC_FALLBACK_MESSAGE } from "@/lib/api";
import {
  REQUIREMENT_GROUPS,
  REQUIREMENT_TYPES,
  MONEY_PRESETS,
  formatMoney,
  parseMoneyInput,
  requirementSentence,
  findRequirementType,
  type RequirementType,
} from "@/lib/requirements";
import { ConfirmDialog } from "@/components/ConfirmDialog";
import { PageTip } from "@/components/onboarding/PageTip";
import { TIP_IDS } from "@/lib/onboarding";
import { Card, CardContent } from "@/components/ui/card";
import { Skeleton } from "@/components/ui/skeleton";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";

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

function friendly(err: unknown): string {
  return err instanceof Error && err.message ? err.message : GENERIC_FALLBACK_MESSAGE;
}

export default function RulesPage() {
  const qc = useQueryClient();
  const [selectedId, setSelectedId] = useState<string | null>(null);

  const templates = useQuery<TemplateSummary[]>({
    queryKey: ["templates"],
    queryFn: () => api.get<TemplateSummary[]>("/api/compliance/templates"),
  });

  const detail = useQuery<TemplateDetail>({
    queryKey: ["templates", selectedId],
    queryFn: () => api.get<TemplateDetail>(`/api/compliance/templates/${selectedId}`),
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

          <div className="space-y-1.5">
            <h2 className="text-xs font-semibold uppercase tracking-wide text-slate-500">
              Your checklists
            </h2>
            {yours.length === 0 ? (
              <p className="text-sm text-slate-500">
                None yet — create one above, or start from a suggested checklist below.
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
        </aside>

        <section>
          {!selectedId ? (
            <Card>
              <CardContent className="p-8 text-sm text-slate-500">
                Pick a checklist on the left, or create one, to set what a vendor must prove.
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
        <span className="font-medium text-slate-800">{template.name}</span>
        <p className="text-xs text-slate-500">
          {template.ruleCount} requirement{template.ruleCount === 1 ? "" : "s"}
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
  onAddRequirement,
  onEditRequirement,
  onRemoveRequirement,
  onDeleteChecklist,
  onUse,
  using,
}: {
  detail: TemplateDetail;
  onAddRequirement: (rule: RulePayload) => void;
  onEditRequirement: (ruleId: string, rule: RulePayload, sortOrder: number) => void;
  onRemoveRequirement: (ruleId: string) => void;
  onDeleteChecklist: () => void;
  onUse: () => void;
  using: boolean;
}) {
  const readOnly = detail.isSystemTemplate;
  const rules = [...detail.rules].sort((a, b) => a.sortOrder - b.sortOrder);

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
            description="This removes the checklist and all of its requirements. Vendors assigned to it will no longer be checked against it."
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
                  onEdit={(payload) => onEditRequirement(r.id, payload, r.sortOrder)}
                  onRemove={() => onRemoveRequirement(r.id)}
                />
              ))}
            </ul>
          )}

          {!readOnly && <AddRequirement onAdd={onAddRequirement} />}
        </CardContent>
      </Card>

      {rules.length > 0 && <ComplianceSummary name={detail.name} rules={rules} />}
    </div>
  );
}

function ComplianceSummary({ name, rules }: { name: string; rules: TemplateRule[] }) {
  const clauses = rules.map((r) => requirementSentence(r));
  const list =
    clauses.length === 1
      ? clauses[0]
      : `${clauses.slice(0, -1).join("; ")}; and ${clauses[clauses.length - 1]}`;
  return (
    <p className="rounded-lg border border-emerald-100 bg-emerald-50/60 px-4 py-3 text-sm text-emerald-900">
      <span className="font-medium">A {name} is compliant when</span> every document proves: {list}.
    </p>
  );
}

function RequirementRow({
  rule,
  readOnly,
  onEdit,
  onRemove,
}: {
  rule: TemplateRule;
  readOnly: boolean;
  onEdit: (payload: RulePayload) => void;
  onRemove: () => void;
}) {
  const [editing, setEditing] = useState(false);
  const type = findRequirementType(rule);

  if (editing && type) {
    return (
      <li className="rounded-md border border-sky-200 bg-sky-50/50 p-3">
        <RequirementValueForm
          type={type}
          initialValue={rule.expectedValue}
          submitLabel="Save"
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
      <span className="min-w-0 flex-1 text-sm text-slate-800">{requirementSentence(rule)}</span>
      {!readOnly && (
        <div className="flex shrink-0 items-center gap-0.5">
          {type && type.valueKind !== "none" && (
            <Button
              size="sm"
              variant="ghost"
              aria-label={`Edit requirement: ${requirementSentence(rule)}`}
              onClick={() => setEditing(true)}
            >
              <Pencil className="h-4 w-4 text-slate-400 hover:text-sky-600" />
            </Button>
          )}
          <Button
            size="sm"
            variant="ghost"
            aria-label={`Remove requirement: ${requirementSentence(rule)}`}
            onClick={onRemove}
          >
            <Trash2 className="h-4 w-4 text-slate-400 hover:text-rose-600" />
          </Button>
        </div>
      )}
    </li>
  );
}

// --------------------------------------------------------------------------
// Add a requirement (the menu + value form)
// --------------------------------------------------------------------------

function buildPayload(type: RequirementType, rawValue: string): RulePayload {
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
    errorMessage: type.errorMessage(expectedValue),
  };
}

function AddRequirement({ onAdd }: { onAdd: (rule: RulePayload) => void }) {
  const [open, setOpen] = useState(false);
  const [picked, setPicked] = useState<RequirementType | null>(null);

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
          <p className="text-xs font-semibold uppercase tracking-wide text-slate-400">{group}</p>
          <div className="mt-1 space-y-1">
            {REQUIREMENT_TYPES.filter((t) => t.group === group).map((t) => (
              <button
                key={t.key}
                onClick={() => setPicked(t)}
                className="block w-full rounded px-2 py-1.5 text-left text-sm text-slate-700 hover:bg-sky-50 pointer-coarse:min-h-11"
              >
                {t.menuLabel}
              </button>
            ))}
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
  onSubmit,
  onCancel,
}: {
  type: RequirementType;
  initialValue: string | null;
  submitLabel: string;
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
    onSubmit(buildPayload(type, value));
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
        {!ready && disabledReason && <span className="text-xs text-slate-400">{disabledReason}</span>}
      </div>
    </div>
  );
}
