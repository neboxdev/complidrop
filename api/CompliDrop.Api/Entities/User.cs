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
    /// When the user confirmed ownership of <see cref="Email"/> via the #184
    /// verification link. Null = unverified. Reminders + audit emails go to this
    /// address, so an unverified value flags a possible silent dead-letter (a
    /// signup typo) — surfaced by the dashboard banner and the reminder worker.
    /// </summary>
    public DateTime? EmailVerifiedAt { get; set; }

    public DateTime CreatedAt { get; set; }
    public DateTime? LastLoginAt { get; set; }
    public DateTime? DeletedAt { get; set; }

    public Organization Organization { get; set; } = null!;
}
