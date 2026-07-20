using System.Text.RegularExpressions;

namespace CompliDrop.Api.Services;

/// <summary>
/// The single server-side definition of vendor contact-email normalization + format validity (#369).
/// <para>
/// Mirrors <c>frontend/src/lib/contact-email.ts</c>. The client guard exists to explain the problem
/// inline while the user types; THIS is the authoritative check, because <c>/api/vendors</c> is
/// reachable without either form. The two files must agree — if they drift, one side silently accepts
/// what the other rejects, which is the exact class of bug #369 reports.
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
    /// Non-empty local part, a single <c>@</c>, and a dotted domain — no whitespace anywhere.
    /// <c>\z</c> (not <c>$</c>) so a trailing newline can't slip through: .NET's <c>$</c> also matches
    /// before a final <c>\n</c>, which would diverge from the JS mirror's <c>$</c>.
    /// </summary>
    [GeneratedRegex(@"^[^\s@]+@[^\s@]+\.[^\s@]+\z")]
    private static partial Regex WellFormed();

    /// <summary>
    /// Normalizes on write: trim; blank → null. Trimming is required so the stored value round-trips
    /// EXACTLY against the per-(org, email) suppression key, which the Resend webhook stores
    /// <c>Trim()</c>'d (#340) — a padded address is otherwise sent and logged verbatim while the
    /// suppression lookup misses, so reminders keep firing into a dead mailbox. Casing is preserved
    /// (every comparison site is already case-insensitive), matching the vendor's chosen display form.
    /// </summary>
    public static string? Normalize(string? email) =>
        string.IsNullOrWhiteSpace(email) ? null : email.Trim();

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
