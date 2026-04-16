using System.Security.Claims;

namespace CompliDrop.Api.Auth;

public class CurrentUserService(IHttpContextAccessor accessor) : ICurrentUser
{
    public Guid? UserId => TryParseGuid(Principal?.FindFirstValue(ClaimTypes.NameIdentifier));
    public Guid? OrganizationId => TryParseGuid(Principal?.FindFirstValue("org_id"));
    public string? Email => Principal?.FindFirstValue(ClaimTypes.Email);
    public string? Plan => Principal?.FindFirstValue("plan");
    public bool IsAuthenticated => Principal?.Identity?.IsAuthenticated ?? false;
    public string? IpAddress => accessor.HttpContext?.Connection.RemoteIpAddress?.ToString();
    public string? UserAgent => accessor.HttpContext?.Request.Headers.UserAgent.ToString();
    public string? CorrelationId => accessor.HttpContext?.Items["CorrelationId"] as string;

    private ClaimsPrincipal? Principal => accessor.HttpContext?.User;

    private static Guid? TryParseGuid(string? value) =>
        Guid.TryParse(value, out var g) ? g : null;
}
