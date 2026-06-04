/**
 * Plain-English "Vendor requirements" catalog (#192).
 *
 * The Compliance Rules page used to make the user hand-type the engine's internal
 * tokens (snake_case field names, operators like `min_value`, unformatted numbers).
 * This module is the translation layer that lets the UI speak in sentences instead:
 *
 *   - REQUIREMENT_TYPES is a menu of pickable requirements, each of which maps to
 *     EXACTLY the `{documentType, fieldName, operator, expectedValue, errorMessage}`
 *     shape the existing `POST /api/compliance/templates/{id}/rules` already accepts
 *     (so this is a pure frontend change — no engine/migration work).
 *   - `requirementSentence()` is the reverse: a stored rule → the sentence shown in
 *     the read view. Unknown/legacy rules fall back to a readable generic sentence
 *     (never a raw token).
 *
 * Honesty note (verified against `ComplianceCheckService`): the engine has NO
 * "date in the future" operator. A `required` rule on `expiration_date` only checks
 * that a date was extracted; the Expired/ExpiringSoon status is computed separately
 * and automatically from the document's expiration date. The "must not be expired"
 * copy below is worded to stay truthful about that.
 */

import { fieldLabel, operatorLabel } from "@/lib/display-labels";

export type RequirementGroup = "Insurance" | "Dates" | "Licenses & permits";
export type RequirementValueKind = "money" | "text" | "none";

export type RequirementType = {
  key: string;
  group: RequirementGroup;
  /** Label shown in the "+ Add a requirement" menu. */
  menuLabel: string;
  documentType: string;
  fieldName: string;
  operator: string;
  valueKind: RequirementValueKind;
  /** Label for the single fill-in (money / text), when the requirement has one. */
  valueLabel?: string;
  valuePlaceholder?: string;
  /** Honest helper text shown under the fill-in / toggle. */
  helper?: string;
  /** The read-view sentence, built from the stored expectedValue. */
  sentence: (value: string | null) => string;
  /** The errorMessage stored on the rule (shown when a document fails the check). */
  errorMessage: (value: string | null) => string;
};

// ---------------------------------------------------------------------------
// Money formatting — the UI shows "$1,000,000"; the engine stores the bare
// integer "1000000". Pat never learns the no-commas rule.
// ---------------------------------------------------------------------------

export const MONEY_PRESETS = [500_000, 1_000_000, 2_000_000];

/** 1000000 → "$1,000,000". */
export function formatMoney(amount: number): string {
  return `$${Math.trunc(amount).toLocaleString("en-US")}`;
}

/**
 * "$1,000,000" / "1,000,000" / "1000000" → 1000000; blank / non-numeric → null.
 * Coverage limits are whole dollars, so a pasted decimal is TRUNCATED at the
 * point ("$1,000,000.50" → 1000000), never concatenated into 100000050.
 */
export function parseMoneyInput(raw: string): number | null {
  const digits = raw.split(".")[0].replace(/[^0-9]/g, "");
  if (digits === "") return null;
  const n = Number.parseInt(digits, 10);
  return Number.isFinite(n) ? n : null;
}

/** Format a stored expectedValue string as money, tolerating a non-numeric value. */
function moneyFromStored(value: string | null): string {
  const n = value == null ? null : Number.parseInt(value, 10);
  return n != null && Number.isFinite(n) ? formatMoney(n) : "the required amount";
}

const NOT_EXPIRED_HELPER =
  "We mark a document Expired the day its expiration date passes, and warn you 30 days before. " +
  "This requirement makes sure the date is on file in the first place.";

// ---------------------------------------------------------------------------
// The catalog. documentType is implied by the group, so the user never sees it.
// ---------------------------------------------------------------------------

