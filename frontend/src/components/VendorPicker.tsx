"use client";

import { useId, useMemo, useState, type KeyboardEvent } from "react";
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
 * with a Change button; when null it shows a search input plus the filtered
 * option list.
 *
 * Accessibility model (FP-130): a real ARIA 1.2 combobox. Focus STAYS in the
 * input; the options are non-tab-stop `<li role="option">` and a virtual
 * highlight moves through them via `aria-activedescendant` (ArrowUp/Down),
 * Enter commits the highlighted option (or creates a new vendor when nothing's
 * highlighted and the typed name is new), Escape clears, and a pointer click
 * selects directly. The "Add new vendor" affordance stays a real `<button>`
 * OUTSIDE the listbox (it's an action, not an option).
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
  // Active-descendant index for keyboard nav (FP-130): which option the ArrowUp/Down
  // highlight sits on. -1 = none highlighted (focus is in the input, nothing virtual-focused).
  const [activeIndex, setActiveIndex] = useState(-1);
  const generatedId = useId();
  const listId = `${inputId ?? generatedId}-options`;
  const optionId = (i: number) => `${listId}-opt-${i}`;

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

  function selectAt(i: number) {
    const v = matches[i];
    if (!v) return;
    onChange({ id: v.id, name: v.name });
    setQuery("");
  }

  // Combobox keyboard model (FP-130): focus stays in the input; Arrow keys move a virtual
  // highlight (aria-activedescendant) through the options, Enter commits the highlighted vendor
  // (or creates one when nothing's highlighted and the typed name is new), Escape clears.
  function handleKeyDown(e: KeyboardEvent<HTMLInputElement>) {
    if (e.key === "ArrowDown") {
      e.preventDefault();
      // From the cold (-1) state ArrowDown lands on the first option; from the last it wraps to first.
      if (matches.length > 0) setActiveIndex((i) => (i >= matches.length - 1 ? 0 : i + 1));
    } else if (e.key === "ArrowUp") {
      e.preventDefault();
      // From the cold (-1) state ArrowUp lands on the LAST option (idiomatic APG); from the first it wraps.
      if (matches.length > 0) setActiveIndex((i) => (i <= 0 ? matches.length - 1 : i - 1));
    } else if (e.key === "Enter") {
      if (activeIndex >= 0 && activeIndex < matches.length) {
        e.preventDefault();
        selectAt(activeIndex);
      } else if (canCreate && !createVendor.isPending) {
        e.preventDefault();
        void handleCreate();
      }
    } else if (e.key === "Escape" && query) {
      e.preventDefault();
      setQuery("");
      setActiveIndex(-1);
    }
  }

  // A real (non-permanent) expanded state now that there's a combobox role to anchor it:
  // the listbox is "open" only while it has something actionable to show. The result count
  // feeds a polite live region so a screen-reader user hears the filter narrowing.
  const hasOptions = matches.length > 0 || canCreate;
  const activeDescendant =
    activeIndex >= 0 && activeIndex < matches.length ? optionId(activeIndex) : undefined;

  return (
    <div className="space-y-2">
      <Input
        id={inputId}
        value={query}
        autoFocus={autoFocus}
        disabled={disabled}
        placeholder={placeholder}
        aria-label={inputId ? undefined : "Search vendors"}
        // Real ARIA 1.2 combobox (FP-130): the role anchors the (now meaningful) aria-expanded,
        // aria-autocomplete tells AT the list filters as you type, and aria-activedescendant points
        // at the Arrow-key-highlighted option so the highlight is announced without moving DOM focus.
        role="combobox"
        aria-autocomplete="list"
        aria-expanded={hasOptions}
        aria-controls={listId}
        aria-activedescendant={activeDescendant}
        onKeyDown={handleKeyDown}
        onChange={(e) => {
          setQuery(e.target.value);
          setActiveIndex(-1);
        }}
      />
      {/* Polite result-count so a screen-reader user hears the filter narrow — silent filtering was
          half the FP-130 finding. Only announced while actively filtering (a non-empty query). */}
      <div role="status" aria-live="polite" className="sr-only">
        {trimmed.length === 0
          ? ""
          : matches.length > 0
            ? `${matches.length} ${matches.length === 1 ? "vendor" : "vendors"} found`
            : canCreate
              ? `No matches. Press Enter to add "${trimmed}".`
              : "No matching vendors."}
      </div>
      <ul
        id={listId}
        role="listbox"
        aria-label="Vendors"
        className="max-h-56 overflow-auto rounded-md border border-slate-200 divide-y divide-slate-100"
      >
        {vendors.isLoading ? (
          <li className="px-3 py-2 text-sm text-slate-500">Loading vendors…</li>
        ) : matches.length === 0 && !canCreate ? (
          <li className="px-3 py-2 text-sm text-slate-500">
            {all.length === 0 ? "No vendors yet — type a name to add one." : "No matches."}
          </li>
        ) : (
          matches.map((v, i) => (
            // The option IS the <li role="option"> — focus stays in the input (combobox model), so
            // options aren't tab stops; click selects, ArrowKeys+Enter select, hover tracks the highlight.
            <li
              key={v.id}
              id={optionId(i)}
              role="option"
              aria-selected={i === activeIndex}
              onClick={() => !disabled && selectAt(i)}
              onMouseEnter={() => setActiveIndex(i)}
              className={cn(
                "flex w-full cursor-pointer items-center px-3 py-2 text-left text-sm text-slate-700",
                "hover:bg-sky-50",
                i === activeIndex && "bg-sky-50",
                disabled && "cursor-not-allowed opacity-60",
              )}
            >
              {v.name}
            </li>
          ))
        )}
        {canCreate && (
          // The create affordance is an ACTION, not a vendor option, so it stays a real <button>
          // OUTSIDE the option set (and the role="listbox" semantics). Enter-in-input also triggers it
          // when nothing's highlighted, so it's reachable by keyboard without being a listbox option.
          <li role="presentation">
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
