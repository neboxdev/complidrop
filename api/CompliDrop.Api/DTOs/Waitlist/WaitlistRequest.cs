namespace CompliDrop.Api.DTOs.Waitlist;

public record WaitlistRequest(
    string Email,
    string? CompanyName,
    string? Industry,
    string? Source
);