export const REQUIREMENT_TYPES: RequirementType[] = [
  // ---- Insurance (Certificate of Insurance) ----
  {
    key: "general_liability",
    group: "Insurance",
    menuLabel: "General liability — minimum coverage",
    documentType: "coi",
    fieldName: "general_liability_limit",
    operator: "min_value",
    valueKind: "money",
    valueLabel: "Minimum coverage amount",
    sentence: (v) => `Carries at least ${moneyFromStored(v)} in general liability insurance`,
    errorMessage: (v) => `General liability is below the ${moneyFromStored(v)} you require.`,
  },
  {
    key: "auto_liability",
    group: "Insurance",
    menuLabel: "Auto liability — minimum coverage",
    documentType: "coi",
    fieldName: "auto_liability_limit",
    operator: "min_value",
    valueKind: "money",
    valueLabel: "Minimum coverage amount",
    sentence: (v) => `Carries at least ${moneyFromStored(v)} in auto liability coverage`,
    errorMessage: (v) => `Auto liability is below the ${moneyFromStored(v)} you require.`,
  },
  {
    key: "professional_liability",
    group: "Insurance",
    menuLabel: "Professional liability (E&O) — minimum coverage",
    documentType: "coi",
    fieldName: "professional_liability_limit",
    operator: "min_value",
    valueKind: "money",
    valueLabel: "Minimum coverage amount",
    sentence: (v) => `Carries at least ${moneyFromStored(v)} in professional liability (E&O) coverage`,
    errorMessage: (v) => `Professional liability (E&O) is below the ${moneyFromStored(v)} you require.`,
  },
  {
    key: "umbrella",
    group: "Insurance",
    menuLabel: "Umbrella / excess — minimum coverage",
    documentType: "coi",
    fieldName: "umbrella_limit",
    operator: "min_value",
    valueKind: "money",
    valueLabel: "Minimum coverage amount",
    sentence: (v) => `Carries at least ${moneyFromStored(v)} in umbrella / excess coverage`,
    errorMessage: (v) => `Umbrella / excess coverage is below the ${moneyFromStored(v)} you require.`,
  },
  {
    key: "workers_comp",
    group: "Insurance",
    menuLabel: "Carries workers' compensation",
    documentType: "coi",
    fieldName: "workers_comp_limit",
    operator: "required",
    valueKind: "none",
    sentence: () => "Carries workers' compensation coverage",
    errorMessage: () => "No workers' compensation coverage was found on the document.",
  },
  {
    key: "additional_insured",
    group: "Insurance",
    menuLabel: "Names you as additional insured",
    documentType: "coi",
    fieldName: "additional_insured",
    operator: "contains",
    valueKind: "text",
    valueLabel: "Name to look for",
    valuePlaceholder: "e.g. Riverside Event Hall",
    helper: "Enter your venue or company name exactly as it should appear on the certificate.",
    sentence: (v) => `Names “${(v ?? "").trim() || "your company"}” as additional insured`,
    errorMessage: (v) => `“${(v ?? "").trim() || "Your company"}” was not found as an additional insured.`,
  },

  // ---- Dates ----
  {
    key: "coi_not_expired",
    group: "Dates",
    menuLabel: "Document must not be expired",
    documentType: "coi",
    fieldName: "expiration_date",
    operator: "required",
    valueKind: "none",
    helper: NOT_EXPIRED_HELPER,
    sentence: () => "Insurance has not expired",
    errorMessage: () => "No expiration date was found, so we can’t confirm the insurance is current.",
  },

  // ---- Licenses & permits ----
  {
    key: "license_number",
    group: "Licenses & permits",
    menuLabel: "Has a license number on file",
    documentType: "license",
    fieldName: "license_number",
    operator: "required",
    valueKind: "none",
    sentence: () => "Has a license number on file",
    errorMessage: () => "No license number was found on the document.",
  },
  {
    key: "license_type",
    group: "Licenses & permits",
    menuLabel: "Holds a specific license type",
    documentType: "license",
    fieldName: "license_type",
    operator: "equals",
    valueKind: "text",
    valueLabel: "License type",
    valuePlaceholder: "e.g. CDL",
    sentence: (v) => `Holds a “${(v ?? "").trim() || "specific"}” license`,
    errorMessage: (v) => `The license type is not “${(v ?? "").trim() || "the one you require"}”.`,
  },
  {
    key: "license_not_expired",
    group: "Licenses & permits",
    menuLabel: "License must not be expired",
    documentType: "license",
    fieldName: "expiration_date",
    operator: "required",
    valueKind: "none",
    helper: NOT_EXPIRED_HELPER,
    sentence: () => "License has not expired",
    errorMessage: () => "No expiration date was found, so we can’t confirm the license is current.",
  },
];

export const REQUIREMENT_GROUPS: RequirementGroup[] = ["Insurance", "Dates", "Licenses & permits"];

/** The catalog item for a stored rule, matched on (documentType, fieldName, operator). */
export function findRequirementType(rule: {
  documentType: string;
  fieldName: string | null;
  operator: string;
}): RequirementType | undefined {
  return REQUIREMENT_TYPES.find(
    (t) =>
      t.documentType === rule.documentType &&
      t.fieldName === rule.fieldName &&
      t.operator === rule.operator,
  );
}

/**
 * The read-view sentence for a stored rule. A catalog match produces the curated
 * sentence; an unknown/legacy rule falls back to a readable generic sentence built
 * from the display-label maps — never a raw snake_case token or operator.
 */
export function requirementSentence(rule: {
  documentType: string;
  fieldName: string | null;
  operator: string;
  expectedValue: string | null;
}): string {
  const type = findRequirementType(rule);
  if (type) return type.sentence(rule.expectedValue);

  const field = fieldLabel(rule.fieldName) || "This document";
  if (rule.operator === "required") return `${field} must be present`;
  // Only the engine's known operators get an English phrasing; anything else
  // (a future operator) degrades to a neutral sentence rather than leaking the
  // raw token — keeping this function's "never a raw operator" promise true.
  if (!["equals", "contains", "min_value"].includes(rule.operator)) {
    return `${field} must meet a custom rule`;
  }
  const op = operatorLabel(rule.operator).toLowerCase();
  const value = rule.expectedValue?.trim();
  return value ? `${field} ${op} ${value}` : `${field} ${op}`;
}
