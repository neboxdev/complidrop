namespace CompliDrop.Api.Auth;

public interface ICurrentUser
{
    Guid? UserId { get; }
    Guid? OrganizationId { get; }
    string? Email { get; }
    string? Plan { get; }
    bool IsAuthenticated { get; }
    string? IpAddress { get; }
    string? UserAgent { get; }
    string? CorrelationId { get; }
}
