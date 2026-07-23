/**
 * Human-facing display labels for the enum / snake_case / camelCase values the
 * backend speaks. The UI must never render a raw enum ("NonCompliant"),
 * snake_case field name ("general_liability_limit"), or dotted audit action
 * ("compliancetemplate.created") — those read like error codes to an SMB venue
 * manager. (#188)
 *
 * The backend mirror is `api/.../Services/DisplayLabels.cs` — it produces the
 * SAME strings for the exported PDF/CSV so the app and the export agree. Keep
 * the two in lockstep.
 */

// -------- Compliance status --------

const COMPLIANCE_STATUS_LABELS: Readonly<Record<string, string>> = {
  Pending: "Awaiting review",
  Compliant: "Compliant",
  NonCompliant: "Action needed",
  ExpiringSoon: "Expiring soon",
  Expired: "Expired",
};

export function complianceStatusLabel(status: string | null | undefined): string {
  if (!status) return "Awaiting review";
  return COMPLIANCE_STATUS_LABELS[status] ?? status;
}

// -------- Extraction status (the "are we done reading it" machine state) --------

const EXTRACTION_STATUS_LABELS: Readonly<Record<string, string>> = {
  Pending: "Waiting to read",
  Processing: "Reading…",
  Completed: "Read",
  ManualRequired: "Needs your review",
  Failed: "Couldn't read",
};

export function extractionStatusLabel(status: string | null | undefined): string {
  if (!status) return "Waiting to read";
  return EXTRACTION_STATUS_LABELS[status] ?? status;
}

// -------- Extracted field names --------

const FIELD_LABELS: Readonly<Record<string, string>> = {
  policyholder_name: "Policyholder",
  insurer_name: "Insurer",
  policy_number: "Policy number",
  effective_date: "Effective date",
  expiration_date: "Expiration date",
  general_liability_limit: "General liability limit",
  workers_comp_limit: "Workers' comp limit",
  auto_liability_limit: "Auto liability limit",
  umbrella_limit: "Umbrella limit",
  professional_liability_limit: "Professional liability limit",
  liquor_liability_limit: "Liquor liability limit",
  certificate_holder: "Certificate holder",
  description_of_operations: "Description of operations",
  additional_insured: "Additional insured",
  holder_name: "Holder",
  license_number: "License number",
  license_type: "License type",
  issuing_authority: "Issuing authority",
  issue_date: "Issue date",
  state: "State",
  permit_number: "Permit number",
  permit_type: "Permit type",
  property_address: "Property address",
  certification_name: "Certification",
  certifying_body: "Certifying body",
  certification_number: "Certification number",
};

/**
 * Human label for an extracted field name. Known fields get a curated label;
 * an unknown one falls back to a sentence-cased de-snaked / de-camelCased form
 * (so it's still readable, never a raw `some_new_field`).
 */
export function fieldLabel(name: string | null | undefined): string {
  if (!name) return "";
  const known = FIELD_LABELS[name.trim().toLowerCase()];
  if (known) return known;
  const spaced = name
    .replace(/_/g, " ")
    .replace(/([a-z0-9])([A-Z])/g, "$1 $2")
    .trim();
  return spaced ? spaced.charAt(0).toUpperCase() + spaced.slice(1) : name;
}

// -------- Audit / activity actions --------

// Keyed lower-case; lookups lower-case the input, so this resolves BOTH the
// interceptor's all-lower-case entity actions (`compliancetemplate.created`)
// AND the explicit camelCase ones (`complianceRule.upserted`,
// `vendorPortalLink.revoked`). Keep in sync with DisplayLabels.cs's Actions.
const ACTION_LABELS: Readonly<Record<string, string>> = {
  "document.created": "Document added",
  "document.uploaded": "Document uploaded",
  "document.updated": "Document updated",
  "document.deleted": "Document removed",
  "document.verified": "Document verified",
  "document.fields_edited": "Document details edited",
  "document.reextract_queued": "Document re-read",
  "document.processed": "Document read",
  "documentfield.created": "Document detail added",
  "documentfield.updated": "Document detail edited",
  "vendor.created": "Vendor added",
  "vendor.updated": "Vendor updated",
  "vendor.deleted": "Vendor removed",
  "vendorportallink.created": "Portal link created",
  "vendorportallink.revoked": "Portal link revoked",
  "vendorportallink.deleted": "Portal link revoked",
  "vendorportallink.emailed": "Upload link emailed",
  "vendorportallink.upload_processed": "Vendor sent a document",
  "compliancetemplate.created": "Requirement set created",
  "compliancetemplate.updated": "Requirement set updated",
  "compliancetemplate.deleted": "Requirement set removed",
  "compliancerule.created": "Requirement added",
  "compliancerule.updated": "Requirement updated",
  "compliancerule.upserted": "Requirement saved",
  "compliancerule.deleted": "Requirement removed",
  "reminder.recipient_suppressed": "Reminders paused — bad email",
  "user.registered": "Account created",
  "user.logged_in": "Signed in",
  "user.login_failed": "Sign-in failed",
  "user.password_changed": "Password changed",
  "user.password_reset": "Password reset",
  "user.password_reset_requested": "Password reset requested",
  "user.email_verified": "Email verified",
  "user.email_changed": "Email changed",
  "user.email_change_requested": "Email change requested",
  "user.account_deleted": "Account deleted",
};

