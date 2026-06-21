"use client";

import { DOCUMENT_TYPES, documentTypeLabel } from "@/lib/document-types";
import { cn } from "@/lib/utils";

type DocumentTypeSelectProps = {
  value: string;
  onChange: (value: string) => void;
  id?: string;
  disabled?: boolean;
  className?: string;
  "aria-label"?: string;
};

// Sentinel option value for a stored type that isn't in the canonical list
// (legacy data). Rendered disabled so it can be DISPLAYED but never re-selected;
// any onChange the user triggers always emits a real canonical value.
const UNKNOWN = "__current";

/**
 * Native <select> over the canonical document-type vocabulary
 * (`@/lib/document-types`). Native (not a custom popover) keeps it keyboard- and
 * screen-reader-accessible for free. Case-insensitive on the incoming value so
 * a lower-case `coi` and a legacy upper-case `COI` both resolve to the same
 * option. Module-scope per the static-components rule (#73). Reused by the
 * upload staging card and the document detail-page type editor (#186).
 */
export function DocumentTypeSelect({
  value,
  onChange,
  id,
  disabled,
  className,
  "aria-label": ariaLabel,
}: DocumentTypeSelectProps) {
  const norm = (value ?? "").trim().toLowerCase();
  const known = DOCUMENT_TYPES.some((t) => t.value === norm);

  return (
    <select
      id={id}
      value={known ? norm : UNKNOWN}
      disabled={disabled}
      aria-label={ariaLabel}
      onChange={(e) => onChange(e.target.value)}
      className={cn(
        "h-9 rounded-md border border-input bg-white px-2 text-sm text-slate-700",
        "focus:outline-none focus:ring-2 focus:ring-ring disabled:opacity-60",
        className,
      )}
    >
      {!known && value && (
        <option value={UNKNOWN} disabled>
          {documentTypeLabel(value)}
        </option>
      )}
      {DOCUMENT_TYPES.map((t) => (
        <option key={t.value} value={t.value}>
          {t.label}
        </option>
      ))}
    </select>
  );
}
