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

export type RequirementGroup = "Insurance" | "Dates" | "Licenses & permits" | "Certifications";
type RequirementValueKind = "money" | "text" | "none";

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
 *
 * k/m suffixes are honored (#319 FP-084): "2M" → 2000000, "1.5M" → 1500000,
 * "500k" → 500000. Before this, the non-digit strip turned "2M" into $2 — an
 * always-green requirement (every COI on earth carries more than $2). The suffix
 * is matched case-insensitively with an optional decimal multiplier.
 */
export function parseMoneyInput(raw: string): number | null {
  const trimmed = raw.trim().toLowerCase();
  if (trimmed === "") return null;
  // Leading $/space/commas, a number (with optional decimal), then a k or m unit — END-ANCHORED so
  // trailing junk ("2MB", "2m!!") doesn't get read as "$2,000,000"; it falls through to whole-dollars.
  const suffix = trimmed.match(/^[\s$,]*([0-9]+(?:\.[0-9]+)?)\s*([km])\s*$/);
  if (suffix) {
    const base = Number.parseFloat(suffix[1]);
    if (!Number.isFinite(base)) return null;
    return Math.round(base * (suffix[2] === "m" ? 1_000_000 : 1_000));
  }
  // No suffix: whole dollars — truncate at the decimal point, strip separators.
  const digits = trimmed.split(".")[0].replace(/[^0-9]/g, "");
  if (digits === "") return null;
  const n = Number.parseInt(digits, 10);
  return Number.isFinite(n) ? n : null;
}

/**
 * A coverage amount this small (< $10,000) is almost certainly a typo — a "$2"
 * GL minimum passes every certificate on earth (#319 FP-084). Drives a
 * non-blocking "did you mean…?" nudge in the requirement form. 0 / null are not
 * flagged (the form already blocks an empty amount).
 */
const SUSPICIOUSLY_LOW_MONEY = 10_000;
export function isSuspiciouslyLowMoney(amount: number | null): boolean {
  return amount != null && amount > 0 && amount < SUSPICIOUSLY_LOW_MONEY;
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
// Additional-insured claim copy (#396 / CLM-1).
//
// An ACORD 25 face confers no rights and only INDICATES additional-insured status; the status
// itself lives in a policy endorsement (CG 20 26-class). The corrected copy therefore says
// "certificate indicates…", not the categorical "Names…". It is STAGED behind the server flag
// `features.correctedAdditionalInsuredWording` (ComplianceClaims:CorrectedAdditionalInsuredWording,
// default OFF) pending the TX-attorney sign-off on CLM-1 (docs/rule-engine/G1-COUNSEL-BRIEF.md §0,
// TRR §3, ADR 0042). `corrected=false` returns TODAY'S EXACT copy byte-for-byte — the flag-off /
// flag-unknown default that keeps prod unchanged — and the catalog entry below delegates to it so
// the legacy strings live in exactly one place.
// ---------------------------------------------------------------------------

function additionalInsuredName(value: string | null, fallback: string): string {
  return (value ?? "").trim() || fallback;
}

/** The additional-insured read-view sentence. `corrected=false` is today's exact copy. */
export function additionalInsuredSentence(value: string | null, corrected: boolean): string {
  const name = additionalInsuredName(value, "your company");
  return corrected
    ? `Certificate indicates “${name}” as additional insured`
    : `Names “${name}” as additional insured`;
}

/** The additional-insured failure message (also the errorMessage stored on the rule).
 *  `corrected=false` is today's exact copy. */
export function additionalInsuredError(value: string | null, corrected: boolean): string {
  const name = additionalInsuredName(value, "Your company");
  return corrected
    ? `The certificate does not indicate “${name}” as an additional insured.`
    : `“${name}” was not found as an additional insured.`;
}

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
  // Gives the seeded "Caterer" liquor-liability rule a real catalog home (#400) — bar / alcohol
  // service (dram-shop / social-host) exposure that general liability excludes. Without this the
  // graded seed rule rendered as a context-free raw-token fallback with no Edit or re-add path
  // (the FP-085 defect class already fixed for the Security Service certification rule below).
  {
    key: "liquor_liability",
    group: "Insurance",
    menuLabel: "Liquor liability — minimum coverage",
    documentType: "coi",
    fieldName: "liquor_liability_limit",
    operator: "min_value",
    valueKind: "money",
    valueLabel: "Minimum coverage amount",
    sentence: (v) => `Carries at least ${moneyFromStored(v)} in liquor liability coverage`,
    errorMessage: (v) => `Liquor liability is below the ${moneyFromStored(v)} you require.`,
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
    // #396 (CLM-1): the catalog copy is the LEGACY (flag-off) wording — byte-identical to pre-#396.
    // The corrected "certificate indicates…" variant is served by requirementSentence /
    // requirementErrorMessage when the server flag is on. Single-sourced via the helpers above.
    sentence: (v) => additionalInsuredSentence(v, false),
    errorMessage: (v) => additionalInsuredError(v, false),
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

  // ---- Certifications ----
  // Gives the seeded "Security Service" certification rule a real catalog home (#319 FP-085) —
  // it used to render as a context-free fallback with no Edit and no re-add path.
  {
    key: "certification_not_expired",
    group: "Certifications",
    menuLabel: "Certification must not be expired",
    documentType: "certification",
    fieldName: "expiration_date",
    operator: "required",
    valueKind: "none",
    helper: NOT_EXPIRED_HELPER,
    sentence: () => "Certification has not expired",
    errorMessage: () => "No expiration date was found, so we can’t confirm the certification is current.",
  },
  {
    key: "certification_number",
    group: "Certifications",
    menuLabel: "Has a certification number on file",
    documentType: "certification",
    fieldName: "certification_number",
    operator: "required",
    valueKind: "none",
    sentence: () => "Has a certification number on file",
    errorMessage: () => "No certification number was found on the document.",
  },
  {
    key: "certification_name",
    group: "Certifications",
    menuLabel: "Holds a specific certification",
    documentType: "certification",
    fieldName: "certification_name",
    operator: "equals",
    valueKind: "text",
    valueLabel: "Certification name",
    valuePlaceholder: "e.g. Certified Protection Professional",
    sentence: (v) => `Holds a “${(v ?? "").trim() || "specific"}” certification`,
    errorMessage: (v) => `The certification is not “${(v ?? "").trim() || "the one you require"}”.`,
  },
];

export const REQUIREMENT_GROUPS: RequirementGroup[] = [
  "Insurance",
  "Dates",
  "Licenses & permits",
  "Certifications",
];

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
 * The engine operators that get an English phrasing in {@link requirementSentence}; any
 * other (future) operator degrades to a neutral sentence rather than leaking the raw token.
 */
const PHRASED_OPERATORS: readonly string[] = ["equals", "contains", "min_value"];

/**
 * Options carried by {@link requirementSentence} / {@link requirementErrorMessage}. #396 (CLM-1):
 * `correctedAdditionalInsuredWording` mirrors the server flag `features.correctedAdditionalInsuredWording`;
 * when true the additional-insured copy uses the honest "certificate indicates…" wording. Absent /
 * undefined (the flag-off / flag-unknown default) keeps today's exact copy.
 */
export type RequirementCopyOptions = { correctedAdditionalInsuredWording?: boolean };

/**
 * The read-view sentence for a stored rule. A catalog match produces the curated
 * sentence; an unknown/legacy rule falls back to a readable generic sentence built
 * from the display-label maps — never a raw snake_case token or operator.
 *
 * `opts.correctedAdditionalInsuredWording` (#396 / CLM-1) swaps ONLY the additional-insured
 * sentence to its staged corrected wording; every other requirement is flag-independent.
 */
export function requirementSentence(
  rule: {
    documentType: string;
    fieldName: string | null;
    operator: string;
    expectedValue: string | null;
  },
  opts?: RequirementCopyOptions,
): string {
  const type = findRequirementType(rule);
  if (type) {
    if (type.key === "additional_insured") {
      return additionalInsuredSentence(rule.expectedValue, opts?.correctedAdditionalInsuredWording === true);
    }
    return type.sentence(rule.expectedValue);
  }

  const field = fieldLabel(rule.fieldName) || "This document";
  if (rule.operator === "required") return `${field} must be present`;
  // An unknown operator degrades to a neutral sentence — keeps the "never a raw operator" promise.
  if (!PHRASED_OPERATORS.includes(rule.operator)) {
    return `${field} must meet a custom rule`;
  }
  const op = operatorLabel(rule.operator).toLowerCase();
  const value = rule.expectedValue?.trim();
  return value ? `${field} ${op} ${value}` : `${field} ${op}`;
}

/**
 * The errorMessage stored on a rule when it is created / edited — the `type.errorMessage(value)` a
 * caller would otherwise inline. #396 (CLM-1): `opts.correctedAdditionalInsuredWording` swaps ONLY
 * the additional-insured message to its staged corrected wording; every other requirement's catalog
 * errorMessage is flag-independent. Absent / undefined keeps today's exact copy.
 */
export function requirementErrorMessage(
  type: RequirementType,
  value: string | null,
  opts?: RequirementCopyOptions,
): string {
  if (type.key === "additional_insured") {
    return additionalInsuredError(value, opts?.correctedAdditionalInsuredWording === true);
  }
  return type.errorMessage(value);
}
