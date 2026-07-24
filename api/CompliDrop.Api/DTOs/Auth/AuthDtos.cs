namespace CompliDrop.Api.DTOs.Auth;

public record RegisterRequest(
    string Email,
    string Password,
    string FullName,
    string CompanyName,
    string? Industry,
    string? CompanySize,
    string? TimeZone);

public record LoginRequest(string Email, string Password);

public record VerifyEmailRequest(string Token);

public record UpdateOrganizationRequest(string Name, string TimeZone);

// Account & access management (#183).
public record ForgotPasswordRequest(string Email);
public record ResetPasswordRequest(string Token, string NewPassword);
public record ChangePasswordRequest(string CurrentPassword, string NewPassword);
public record ChangeEmailRequest(string Password, string NewEmail);
public record DeleteAccountRequest(string Password);

/// <summary>
/// Server-evaluated feature flags the SPA gates UI on, carried on every me-shaped payload (#416).
/// <c>CorrectedChecklists</c> mirrors <c>TemplateCorrections:Enabled</c> (ADR 0036 Amendment 3):
/// while false, the frontend hides the liquor "+ Add a requirement" menu option and the
/// additional-insured nudge, keeping the gated #416 behavior invisible pending the
/// G1-COUNSEL-BRIEF §0 legal/insurance sign-off.
/// <c>CorrectedAdditionalInsuredWording</c> mirrors
/// <c>ComplianceClaims:CorrectedAdditionalInsuredWording</c> (#396, CLM-1, ADR 0043): while false,
/// the frontend renders the legacy categorical "Names '{name}' as additional insured" copy; when
/// true it swaps in the honest "certificate indicates…" wording. DELIBERATELY separate from
/// <c>CorrectedChecklists</c> — CLM-1 unlocks on a different attorney sign-off.
/// Additive — no existing me field changed.
/// </summary>
public record AuthFeatures(bool CorrectedChecklists, bool CorrectedAdditionalInsuredWording);

public record AuthMeResponse(
    Guid UserId,
    Guid OrganizationId,
    string Email,
    string FullName,
    string Role,
    string Plan,
    string OrganizationName,
    string TimeZone,
    bool EmailVerified,
    bool HasCompletedOnboarding,
    AuthFeatures Features);
