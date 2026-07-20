using System.Text.RegularExpressions;

namespace CompliDrop.Api.Services;

/// <summary>
/// The single server-side definition of vendor contact-email normalization + format validity (#369).
/// <para>
/// Mirrors <c>frontend/src/lib/contact-email.ts</c>. The client guard exists to explain the problem
/// inline while the user types; THIS is the authoritative check, because <c>/api/vendors</c> is
/// reachable without either form. The two files must agree — if they drift, one side silently accepts
/// what the other rejects, which is the exact class of bug #369 reports. The shared corpus in
/// <c>docs/fixtures/contact-email-cases.json</c> drives BOTH test suites so that agreement is
/// mechanically pinned rather than asserted in a comment.
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
    private const string Blank =
        @"\u0000-\u0020\u007F-\u00A0\u1680\u2000-\u200D\u2028\u2029\u202F\u205F\u2060\u3000\uFEFF";

    /// <summary>Leading/trailing runs of <see cref="Blank"/>, stripped by <see cref="Normalize"/>.</summary>
    [GeneratedRegex("^[" + Blank + "]+|[" + Blank + "]+\\z")]
    private static partial Regex BlankEdges();

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
    /// Uses <see cref="BlankEdges"/> rather than <c>string.Trim()</c> so the JS mirror can strip the
    /// IDENTICAL set — <c>.trim()</c> and <c>Trim()</c> disagree on U+0085 and U+FEFF, which would
    /// otherwise reintroduce the drift one layer down from the regex.
    /// </para>
    /// </summary>
    public static string? Normalize(string? email)
    {
        if (email is null) return null;
        var stripped = BlankEdges().Replace(email, string.Empty);
        return stripped.Length == 0 ? null : stripped;
    }

    /// <summary>
    /// True when the address is absent or usable. Absent is VALID: a vendor with no contact email is a
    /// supported state, so this gate must not turn "no email" into a 400. Evaluates the NORMALIZED
    /// value, so validation and persistence can never disagree about what was checked.
    /// </summary>
    public static bool IsWellFormed(string? email)
    {
        var normalized = Normalize(email);
        if (normalized is null) return true;
        return normalized.Length <= MaxLength && WellFormed().IsMatch(normalized);
    }
}
