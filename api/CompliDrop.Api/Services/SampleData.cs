using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace CompliDrop.Api.Services;

/// <summary>
/// Constants describing the one-click sample-demo fixtures (#238 / ADR 0028), shared by the code
/// that SEEDS them, the code that must avoid acting on them, and the schema that constrains them.
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

    /// <summary>
    /// Database name of the partial unique index that allows at most one LIVE sample document per org
    /// (<c>IsSample AND DeletedAt IS NULL</c>, #238) — the concurrent-double-click backstop. Npgsql
    /// reports it as <see cref="PostgresException.ConstraintName"/> on the 23505 the losing seed gets.
    /// <para/>
    /// It lives HERE, in <c>Services/</c>, for the reason <see cref="WaitlistSignup.EmailUniqueIndexName"/>
    /// does (#389 review): an index name that a <c>catch</c> filter matches on must be ONE constant owned
    /// by a layer BOTH sides may depend on — <c>ModelConfiguration</c> names the index with it
    /// (<c>HasDatabaseName</c>) and <c>SampleEndpoints</c> matches the violation with it, so the schema
    /// and the matcher agree by construction. Hand-copying it into the endpoint is exactly the drift
    /// that turns a concurrent double-click back into a 500. The value is unchanged, so pinning it
    /// needed no migration.
    /// </summary>
    public const string DocumentUniqueIndexName = "IX_Documents_OrganizationId_SampleUnique";

    /// <summary>
    /// True when <paramref name="ex"/> is the one-live-sample-per-org violation — and nothing else, so
    /// an unrelated 23505 is surfaced rather than silently answered as "someone else seeded first".
    /// Same shape as <see cref="WaitlistSignup.IsDuplicateEmail"/> and
    /// <see cref="IdempotencyService.IsKeyConflict"/>.
    /// </summary>
    public static bool IsDocumentUniqueViolation(DbUpdateException ex) =>
        ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation } pg
        && string.Equals(pg.ConstraintName, DocumentUniqueIndexName, StringComparison.Ordinal);
}
