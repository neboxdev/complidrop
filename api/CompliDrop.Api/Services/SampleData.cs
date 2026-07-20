namespace CompliDrop.Api.Services;

/// <summary>
/// Constants describing the one-click sample-demo fixtures (#238 / ADR 0028), shared by the code
/// that SEEDS them and the code that must avoid acting on them.
/// <para/>
/// The vendor address is the important one. It is an RFC 2606 reserved domain that accepts no mail,
/// so any send to it is a guaranteed hard bounce — which writes an <c>EmailSuppression</c>, a
/// <c>reminder.recipient_suppressed</c> feed event and a permanent "bounced" alarm badge onto a
/// vendor that does not exist, at real Resend cost. Every send path therefore checks the ADDRESS
/// rather than the <c>Vendor.IsSample</c> flag (#367 review): <c>VendorEndpoints.UpdateVendor</c>
/// lets a user rename the sample vendor and give it a REAL contact address without clearing
/// <c>IsSample</c>, so a flag-based skip would permanently and silently drop that real vendor's mail
/// with no UI signal. The undeliverable address is the actual hazard; the flag is only a label.
/// </summary>
public static class SampleData
{
    /// <summary>Contact address seeded onto the sample vendor. RFC 2606 reserved — accepts no mail.</summary>
    public const string VendorEmail = "sample-vendor@example.com";

    /// <summary>
    /// True when <paramref name="email"/> is the seeded, undeliverable sample-vendor address and must
    /// not be sent to. Case-insensitive and whitespace-tolerant to match how addresses are stored
    /// as-typed (the reminder worker's recipient dedupe uses <c>OrdinalIgnoreCase</c> for the same reason).
    /// </summary>
    public static bool IsUndeliverableSampleAddress(string? email) =>
        string.Equals(email?.Trim(), VendorEmail, StringComparison.OrdinalIgnoreCase);
}
