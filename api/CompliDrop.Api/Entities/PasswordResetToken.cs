namespace CompliDrop.Api.Entities;

/// <summary>
/// A single-use password-reset grant (#183). The raw token is emailed; only
/// <see cref="TokenHash"/> (SHA-256 hex) is stored. Short-lived (45 min) because
/// it grants the ability to set a new password — much more sensitive than the
/// 7-day email-verification token. Redeeming it clears the account lockout and
/// invalidates every other outstanding reset token for the same user. Not
/// tenant-scoped (the reset endpoint is anonymous — the token is the bearer
/// secret).
/// </summary>
public class PasswordResetToken
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }

    /// <summary>SHA-256 hex of the raw token. Unique-indexed for O(1) lookup on reset.</summary>
    public string TokenHash { get; set; } = string.Empty;

    public DateTime ExpiresAt { get; set; }

    /// <summary>Set the moment the token is redeemed (or invalidated by a newer request);
    /// a non-null value makes the token unusable.</summary>
    public DateTime? ConsumedAt { get; set; }

    public DateTime CreatedAt { get; set; }

    public User User { get; set; } = null!;
}