/**
 * Plain-English label for an audit action key (e.g. "compliancetemplate.created"
 * → "Requirement set created"). Unknown keys fall back to a de-dotted,
 * de-snaked, de-camelCased, title-cased form — the previous `prettyAction`
 * split on `.`/`_` only and so garbled all-lower-case entity names like
 * "compliancetemplate" into "Compliancetemplate". (#188)
 */
export function actionLabel(action: string): string {
  const known = ACTION_LABELS[action.trim().toLowerCase()];
  if (known) return known;
  return action
    .replace(/([a-z0-9])([A-Z])/g, "$1 $2")
    .replace(/\./g, " · ")
    .replace(/_/g, " ")
    .replace(/\b\w/g, (c) => c.toUpperCase());
}

// -------- Relative time (activity feed) --------

/**
 * Compact relative time for the activity feed ("just now", "5m ago", "3h ago",
 * "2d ago"), falling back to a localized date for anything older than ~a week —
 * far friendlier than a raw `toLocaleString()` timestamp for a "what just
 * happened" feed (#318 FP-049). Pure given `now` (defaults to the current time)
 * so it's deterministically testable. A future/clock-skewed timestamp reads
 * "just now" rather than a nonsensical negative.
 */
export function relativeTime(iso: string | null | undefined, now: number = Date.now()): string {
  if (!iso) return "";
  const then = new Date(iso).getTime();
  if (!Number.isFinite(then)) return "";
  const diffSec = Math.round((now - then) / 1000);
  if (diffSec < 45) return "just now";
  const min = Math.round(diffSec / 60);
  if (min < 60) return `${min}m ago`;
  const hr = Math.round(min / 60);
  if (hr < 24) return `${hr}h ago`;
  const days = Math.round(hr / 24);
  if (days < 7) return `${days}d ago`;
  return new Date(then).toLocaleDateString();
}

// -------- Compliance-rule operators (the rules / requirements page) --------

const OPERATOR_LABELS: Readonly<Record<string, string>> = {
  required: "Must be present",
  equals: "Must equal",
  contains: "Must contain",
  min_value: "Must be at least",
};

/** Human label for a compliance-rule operator (e.g. "min_value" → "Must be at least"). */
export function operatorLabel(op: string | null | undefined): string {
  if (!op) return "";
  return OPERATOR_LABELS[op.trim().toLowerCase()] ?? op;
}

// -------- Reminder email delivery status --------

const DELIVERY_STATUS_LABELS: Readonly<Record<string, string>> = {
  sent: "Sent",
  delivered: "Delivered",
  opened: "Opened",
  clicked: "Clicked",
  failed: "Couldn't send",
  bounced: "Bounced — bad address",
  complained: "Marked as spam",
};

/**
 * Human label for a Resend delivery status (e.g. "bounced" → "Bounced — bad
 * address"). The status set is driven by the Resend webhook + the precedence
 * ladder (sent < delivered < opened < clicked), so cover all of them — and
 * sentence-case any future token as a fallback so a new status can never leak
 * as a raw lowercase code. (#188)
 */
export function deliveryStatusLabel(status: string | null | undefined): string {
  if (!status) return "";
  const key = status.trim().toLowerCase();
  if (DELIVERY_STATUS_LABELS[key]) return DELIVERY_STATUS_LABELS[key];
  return key.charAt(0).toUpperCase() + key.slice(1);
}

// -------- Compliance-check failure explanations (#193) --------

/**
 * The subset of a compliance-check row the detail page needs to explain a
 * failure. Mirrors the backend `ComplianceCheckDto` (camelCased by JSON
 * serialization).
 */
export type ComplianceCheckLike = {
  isPassed: boolean;
  ruleErrorMessage?: string | null;
  ruleFieldName?: string | null;
  ruleOperator?: string | null;
  ruleExpectedValue?: string | null;
  actualValue?: string | null;
  notes?: string | null;
};

/**
 * Byte-for-byte mirror of `ComplianceCheckService.UnreadableValueNote` (api). #383 / ADR 0040: a
 * canonical value we could NOT parse fails its rule and the server stamps THIS note on the check.
 *
 * The affected rules' catalog `errorMessage` asserts the value was "not found" (e.g. "No expiration
 * date was found …") — false when a value WAS found and we merely couldn't read it, and the exact
 * fact ADR 0040 exists to distinguish. So `complianceFailureReason` keys on this note (a server
 * verdict, not a re-implementation of parsing on the client) to swap that misleading base for the
 * honest "we couldn't read" statement. The backend pins its side with `UnreadableValueNote` tests;
 * `display-labels.test.ts` pins this string, so an edit to one that forgets the other goes red.
 */
export const UNREADABLE_VALUE_NOTE =
  "We couldn't read this value, so we can't confirm this requirement. Check the document and correct it.";

