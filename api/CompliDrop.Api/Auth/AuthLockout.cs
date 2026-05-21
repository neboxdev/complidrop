namespace CompliDrop.Api.Auth;

/// <summary>
/// Pure account-lockout policy. After <see cref="Threshold"/> failed login attempts the account
/// is locked, with the lock duration growing <see cref="StepMinutes"/> minutes per additional
/// attempt, capped at <see cref="MaxMultiplier"/>×. Extracted from the login endpoint so the
/// calculation is unit-testable without the wall clock (the caller applies it to DateTime.UtcNow).
/// </summary>
internal static class AuthLockout
{
    public const int Threshold = 10;
    public const int MaxMultiplier = 12;
    public const int StepMinutes = 15;

    /// <summary>
    /// The lock duration for the given (post-increment) failed-attempt count, or
    /// <c>null</c> if the account should not be locked yet.
    /// </summary>
    public static TimeSpan? ComputeLockoutDuration(int failedLoginAttempts)
    {
        if (failedLoginAttempts < Threshold) return null;
        var multiplier = Math.Min(failedLoginAttempts - (Threshold - 1), MaxMultiplier);
        return TimeSpan.FromMinutes(StepMinutes * multiplier);
    }
}
