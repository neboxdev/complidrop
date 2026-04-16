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

public record AuthMeResponse(
    Guid UserId,
    Guid OrganizationId,
    string Email,
    string FullName,
    string Role,
    string Plan,
    string OrganizationName,
    string TimeZone);
