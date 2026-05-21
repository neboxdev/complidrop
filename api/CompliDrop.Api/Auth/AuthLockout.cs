namespace CompliDrop.Api.Auth;

/// <summary>
/// Pure account-lockout policy. After <see cref="Threshold"/> failed login attempts the account
/// is locked for <see cref="BaseMinutes"/> minutes, and each subsequent failed attempt doubles
/// the lock duration, capped at <see cref="MaxLockMinutes"/> (24h). Extracted from the login
/// endpoint so the calculation is unit-testable without the wall clock (the caller applies the
/// returned duration to DateTime.UtcNow).
/// </summary>
internal static class AuthLockout
{
    public const int Threshold = 10;
    public const int BaseMinutes = 15;
    public const int MaxLockMinutes = 24 * 60; // 24h cap

    /// <summary>
    /// The lock duration for the given (post-increment) failed-attempt count, or <c>null</c> if
    /// the account should not be locked yet. Exponential backoff: 15 min at the threshold,
    /// doubling per subsequent attempt, capped at 24h.
    /// </summary>
    public static TimeSpan? ComputeLockoutDuration(int failedLoginAttempts)
    {
        if (failedLoginAttempts < Threshold) return null;

        var steps = failedLoginAttempts - Threshold;
        long minutes = BaseMinutes;
        // Double per step, stopping once the cap is reached — overflow-safe for large counts.
        for (var i = 0; i < steps && minutes < MaxLockMinutes; i++)
            minutes *= 2;

        return TimeSpan.FromMinutes(Math.Min(minutes, MaxLockMinutes));
    }
}
