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
 * test suites are driven by the shared corpus in `api/CompliDrop.Api.Tests/SharedFixtures/contact-email-cases.json`,
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

/**
 * Membership test for BLANK, as a linear predicate rather than a regex — the mirror of the
 * server's `ContactEmail.IsBlank`.
 *
 * These are the SAME ranges as the BLANK character class above, in the same order; the pair is
 * pinned by a test that walks every code point in the BMP and asserts they never disagree, so
 * this cannot drift from the class WELL_FORMED still uses.
 *
 * Why not regex-strip the edges: the previous `^[BLANK]+|[BLANK]+$` is unanchored in its second
 * alternative, so when that alternative cannot match, the engine retries at every offset and the
 * greedy `[BLANK]+` re-consumes the run each time — O(n^2).
 *
 * The hostile shape is blanks in the MIDDLE with a non-blank at both ends (leading/trailing
 * padding is linear — the `^`-anchored alternative eats it in one match). `isMalformedContactEmail`
 * runs synchronously on every keystroke and paste in both vendor forms, so pasting such a value
 * freezes the tab on the main thread. Lower blast radius than the server, where the same pattern
 * was a remote DoS (see `ContactEmail.IsBlank` for the measured numbers) — but both mirrors had to
 * change together regardless: reviewers.md declares drift between these two files a real finding.
 */
function isBlank(c: string): boolean {
  const p = c.codePointAt(0)!;
  return (
    p <= 0x0020 || // C0 controls + space
    (p >= 0x007f && p <= 0x00a0) || // DEL + C1 (incl. U+0085 NEL) + NBSP
    p === 0x1680 || // Ogham space mark
    (p >= 0x2000 && p <= 0x200d) || // en-quad..ZWJ (incl. U+200B ZWSP)
    p === 0x2028 || // line separator
    p === 0x2029 || // paragraph separator
    p === 0x202f || // narrow no-break space
    p === 0x205f || // medium mathematical space
    p === 0x2060 || // word joiner
    p === 0x3000 || // ideographic space
    p === 0xfeff // ZWNBSP / BOM
  );
}

/**
 * A blank-class character that renders as NOTHING (or as an indistinguishable look-alike):
 * everything in the class except ordinary space and the ASCII layout controls, which a user can
 * plainly see. Drives only the choice of error copy, never accept/reject — so it deliberately has
 * no corpus entry: the corpus owns what is REJECTED, this owns how we word it.
 *
 * NBSP counts as invisible on purpose — it is pixel-identical to a space, so "there is a space
 * here" is not a fix the user can act on, whereas "there is a hidden character" is.
 */
function isInvisible(c: string): boolean {
  const p = c.codePointAt(0)!;
  return isBlank(c) && p !== 0x0020 && !(p >= 0x0009 && p <= 0x000d);
}

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
 * Uses the shared BLANK predicate rather than `String.prototype.trim` precisely so it strips
 * the IDENTICAL set as the server — the two native trims disagree on U+0085 and U+FEFF, which
 * would reintroduce the drift one layer below the character class.
 */
export function trimContactEmail(raw: string | null | undefined): string | null {
  if (raw === null || raw === undefined) return null;

  let start = 0;
  let end = raw.length;
  while (start < end && isBlank(raw[start])) start++;
  while (end > start && isBlank(raw[end - 1])) end--;

  return end === start ? null : raw.slice(start, end);
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
  return contactEmailError(raw) !== undefined;
}

/**
 * Shared inline-error copy. Mirrored by `ContactEmail.InvalidMessage` and friends on the server
 * and pinned to the shared corpus's `messages` block by test on both sides, so the message the
 * user reads while typing and the 400 body they get on submit cannot say different things about
 * the same input — a quieter mirror pair than the predicate, but the same drift class.
 */
export const CONTACT_EMAIL_ERROR =
  "Enter a valid contact email address, like ops@acmecatering.com - or leave it blank.";

/**
 * Separate copy for the invisible-character case. "Enter a valid email address" is unactionable
 * when the field LOOKS correct: a zero-width or non-breaking character pasted from a PDF or a mail
 * client renders as nothing, so the user re-reads a correct-looking address and cannot see what is
 * wrong with it. The explicit blank class rejects these (neither engine's `\s` covers most of
 * them), which is why this message is needed now and was not before.
 */
export const CONTACT_EMAIL_HIDDEN_CHARACTER_ERROR =
  "This address contains a hidden character - retype it, or leave it blank.";

/** Copy for a value that is otherwise well-formed but longer than the column. */
export const CONTACT_EMAIL_TOO_LONG_ERROR =
  "That email address is too long - use one under 256 characters, or leave it blank.";

/**
 * The message to show for a rejected address, or `undefined` when it is acceptable — the single
 * decision both forms render. Blank is acceptable: a vendor with no contact email is a supported
 * state. Mirrors the server's `ContactEmail.DescribeProblem`.
 */
export function contactEmailError(raw: string | null | undefined): string | undefined {
  const normalized = trimContactEmail(raw);
  if (normalized === null) return undefined;
  if (normalized.length > CONTACT_EMAIL_MAX_LENGTH) return CONTACT_EMAIL_TOO_LONG_ERROR;
  if (WELL_FORMED.test(normalized)) return undefined;

  // Edges are already stripped, so any remaining blank-class character is INTERIOR. But only the
  // INVISIBLE ones earn the hidden-character wording: an ordinary space or tab is plainly visible,
  // so "contains a hidden character" would be a lie for the display-name form
  // `Jane Smith <jane@acme.com>` — one of the two literals this ticket reports.
  for (const c of normalized) if (isInvisible(c)) return CONTACT_EMAIL_HIDDEN_CHARACTER_ERROR;

  return CONTACT_EMAIL_ERROR;
}
