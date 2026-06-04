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
export function fieldLabel(name: string): string {
  const known = FIELD_LABELS[name.trim().toLowerCase()];
  if (known) return known;
  const spaced = name
    .replace(/_/g, " ")
    .replace(/([a-z0-9])([A-Z])/g, "$1 $2")
    .trim();
  return spaced ? spaced.charAt(0).toUpperCase() + spaced.slice(1) : name;
}

// -------- Audit / activity actions --------

const ACTION_LABELS: Readonly<Record<string, string>> = {
  "document.created": "Document added",
  "document.uploaded": "Document uploaded",
  "document.updated": "Document updated",
  "document.deleted": "Document removed",
  "document.verified": "Document verified",
  "document.fields_edited": "Document details edited",
  "document.reextract_queued": "Document re-read",
  "vendor.created": "Vendor added",
  "vendor.updated": "Vendor updated",
  "vendor.deleted": "Vendor removed",
  "vendorportallink.created": "Portal link created",
  "vendorportallink.deleted": "Portal link revoked",
  "compliancetemplate.created": "Requirement set created",
  "compliancetemplate.updated": "Requirement set updated",
  "compliancetemplate.deleted": "Requirement set removed",
  "compliancerule.created": "Requirement added",
  "compliancerule.updated": "Requirement updated",
  "compliancerule.deleted": "Requirement removed",
  "user.registered": "Account created",
  "user.login": "Signed in",
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
