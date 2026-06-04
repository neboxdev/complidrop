namespace CompliDrop.Api.Entities;

public class User
{
    public Guid Id { get; set; }
    public Guid OrganizationId { get; set; }
    public string Email { get; set; } = string.Empty;
    public string PasswordHash { get; set; } = string.Empty;
    public string FullName { get; set; } = string.Empty;
    public string Role { get; set; } = "admin";
    public int FailedLoginAttempts { get; set; } = 0;
    public DateTime? LockedUntil { get; set; }

    /// <summary>
    /// Rotated on every credential change (password reset / change) — see #202.
    /// Embedded as the `stamp` claim in issued session + refresh tokens and
    /// re-checked per authenticated request (OnTokenValidated) + on refresh, so a
    /// stolen/old token stops validating the moment the credential changes
    /// (stateless JWTs otherwise have no revocation). Defaults to a fresh GUID for
    /// every row (DB default gen_random_uuid()).
    /// </summary>
    public Guid SecurityStamp { get; set; }

    /// <summary>
    /// When the user confirmed ownership of <see cref="Email"/> via the #184
    /// verification link. Null = unverified. Reminders + audit emails go to this
    /// address, so an unverified value flags a possible silent dead-letter (a
    /// signup typo) — surfaced by the dashboard banner and the reminder worker.
    /// </summary>
    public DateTime? EmailVerifiedAt { get; set; }

    /// <summary>
    /// False until the user finishes (or skips) the first-run onboarding flow (#191).
    /// Gates the one-time welcome modal so it fires exactly once and persists across
    /// devices. The #191 migration backfills every pre-existing user to true (they're
    /// already oriented); new signups default to false. Flipped via
    /// POST /api/auth/complete-onboarding.
    /// </summary>
    public bool HasCompletedOnboarding { get; set; }

    public DateTime CreatedAt { get; set; }
    public DateTime? LastLoginAt { get; set; }
    public DateTime? DeletedAt { get; set; }

    public Organization Organization { get; set; } = null!;
}
