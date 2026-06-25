/**
 * Canonical document-type vocabulary — the single source of truth the UI shares
 * for the upload picker, the orphaned-row / detail-page type editors, and any
 * value→label rendering.
 *
 * The `value`s mirror the backend exactly: the LLM extraction prompt
 * (`api/.../Services/Extraction/ExtractionPrompts.cs`) and the PATCH endpoint's
 * `AllowedDocumentTypes` set both speak this same lower-case vocabulary. A
 * mismatch would let the UI submit a type the server rejects, so keep the two
 * lists in lockstep (#186).
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
