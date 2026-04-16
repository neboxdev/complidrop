"use client";

import { useState } from "react";
import { useQuery, useMutation, useQueryClient } from "@tanstack/react-query";
import { toast } from "sonner";
import { Plus, Trash2 } from "lucide-react";
import { api } from "@/lib/api";
import { Card, CardContent } from "@/components/ui/card";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { Badge } from "@/components/ui/badge";

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

const OPERATORS = ["required", "equals", "contains", "min_value"];
const DOC_TYPES = ["coi", "license", "permit", "certification", "other"];

export default function RulesPage() {
  const qc = useQueryClient();
  const [selectedId, setSelectedId] = useState<string | null>(null);
  const [newTemplateName, setNewTemplateName] = useState("");

  const templates = useQuery<TemplateSummary[]>({
    queryKey: ["templates"],
    queryFn: () => api.get<TemplateSummary[]>("/api/compliance/templates"),
  });

  const detail = useQuery<TemplateDetail>({
    queryKey: ["templates", selectedId],
    queryFn: () => api.get<TemplateDetail>(`/api/compliance/templates/${selectedId}`),
    enabled: !!selectedId,
  });

  const createTemplate = useMutation({
    mutationFn: (name: string) => api.post<{ id: string }>("/api/compliance/templates", { name }),
    onSuccess: (res) => {
      qc.invalidateQueries({ queryKey: ["templates"] });
      setSelectedId(res.id);
      setNewTemplateName("");
      toast.success("Template created");
    },
  });

  const deleteTemplate = useMutation({
    mutationFn: (id: string) => api.delete<void>(`/api/compliance/templates/${id}`),
    onSuccess: () => {
      qc.invalidateQueries({ queryKey: ["templates"] });
      setSelectedId(null);
      toast.success("Template removed");
    },
  });

  const upsertRule = useMutation({
    mutationFn: (vars: { templateId: string; rule: Partial<TemplateRule> }) =>
      api.post<{ id: string }>(`/api/compliance/templates/${vars.templateId}/rules`, vars.rule),
    onSuccess: () => {
      qc.invalidateQueries({ queryKey: ["templates", selectedId] });
      qc.invalidateQueries({ queryKey: ["templates"] });
      toast.success("Rule saved");
    },
  });

  const deleteRule = useMutation({
    mutationFn: (vars: { templateId: string; ruleId: string }) =>
      api.delete<void>(`/api/compliance/templates/${vars.templateId}/rules/${vars.ruleId}`),
    onSuccess: () => {
      qc.invalidateQueries({ queryKey: ["templates", selectedId] });
      qc.invalidateQueries({ queryKey: ["templates"] });
    },
  });

  return (
    <div className="max-w-6xl mx-auto px-6 py-8 grid grid-cols-1 md:grid-cols-[300px_1fr] gap-6">
      <aside className="space-y-4">
        <div>
          <h1 className="text-2xl font-semibold text-sky-900">Templates</h1>
          <p className="text-xs text-slate-500">System templates are read-only.</p>
        </div>
        <Card>
          <CardContent className="p-4 space-y-2">
            <div className="flex gap-2">
              <Input
                value={newTemplateName}
                onChange={(e) => setNewTemplateName(e.target.value)}
                placeholder="New template name"
              />
              <Button
                size="sm"
                disabled={!newTemplateName || createTemplate.isPending}
                onClick={() => createTemplate.mutate(newTemplateName)}
              >
                <Plus className="w-4 h-4" />
              </Button>
            </div>
          </CardContent>
        </Card>
        <div className="space-y-1">
          {(templates.data ?? []).map((t) => (
            <button
              key={t.id}
              onClick={() => setSelectedId(t.id)}
              className={`w-full text-left px-3 py-2 rounded-md text-sm border ${
                selectedId === t.id ? "border-sky-500 bg-sky-50" : "border-slate-200 hover:border-sky-300"
              }`}
            >
              <div className="flex items-center justify-between">
                <span className="font-medium">{t.name}</span>
                {t.isSystemTemplate && <Badge className="bg-slate-100 text-slate-600 border-transparent">system</Badge>}
              </div>
              <p className="text-xs text-slate-500">{t.ruleCount} rules · {t.vendorCount} vendors</p>
            </button>
          ))}
        </div>
      </aside>

      <section className="space-y-4">
        {!selectedId ? (
          <Card>
            <CardContent className="p-8 text-sm text-slate-500">
              Select or create a template to edit its rules.
            </CardContent>
          </Card>
        ) : detail.isLoading || !detail.data ? (
          <Card>
            <CardContent className="p-8 text-sm text-slate-400">Loading…</CardContent>
          </Card>
        ) : (
          <>
            <div className="flex items-start justify-between">
              <div>
                <h2 className="text-xl font-semibold text-slate-900">{detail.data.name}</h2>
                <p className="text-sm text-slate-500">{detail.data.description ?? "No description"}</p>
              </div>
              {!detail.data.isSystemTemplate && (
                <Button
                  variant="outline"
                  size="sm"
                  onClick={() => {
                    if (confirm(`Delete ${detail.data!.name}?`)) deleteTemplate.mutate(selectedId!);
                  }}
                >
                  Delete template
                </Button>
              )}
            </div>

            <Card>
              <CardContent className="p-4 overflow-x-auto">
                <table className="w-full text-sm">
                  <thead className="text-xs uppercase text-slate-500">
                    <tr>
                      <th className="text-left py-2">Doc type</th>
                      <th className="text-left py-2">Field</th>
                      <th className="text-left py-2">Operator</th>
                      <th className="text-left py-2">Value</th>
                      <th className="text-left py-2">Message</th>
                      <th />
                    </tr>
                  </thead>
                  <tbody>
                    {detail.data.rules.map((r) => (
                      <tr key={r.id} className="border-t border-slate-100">
                        <td className="py-2 uppercase text-xs">{r.documentType}</td>
                        <td className="py-2 text-slate-700">{r.fieldName}</td>
                        <td className="py-2 text-slate-600">{r.operator}</td>
                        <td className="py-2 text-slate-600">{r.expectedValue ?? "—"}</td>
                        <td className="py-2 text-slate-500 text-xs">{r.errorMessage ?? "—"}</td>
                        <td className="py-2 text-right">
                          {!detail.data!.isSystemTemplate && (
                            <Button
                              size="sm"
                              variant="ghost"
                              onClick={() => deleteRule.mutate({ templateId: selectedId!, ruleId: r.id })}
                            >
                              <Trash2 className="w-4 h-4 text-slate-400 hover:text-rose-600" />
                            </Button>
                          )}
                        </td>
                      </tr>
                    ))}
                    {!detail.data.isSystemTemplate && (
                      <NewRuleRow
                        onSave={(rule) =>
                          upsertRule.mutate({ templateId: selectedId!, rule: { ...rule, sortOrder: (detail.data?.rules.length ?? 0) + 1 } })
                        }
                      />
                    )}
                  </tbody>
                </table>
              </CardContent>
            </Card>
          </>
        )}
      </section>
    </div>
  );
}

