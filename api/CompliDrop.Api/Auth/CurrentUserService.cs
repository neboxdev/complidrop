using System.Security.Claims;
using CompliDrop.Api.Services;

namespace CompliDrop.Api.Auth;

public class CurrentUserService(IHttpContextAccessor accessor) : ICurrentUser
{
    public Guid? UserId => TryParseGuid(Principal?.FindFirstValue(ClaimTypes.NameIdentifier));
    public Guid? OrganizationId => TryParseGuid(Principal?.FindFirstValue("org_id"));
    public string? Email => Principal?.FindFirstValue(ClaimTypes.Email);
    public string? Plan => Principal?.FindFirstValue("plan");
    public bool IsAuthenticated => Principal?.Identity?.IsAuthenticated ?? false;

    // These three are the ONLY client-controlled values that reach an AuditLog row, and the audit
    // row is written in the SAME SaveChanges as the business mutation — so an over-length one used
    // to fail the whole unit of work with Postgres 22001 -> 500 (#372). A vendor could hand the
    // PUBLIC portal-upload route a 600-char User-Agent and take the upload down with it, and an
    // attacker could suppress their own `user.login_failed` audit row (the lockout increment had
    // already committed in an earlier SaveChanges). Clamping HERE — at the one boundary both audit
    // writers read (AuditSaveChangesInterceptor and AuditLogger) — covers every audit writer by
    // construction, present and future, instead of at each sink.
    //
    // Truncate rather than drop: a UA prefix still names the browser/OS and a truncated address is
    // still forensic evidence. The correlation id is already bounded by CorrelationIdMiddleware, so
    // this clamp is a no-op that keeps the invariant total (and independent of middleware ordering)
    // WITHOUT letting the stored value diverge from the echoed X-Trace-Id response header.
    public string? IpAddress => ColumnClamp.To(
        accessor.HttpContext?.Connection.RemoteIpAddress?.ToString(), AuditColumnLengths.IpAddress);

    public string? UserAgent => ColumnClamp.To(
        accessor.HttpContext?.Request.Headers.UserAgent.ToString(), AuditColumnLengths.UserAgent);

    public string? CorrelationId => ColumnClamp.To(
        accessor.HttpContext?.Items["CorrelationId"] as string, AuditColumnLengths.CorrelationId);

    private ClaimsPrincipal? Principal => accessor.HttpContext?.User;

    private static Guid? TryParseGuid(string? value) =>
        Guid.TryParse(value, out var g) ? g : null;
}
