/**
 * Canonical document-type vocabulary — the single source of truth the UI shares
 * for the upload picker, the orphaned-row / detail-page type editors, and any
 * value→label rendering.
 *
 * The `value`s mirror the backend exactly. The backend's single source of truth
 * is `CanonicalDocumentTypes.All` in `api/.../Services/CanonicalDocumentTypes.cs`
 * (#373, ADR 0045); every server-side copy — both provider schemas, the extraction
 * prompt's DOCUMENT TYPES block, `DisplayLabels.DocumentTypes` — is pinned equal
 * to it by `CanonicalDocumentTypeTests`. (#389 deleted the PATCH endpoint's own
 * `AllowedDocumentTypes` set, which this comment used to name; those endpoints now
 * call `CanonicalDocumentTypes` directly.)
 *
 * THIS FILE IS THE ONE MIRROR NO .NET TEST CAN REACH — there is no shared fixture
 * here, unlike ADR 0038's contact-email corpus — so this comment is its only guard.
 * A mismatch lets the UI submit a type the server rejects, and worse, a value the
 * server would store outside the vocabulary grades against zero compliance rules.
 * Keep the two lists in lockstep (#186, ADR 0045 "Known limitation").
 *
 * The `label`s are the human-facing names an SMB venue manager recognizes.
 * #188 humanizes status/jargon copy app-wide and reuses `documentTypeLabel`
 * here as its document-type source — don't fork a second map there.
 */
export const DOCUMENT_TYPES = [
  { value: "coi", label: "Certificate of Insurance" },
  { value: "license", label: "Business License" },
  { value: "permit", label: "Permit" },
  { value: "certification", label: "Certification" },
  { value: "contract", label: "Contract" },
  { value: "other", label: "Other" },
] as const;

const LABELS: Readonly<Record<string, string>> = Object.fromEntries(
  DOCUMENT_TYPES.map((t) => [t.value, t.label]),
);

/**
 * Human label for a stored document-type value. Case-insensitive so it resolves
 * both the backend's lower-case `coi` and any legacy/upper-case `COI`. An empty
 * or null type means "not yet classified" → "Other". A genuinely unknown
 * non-empty value is returned verbatim rather than hidden, so unexpected data
 * stays visible instead of silently collapsing to "Other".
 */
export function documentTypeLabel(value: string | null | undefined): string {
  if (!value || !value.trim()) return "Other";
  return LABELS[value.trim().toLowerCase()] ?? value;
}

/**
 * The vocabulary value a stored document type resolves to, or null when the
 * stored string resolves to nothing in the list.
 *
 * `documentTypeLabel` deliberately normalizes case, so a legacy `"COI"` renders
 * as "Certificate of Insurance" and looks identical to a canonical `"coi"`. The
 * backend does NOT normalize: `ComputeOutcome`'s applicable-rules filter compares
 * `DocumentType` with an ORDINAL, case-sensitive `==`, so a `"COI"` document
 * matches zero `"coi"` rules and is never graded (ADR 0048 § Context / ADR 0045's
 * un-laundered legacy rows). The "Not checked yet" card needs to tell those two
 * apart — one is "add a requirement", the other is "the stored value is wrong" —
 * so compare the RAW value with this function's result rather than with a label.
 *
 * PRESENTATIONAL ONLY: it decides which remedy the card leads with, never which
 * cause the card names — the cause comes from the backend (ADR 0048 §4).
 */
export function canonicalDocumentType(value: string | null | undefined): string | null {
  if (!value || !value.trim()) return null;
  const norm = value.trim().toLowerCase();
  return DOCUMENT_TYPES.some((t) => t.value === norm) ? norm : null;
}
