/**
 * The single definition of "is this vendor contact email obviously malformed" (#369).
 *
 * The vendors LIST add-form has guarded this since FP-076; the vendor DETAIL edit form
 * never did, and the backend only trimmed — so a typo saved through the edit path landed
 * a 200 OK and then broke every reminder send silently (they retry in place, ADR 0025,
 * surfacing nothing to the operator unless Resend happens to bounce). #369 *is* what
 * happened when one form owned the only copy of this rule, so the fix is a shared
 * predicate rather than a second copy pasted into the detail page — the same reasoning
 * that produced `PlanDocumentScope` (#367) and `DocumentSupersession` (#327).
 *
 * `Services/ContactEmail.cs` is the server-side mirror and MUST agree with this file:
 * the client guard exists to explain the problem inline, the server check exists because
 * the API is reachable without either form. If they drift, one side silently accepts what
 * the other rejects — the exact class of bug this ticket is.
 *
 * Deliberately NOT a full RFC 5322 parser. The goal is to catch the typo that costs a
 * customer their reminders (`jane@acme,com`, a pasted `Jane Smith <jane@acme.com>`),
 * not to adjudicate exotic-but-legal addresses; over-strict validation that rejects a
 * real address is a worse failure than letting an odd one through.
 */

/** Max persisted length — `Vendor.ContactEmail` is `varchar(256)` (ModelConfiguration). */
export const CONTACT_EMAIL_MAX_LENGTH = 256;

/**
 * Non-empty local part, a single `@`, and a dotted domain — no whitespace anywhere.
 * Rejects `jane@acme,com` (no dot in the domain) and `Jane Smith <jane@acme.com>`
 * (whitespace in the local part).
 */
const WELL_FORMED = /^[^\s@]+@[^\s@]+\.[^\s@]+$/;

/**
 * True when `raw` is present but not a usable address. Blank is NOT malformed: a vendor
 * with no contact email is a supported state (the detail page already explains that the
 * "Email link" button needs one), so blank must stay saveable.
 *
 * Callers compare against the TRIMMED value — see `trimContactEmail`.
 */
export function isMalformedContactEmail(raw: string | null | undefined): boolean {
  const trimmed = (raw ?? "").trim();
  if (trimmed === "") return false;
  return trimmed.length > CONTACT_EMAIL_MAX_LENGTH || !WELL_FORMED.test(trimmed);
}

/**
 * Normalizes for transport: trim, and blank → null. Mirrors the server's
 * `ContactEmail.Normalize`, which trims so the stored value round-trips EXACTLY against
 * the per-(org, email) suppression key the Resend webhook writes trimmed (#340).
 */
export function trimContactEmail(raw: string | null | undefined): string | null {
  const trimmed = (raw ?? "").trim();
  return trimmed === "" ? null : trimmed;
}

/** Shared inline-error copy, so both vendor forms say the same thing. */
export const CONTACT_EMAIL_ERROR = "Enter a valid email address.";
