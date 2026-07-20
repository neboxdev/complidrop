/**
 * The single definition of "is this vendor contact email obviously malformed" (#369).
 *
 * The vendors LIST add-form has guarded this since FP-076; the vendor DETAIL edit form
 * never did, and the backend only trimmed — so a typo saved through the edit path landed
 * a 200 OK and then broke every reminder send silently (they retry in place, ADR 0025,
 * surfacing nothing to the operator unless Resend happens to bounce). #369 *is* what
 * happened when one form owned the only copy of this rule, so the fix is a shared
 * predicate rather than a second copy pasted into the detail page — the same reasoning
 * that produced `DocumentSupersession` (#327), and mirroring the existing cross-language
 * pair `Services/DisplayLabels.cs` <-> `frontend/src/lib/display-labels.ts` (#188).
 *
 * `Services/ContactEmail.cs` is the server-side mirror and MUST agree with this file:
 * the client guard exists to explain the problem inline, the server check exists because
 * the API is reachable without either form. That agreement is pinned mechanically — both
 * test suites are driven by the shared corpus in `docs/fixtures/contact-email-cases.json`,
 * so a case added on one side is automatically asserted on the other.
 *
 * Deliberately NOT a full RFC 5322 parser. The goal is to catch the typo that costs a
 * customer their reminders (`jane@acme,com`, a pasted `Jane Smith <jane@acme.com>`),
 * not to adjudicate exotic-but-legal addresses; over-strict validation that rejects a
 * real address is a worse failure than letting an odd one through. Non-ASCII letters
 * stay legal (`josé@empresa.es`).
 */

/** Max persisted length — `Vendor.ContactEmail` is `varchar(256)` (ModelConfiguration). */
export const CONTACT_EMAIL_MAX_LENGTH = 256;

/**
 * Blank-or-invisible code points, spelled out EXPLICITLY rather than as `\s`.
 *
 * This string is character-for-character identical to `ContactEmail.Blank` on the server,
 * and that is the point. `\s` is NOT the same class in the two engines — .NET's includes
 * U+0085 (NEL) and excludes U+FEFF (BOM); JS's is exactly the reverse — and `Trim()` vs
 * `.trim()` diverge on the same two code points. Relying on each engine's `\s` made the
 * mirrors disagree on real input: a pasted BOM was rejected here and ACCEPTED by the
 * server, which then stored an unsendable address — the very silent reminder failure #369
 * exists to prevent. It also let C0 controls through server-side, where a NUL cannot be
 * stored by Postgres at all (SQLSTATE 22021, surfacing as a 500).
 *
 * Ranges: C0 + space; DEL + C1 (incl. U+0085) + NBSP; Ogham space; en-quad..ZWJ (incl.
 * U+200B ZWSP); line/paragraph separators; narrow + medium spaces; word joiner;
 * ideographic space; ZWNBSP/BOM. Deliberately NOT a general-category class like `\p{C}` —
 * those resolve against each runtime's Unicode tables, which is the same engine-dependence
 * wearing a different hat.
 */
const BLANK =
  "\\u0000-\\u0020\\u007F-\\u00A0\\u1680\\u2000-\\u200D\\u2028\\u2029\\u202F\\u205F\\u2060\\u3000\\uFEFF";

/** Leading/trailing runs of BLANK — the mirror of the server's `BlankEdges`. */
const BLANK_EDGES = new RegExp(`^[${BLANK}]+|[${BLANK}]+$`, "g");

/**
 * Non-empty local part, a single `@`, and a dotted domain — no blank-or-invisible character
 * anywhere. Rejects `jane@acme,com` (no dot in the domain) and `Jane Smith <jane@acme.com>`
 * (space in the local part). JS's `$` without `/m` matches only at end-of-string, which is
 * what the server's `\z` means — so the two anchors agree.
 */
const WELL_FORMED = new RegExp(`^[^${BLANK}@]+@[^${BLANK}@]+\\.[^${BLANK}@]+$`);

/**
 * Normalizes for transport: strip blank-or-invisible edges, and empty → null. Mirrors the
 * server's `ContactEmail.Normalize`, which strips so the stored value round-trips EXACTLY
 * against the per-(org, email) suppression key the Resend webhook writes trimmed (#340).
 *
 * Uses BLANK_EDGES rather than `String.prototype.trim` precisely so it strips the IDENTICAL
 * set as the server — the two native trims disagree on U+0085 and U+FEFF, which would
 * reintroduce the drift one layer below the regex.
 */
export function trimContactEmail(raw: string | null | undefined): string | null {
  if (raw === null || raw === undefined) return null;
  const stripped = raw.replace(BLANK_EDGES, "");
  return stripped === "" ? null : stripped;
}

/**
 * True when `raw` is present but not a usable address. Blank is NOT malformed: a vendor
 * with no contact email is a supported state (the detail page already explains that the
 * "Email link" button needs one), so blank must stay saveable.
 *
 * Evaluates the NORMALIZED value, so the check and the transmitted value can never
 * disagree about what was inspected.
 */
export function isMalformedContactEmail(raw: string | null | undefined): boolean {
  const normalized = trimContactEmail(raw);
  if (normalized === null) return false;
  return normalized.length > CONTACT_EMAIL_MAX_LENGTH || !WELL_FORMED.test(normalized);
}

/** Shared inline-error copy, so both vendor forms say the same thing. */
export const CONTACT_EMAIL_ERROR = "Enter a valid email address.";
