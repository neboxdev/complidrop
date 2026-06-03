namespace CompliDrop.Api.Entities;

/// <summary>
/// A single-use, tokenized email-verification grant (#184). The raw token is
/// emailed to the user; only <see cref="TokenHash"/> (SHA-256 hex) is stored.
/// Consumed exactly once (<see cref="ConsumedAt"/>) and expires at
/// <see cref="ExpiresAt"/>. Not tenant-scoped — verification happens before any
/// org context exists (the verify endpoint is anonymous, like register), so the
/// token itself is the bearer secret. Soft-delete is intentionally absent:
/// these rows are short-lived and pruned, not user-visible data.
/// </summary>
public class EmailVerificationToken
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }

    /// <summary>SHA-256 hex of the raw token. Unique-indexed for O(1) lookup on verify.</summary>
    public string TokenHash { get; set; } = string.Empty;

    public DateTime ExpiresAt { get; set; }

    /// <summary>Set the moment the token is redeemed; a non-null value makes the token unusable.</summary>
    public DateTime? ConsumedAt { get; set; }

    public DateTime CreatedAt { get; set; }

    public User User { get; set; } = null!;
}
