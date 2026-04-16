using CompliDrop.Api.Auth;

namespace CompliDrop.Api.Tests.TestHelpers;

public class FakeCurrentUser : ICurrentUser
{
    public Guid? UserId { get; set; }
    public Guid? OrganizationId { get; set; }
    public string? Email { get; set; }
    public string? Plan { get; set; } = "free";
    public bool IsAuthenticated => UserId is not null;
    public string? IpAddress { get; set; } = "127.0.0.1";
    public string? UserAgent { get; set; } = "xunit";
    public string? CorrelationId { get; set; } = Guid.NewGuid().ToString("N");
}