function NewRuleRow({ onSave }: { onSave: (rule: Partial<TemplateRule>) => void }) {
  const [documentType, setDocumentType] = useState("coi");
  const [fieldName, setFieldName] = useState("");
  const [operator, setOperator] = useState("required");
  const [expectedValue, setExpectedValue] = useState("");
  const [errorMessage, setErrorMessage] = useState("");

  return (
    <tr className="border-t border-slate-100 bg-sky-50/40">
      <td className="py-2">
        <select
          value={documentType}
          onChange={(e) => setDocumentType(e.target.value)}
          className="border border-slate-200 rounded px-2 py-1 text-xs uppercase"
        >
          {DOC_TYPES.map((t) => <option key={t} value={t}>{t}</option>)}
        </select>
      </td>
      <td className="py-2">
        <Input value={fieldName} onChange={(e) => setFieldName(e.target.value)} placeholder="e.g. general_liability_limit" className="h-8" />
      </td>
      <td className="py-2">
        <select
          value={operator}
          onChange={(e) => setOperator(e.target.value)}
          className="border border-slate-200 rounded px-2 py-1 text-xs"
        >
          {OPERATORS.map((o) => <option key={o} value={o}>{o}</option>)}
        </select>
      </td>
      <td className="py-2">
        <Input value={expectedValue} onChange={(e) => setExpectedValue(e.target.value)} placeholder="1000000" className="h-8" />
      </td>
      <td className="py-2">
        <Input value={errorMessage} onChange={(e) => setErrorMessage(e.target.value)} placeholder="Why this matters" className="h-8" />
      </td>
      <td className="py-2 text-right">
        <Button
          size="sm"
          disabled={!fieldName || !operator}
          onClick={() => {
            onSave({ documentType, fieldName, operator, expectedValue: expectedValue || null, errorMessage: errorMessage || null });
            setFieldName("");
            setExpectedValue("");
            setErrorMessage("");
          }}
        >
          Add
        </Button>
      </td>
    </tr>
  );
}