// Field names whose values are dollar amounts — rendered as US currency so the
// failure reason reads "$1,000,000", not "1000000".
const MONEY_FIELD = /limit|liabilit|amount|coverage/i;

/**
 * Format a stored field value for display. Money-ish fields whose value is a
 * bare number render as whole-dollar US currency ("1000000" → "$1,000,000");
 * everything else renders trimmed-verbatim. Returns null for an empty value so
 * the caller can say "missing" instead of printing an empty string. (#193)
 */
export function formatCheckValue(
  fieldName: string | null | undefined,
  value: string | null | undefined,
): string | null {
  const trimmed = value?.trim();
  if (!trimmed) return null;
  if (fieldName && MONEY_FIELD.test(fieldName)) {
    const n = Number(trimmed.replace(/[$,\s]/g, ""));
    if (Number.isFinite(n) && trimmed.replace(/[$,\s]/g, "") !== "") {
      return n.toLocaleString("en-US", {
        style: "currency",
        currency: "USD",
        maximumFractionDigits: 0,
      });
    }
  }
  return trimmed;
}

/**
 * Plain-English explanation of WHY one requirement failed, for the
 * document-detail "Why isn't this compliant?" card. Prefers the owner-authored
 * requirement text (`ruleErrorMessage`); otherwise synthesizes one from the
 * operator + field + expected value. Appends what the document actually shows
 * when a value was extracted, or a "couldn't find this" note when it's missing.
 * Never surfaces a raw operator token or snake_case field name. (#193)
 *
 * Exception: an UNREADABLE canonical value (#383 / ADR 0040). The catalog
 * `errorMessage` for these rules claims the value was "not found", but here one
 * WAS found and we couldn't parse it — so we ignore the misleading owner text
 * and state honestly that we couldn't read the field, still showing the raw
 * value so the user can see what to correct. Keyed on the server's own note, so
 * the client never re-decides "is this readable?"; when this note is present the
 * check always carries the raw text in `actualValue`.
 */
export function complianceFailureReason(check: ComplianceCheckLike): string {
  if (!check.isPassed && check.notes?.trim() === UNREADABLE_VALUE_NOTE) {
    // Parallel to the terse "… — this document shows X." form below, and audience-neutral: this same
    // sentence is reused in the vendor-email body, so it carries no owner-only "edit it here" CTA (the
    // ManualReviewCard on the detail page owns that instruction).
    const label = fieldLabel(check.ruleFieldName).toLowerCase() || "value";
    const raw = formatCheckValue(check.ruleFieldName, check.actualValue);
    return raw
      ? `We couldn't read the ${label} — this document shows ${raw}.`
      : `We couldn't read the ${label} on this document.`;
  }

  const expected = formatCheckValue(check.ruleFieldName, check.ruleExpectedValue);
  const synthesized = [
    fieldLabel(check.ruleFieldName),
    operatorLabel(check.ruleOperator).toLowerCase(),
    expected,
  ]
    .filter(Boolean)
    .join(" ")
    .trim();
  const base = (
    check.ruleErrorMessage?.trim() ||
    synthesized ||
    "This requirement wasn't met."
  ).replace(/[.\s]+$/, "");

  const actual = formatCheckValue(check.ruleFieldName, check.actualValue);
  return actual
    ? `${base} — this document shows ${actual}.`
    : `${base} — we couldn't find this on the document.`;
}

// -------- Document processing-error codes (#193) --------

// Maps the codes ExtractionWorker.MarkFailed writes ("code: detail") to copy a
// venue manager can act on. The raw string goes in the "Details for support"
// disclosure, never in the headline.
// Body copy deliberately says "this file" (never "this document") so it can sit
// directly under the card's "We couldn't read this document" headline without a
// duplicate-phrase clash in tests that match the headline by text.
const PROCESSING_ERROR_LABELS: Readonly<Record<string, string>> = {
  "extraction.too_many_attempts":
    "We tried several times but couldn't read this file. It may be blurry, password-protected, or not a type we recognize — try uploading a clearer copy.",
  // The monthly limit genuinely resets at the start of each UTC month (#256) — but an
  // already-failed document is NOT re-read automatically: the user presses "Read again"
  // (document detail header) once the new month starts. Don't promise auto-recovery.
  "extraction.cost_ceiling_hit":
    "We couldn't read this file because your account hit its monthly processing limit. The limit resets at the start of next month — open this document and press “Read again” then, or contact support to raise it.",
  "extraction.failed":
    "Something went wrong while reading this file. Try uploading it again, or contact support if it keeps happening.",
};

/**
 * Map a raw `processingError` — either "code: detail" from `MarkFailed` or a
 * bare exception message from a mid-retry failure — to plain-English copy.
 * Unknown codes and raw exceptions fall back to a generic line; the raw value
 * is NEVER returned (it belongs in the support disclosure). (#193)
 */
export function processingErrorMessage(raw: string | null | undefined): string {
  const code = raw?.split(":", 1)[0]?.trim().toLowerCase() ?? "";
  return (
    PROCESSING_ERROR_LABELS[code] ??
    "We weren't able to read this file. Try uploading it again, or contact support if it keeps happening."
  );
}
