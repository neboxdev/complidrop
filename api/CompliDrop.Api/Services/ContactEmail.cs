using System.Text.RegularExpressions;

namespace CompliDrop.Api.Services;

/// <summary>
/// The single server-side definition of vendor contact-email normalization + format validity (#369).
/// <para>
/// Mirrors <c>frontend/src/lib/contact-email.ts</c>. The client guard exists to explain the problem
/// inline while the user types; THIS is the authoritative check, because <c>/api/vendors</c> is
/// reachable without either form. The two files must agree — if they drift, one side silently accepts
/// what the other rejects, which is the exact class of bug #369 reports. The shared corpus in
/// <c>api/CompliDrop.Api.Tests/SharedFixtures/contact-email-cases.json</c> drives BOTH test suites so
/// that agreement is mechanically pinned rather than asserted in a comment.
/// </para>
/// <para>
/// Deliberately NOT unified with <c>AuthEndpoints.IsValidEmail</c>, which stays laxer
/// (<c>Contains('@')</c>). That asymmetry is intentional and load-bearing: an ACCOUNT email is proven
/// by the verification mail, so a typo self-corrects when the mail never arrives, and over-strict
/// signup validation locks a real customer out of registering. A VENDOR contact email is never proven
/// — nothing round-trips through it — and a typo fails silently forever (reminders retry in place,
/// ADR 0025). Different evidence, different strictness.
/// </para>
/// <para>
/// Deliberately NOT <see cref="System.Net.Mail.MailAddress"/>: it ACCEPTS the display-name form
/// <c>Jane Smith &lt;jane@acme.com&gt;</c>, which is one of the two literals #369 reports as the
/// failure case, and would then persist the whole string as the send address.
/// </para>
/// </summary>
public static partial class ContactEmail
{
    /// <summary>Max persisted length — <c>Vendor.ContactEmail</c> is <c>varchar(256)</c>.</summary>
    public const int MaxLength = 256;

    /// <summary>
    /// Blank-or-invisible code points, spelled out EXPLICITLY rather than as <c>\s</c>.
    /// <para>
    /// This is the fix for the review's confirmed drift: .NET's <c>\s</c> is
    /// <c>[\f\n\r\t\v\x85\p{Z}]</c> while JS's <c>\s</c> excludes U+0085 and includes U+FEFF, and
    /// <c>Trim()</c> vs <c>.trim()</c> diverge on those same two code points. Relying on either
    /// engine's <c>\s</c> made the mirrors disagree on real input: a pasted BOM was REJECTED by the
    /// client and ACCEPTED by the server, which then stored an unsendable address — precisely the
    /// silent reminder failure #369 exists to prevent.
    /// </para>
    /// <para>
    /// It also closes a second confirmed bug: <c>\s</c> does not cover the C0 controls, so a NUL
    /// reached <c>SaveChangesAsync</c> and raised Postgres 22021 (<c>varchar</c> cannot store 0x00),
    /// surfacing as a 500 — the same 400-instead-of-500 outcome the length cap guarantees.
    /// </para>
    /// <para>
    /// Ranges (identical text in the JS mirror): C0 + space; DEL + C1 (incl. U+0085 NEL) + NBSP;
    /// Ogham space; the en-quad..ZWJ block (incl. U+200B ZWSP); line/paragraph separators; narrow and
    /// medium spaces; word joiner; ideographic space; ZWNBSP/BOM. Deliberately NOT a general-category
    /// class like <c>\p{C}</c> — those resolve against each runtime's Unicode tables, which is the
    /// same engine-dependence in a new costume. Non-ASCII LETTERS stay legal (<c>josé@empresa.es</c>).
    /// </para>
    /// </summary>
    /// <remarks>
    /// internal (via <c>InternalsVisibleTo</c>) so <c>ContactEmailTests</c> can assert this class and
    /// <see cref="IsBlank"/> against each other over the whole BMP. A copied literal in the test
    /// could not detect the drift it exists to catch — it would drift with the copy.
    /// </remarks>
    internal const string Blank =
        @"\u0000-\u0020\u007F-\u00A0\u1680\u2000-\u200D\u2028\u2029\u202F\u205F\u2060\u3000\uFEFF";

