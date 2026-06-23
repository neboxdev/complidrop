namespace CompliDrop.Api.Entities;

/// <summary>
/// Why a reminder recipient address was suppressed. Ordered so a stronger signal can upgrade a weaker one
/// but never downgrade: a <see cref="Complained"/> address (an affirmative opt-out) must stay suppressed
/// even if a later bounce arrives for it. See ADR 0031.
/// The integer value IS the precedence rank — the webhook upsert compares with <c>reason &gt; existing.Reason</c>
/// — so this enum is APPEND-ONLY: never reorder or renumber existing members; a future stronger reason
/// (e.g. a manual block) must take a HIGHER value than <see cref="Complained"/>.
/// </summary>
public enum EmailSuppressionReason
{
    /// <summary>A hard (Resend "Permanent") bounce — the address is undeliverable. A deliverability signal.</summary>
    Bounced = 0,

    /// <summary>A spam complaint (Resend <c>email.complained</c>) — an affirmative consent opt-out, permanent.</summary>
    Complained = 1
}

/// <summary>
/// A reminder recipient address the engine must stop sending to, scoped per <c>(OrganizationId, Email)</c>.
/// Recorded by the Resend webhook on a hard bounce or a complaint, checked by
/// <c>ReminderBackgroundService</c> before each send, and surfaced on the vendor whose <c>ContactEmail</c>
/// it matches so a dead address isn't buried on a <c>ReminderLog</c> row (#340). Tenant-scoped because the
/// org is the sender relationship; Resend's own account-level suppression list is the global delivery
/// backstop. See ADR 0031.
/// </summary>
public class EmailSuppression
{
    public Guid Id { get; set; }
    public Guid OrganizationId { get; set; }

    /// <summary>Stored lowercased for case-insensitive matching against recipient addresses.</summary>
    public string Email { get; set; } = string.Empty;

    public EmailSuppressionReason Reason { get; set; }

    /// <summary>The <c>ReminderLog</c> whose bounce/complaint first created (or last upgraded) this record.</summary>
    public Guid? SourceReminderLogId { get; set; }

    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}
