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
    bool HasCompletedOnboarding);