    /// <summary>
    /// Membership test for <see cref="Blank"/>, as a linear predicate rather than a regex.
    /// <para>
    /// These are the SAME ranges as the <see cref="Blank"/> character-class string above, in the same
    /// order. The pair is pinned by <c>The_blank_predicate_and_the_character_class_agree</c>, which
    /// walks every code point in the BMP and asserts the two never disagree — so this cannot drift from
    /// the class the <see cref="WellFormed"/> regex still uses.
    /// </para>
    /// <para>
    /// Why not regex-strip the edges: the previous <c>^[Blank]+|[Blank]+\z</c> is UNANCHORED in its
    /// second alternative, so when that alternative cannot match, the engine retries at every offset
    /// and the greedy <c>[Blank]+</c> re-consumes the run each time — O(n²), with no match timeout
    /// configured anywhere in the API and the 256-char cap applied only AFTER normalization.
    /// <para>
    /// The hostile shape is NOT leading/trailing padding: that is linear (1.3 ms on 10 MB), because
    /// <c>^[Blank]+</c> matches at offset 0 and consumes the whole run in one match. It is blanks in
    /// the MIDDLE with a non-blank at both ends, where neither alternative can match. Measured on the
    /// real generated-regex path: 100k → 225 ms, 200k → 1.0 s, 400k → 4.3 s (~4× per doubling), which
    /// extrapolates to ~45 minutes of one pegged CPU for a 10 MB body — a size Kestrel accepts and any
    /// authenticated org user can post. Pinned by
    /// <c>Normalization_of_a_blank_heavy_value_completes_promptly</c>.
    /// </para>
    /// <para>
    /// A linear scan is O(n) with identical accept/reject semantics, and preserves the
    /// engine-independence this class exists for: it is NOT the forbidden <c>\s</c>/<c>\p{C}</c>
    /// simplification — the character set is unchanged.
    /// </para>
    /// </para>
    /// </summary>
    internal static bool IsBlank(char c) =>
        c <= '\u0020'                              // C0 controls + space
        || (c >= '\u007F' && c <= '\u00A0')     // DEL + C1 (incl. U+0085 NEL) + NBSP
        || c == '\u1680'                           // Ogham space mark
        || (c >= '\u2000' && c <= '\u200D')     // en-quad..ZWJ (incl. U+200B ZWSP)
        || c == '\u2028' || c == '\u2029'       // line / paragraph separators
        || c == '\u202F'                           // narrow no-break space
        || c == '\u205F'                           // medium mathematical space
        || c == '\u2060'                           // word joiner
        || c == '\u3000'                           // ideographic space
        || c == '\uFEFF';                          // ZWNBSP / BOM

    /// <summary>
    /// Non-empty local part, a single <c>@</c>, and a dotted domain — no blank-or-invisible character
    /// anywhere. <c>\z</c> (not <c>$</c>) so a trailing newline can't slip through: .NET's <c>$</c>
    /// also matches before a final <c>\n</c>. (JS's <c>$</c> without <c>/m</c> does not, so the mirror
    /// uses <c>$</c> and the two agree.)
    /// </summary>
    [GeneratedRegex("^[^" + Blank + "@]+@[^" + Blank + "@]+\\.[^" + Blank + "@]+\\z")]
    private static partial Regex WellFormed();

    /// <summary>
    /// Normalizes on write: strip blank-or-invisible edges; empty → null. Stripping is required so the
    /// stored value round-trips EXACTLY against the per-(org, email) suppression key, which the Resend
    /// webhook stores <c>Trim()</c>'d (#340) — a padded address is otherwise sent and logged verbatim
    /// while the suppression lookup misses, so reminders keep firing into a dead mailbox. Casing is
    /// preserved (every comparison site is already case-insensitive).
    /// <para>
    /// Uses <see cref="IsBlank"/> rather than <c>string.Trim()</c> so the JS mirror can strip the
    /// IDENTICAL set — <c>.trim()</c> and <c>Trim()</c> disagree on U+0085 and U+FEFF, which would
    /// otherwise reintroduce the drift one layer down from the character class.
    /// </para>
    /// </summary>
    public static string? Normalize(string? email)
    {
        if (email is null) return null;

        var start = 0;
        var end = email.Length;
        while (start < end && IsBlank(email[start])) start++;
        while (end > start && IsBlank(email[end - 1])) end--;

        return end == start ? null : email[start..end];
    }

    /// <summary>
    /// True when the address is absent or usable. Absent is VALID: a vendor with no contact email is a
    /// supported state, so this gate must not turn "no email" into a 400. Evaluates the NORMALIZED
    /// value, so validation and persistence can never disagree about what was checked.
    /// </summary>
    public static bool IsWellFormed(string? email) => TryNormalize(email, out _);

    /// <summary>
    /// Validates and normalizes in ONE pass: returns false when the address is present but unusable,
    /// otherwise emits the value to persist (<c>null</c> when absent — a supported state).
    /// <para>
    /// Callers use this instead of <see cref="IsWellFormed"/> followed by <see cref="Normalize"/> so the
    /// value that was CHECKED is by construction the value that gets WRITTEN. The two-call shape also
    /// normalized the raw request string twice per write, doubling the scan an attacker controls.
    /// </para>
    /// </summary>
    public static bool TryNormalize(string? email, out string? normalized)
    {
        normalized = Normalize(email);
        if (normalized is null) return true;

        // Length first: it is O(1) and bounds the input the regex then walks, so an oversized value can
        // never reach the backtracking engine (see IsBlank on why that ordering is load-bearing).
        if (normalized.Length > MaxLength)
        {
            normalized = null;
            return false;
        }

        if (WellFormed().IsMatch(normalized)) return true;

        normalized = null;
        return false;
    }
}
