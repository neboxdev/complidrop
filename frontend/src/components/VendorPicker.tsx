"use client";

import { useId, useMemo, useState } from "react";
import { Check, Plus, X } from "lucide-react";
import { Input } from "@/components/ui/input";
import { Button } from "@/components/ui/button";
import { useVendors, useCreateVendor } from "@/hooks/useVendors";
import { cn } from "@/lib/utils";

export type VendorOption = { id: string; name: string };

type VendorPickerProps = {
  /** Currently-chosen vendor, or null when nothing is selected yet. */
  value: VendorOption | null;
  /** Called with the chosen vendor, or null when the selection is cleared. */
  onChange: (vendor: VendorOption | null) => void;
  /** Associates an external <label htmlFor> with the search input (#76 a11y). */
  inputId?: string;
  autoFocus?: boolean;
  /** Surface a friendly message if creating a brand-new vendor fails. */
  onCreateError?: (message: string) => void;
  placeholder?: string;
  disabled?: boolean;
};

// Cap how many matches render so a large vendor book doesn't paint a giant
// list — the search box narrows it well before the cap bites.
const MAX_VISIBLE = 8;

/**
 * Type-ahead vendor selector with an inline "Add new vendor" affordance.
 *
 * Module-scope (NOT declared inside a page render body) per the CLAUDE.md
 * react-hooks/static-components rule — inline components reset their state on
 * every parent render. Reused by the documents upload staging card AND the
 * orphaned-row assign affordance (#186).
 *
 * Selection model: when `value` is set the picker collapses to a compact pill
 * with a Change button; when null it shows the search input plus the filtered
 * option list. Each option is a real <button>, so it's keyboard-reachable
 * without bespoke combobox key handling.
 */
export function VendorPicker({
  value,
  onChange,
  inputId,
  autoFocus,
  onCreateError,
  placeholder = "Search vendors…",
  disabled,
}: VendorPickerProps) {
  const vendors = useVendors();
  const createVendor = useCreateVendor();
  const [query, setQuery] = useState("");
  const generatedId = useId();
  const listId = `${inputId ?? generatedId}-options`;

  // Memoized so `matches`'s dependency is stable across renders (a fresh
  // `vendors.data ?? []` literal would otherwise recompute the filter every
  // render — react-hooks/exhaustive-deps).
  const all = useMemo(() => vendors.data ?? [], [vendors.data]);
  const trimmed = query.trim();
  const lower = trimmed.toLowerCase();

  const matches = useMemo(() => {
    if (!lower) return all.slice(0, MAX_VISIBLE);
    return all.filter((v) => v.name.toLowerCase().includes(lower)).slice(0, MAX_VISIBLE);
  }, [all, lower]);

  // Offer "Add new" only when the typed name doesn't already exist (exact,
  // case-insensitive) so we never create a duplicate of a vendor that's just
  // scrolled out of the visible matches.
  const exactExists = all.some((v) => v.name.toLowerCase() === lower);
  const canCreate = trimmed.length > 0 && !exactExists;

  if (value) {
    return (
      <div className="flex items-center gap-2">
        <span className="inline-flex items-center gap-1.5 rounded-md bg-sky-50 px-2.5 py-1 text-sm font-medium text-sky-800">
          <Check className="h-3.5 w-3.5" /> {value.name}
        </span>
        {!disabled && (
          <Button
            type="button"
            variant="ghost"
            size="sm"
            onClick={() => {
              onChange(null);
              setQuery("");
            }}
          >
            <X className="mr-1 h-3.5 w-3.5" /> Change
          </Button>
        )}
      </div>
    );
  }

  async function handleCreate() {
    try {
      const res = await createVendor.mutateAsync({ name: trimmed });
      onChange({ id: res.id, name: trimmed });
      setQuery("");
    } catch (err) {
      onCreateError?.(err instanceof Error ? err.message : "Couldn't add that vendor.");
    }
  }

  return (
    <div className="space-y-2">
      <Input
        id={inputId}
        value={query}
        autoFocus={autoFocus}
        disabled={disabled}
        placeholder={placeholder}
        aria-label={inputId ? undefined : "Search vendors"}
        aria-expanded
        aria-controls={listId}
        onChange={(e) => setQuery(e.target.value)}
      />
      <ul id={listId} className="max-h-56 overflow-auto rounded-md border border-slate-200 divide-y divide-slate-100">
        {vendors.isLoading ? (
          <li className="px-3 py-2 text-sm text-slate-500">Loading vendors…</li>
        ) : matches.length === 0 && !canCreate ? (
          <li className="px-3 py-2 text-sm text-slate-500">
            {all.length === 0 ? "No vendors yet — type a name to add one." : "No matches."}
          </li>
        ) : (
          matches.map((v) => (
            <li key={v.id}>
              <button
                type="button"
                disabled={disabled}
                onClick={() => {
                  onChange({ id: v.id, name: v.name });
                  setQuery("");
                }}
                className={cn(
                  "flex w-full items-center px-3 py-2 text-left text-sm text-slate-700",
                  "hover:bg-sky-50 focus:bg-sky-50 focus:outline-none",
                )}
              >
                {v.name}
              </button>
            </li>
          ))
        )}
        {canCreate && (
          <li>
            <button
              type="button"
              disabled={disabled || createVendor.isPending}
              onClick={handleCreate}
              className={cn(
                "flex w-full items-center px-3 py-2 text-left text-sm font-medium text-sky-700",
                "hover:bg-sky-50 focus:bg-sky-50 focus:outline-none disabled:opacity-60",
              )}
            >
              <Plus className="mr-1.5 h-4 w-4" />
              {createVendor.isPending ? "Adding…" : `Add new vendor "${trimmed}"`}
            </button>
          </li>
        )}
      </ul>
    </div>
  );
}
