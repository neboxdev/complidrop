using CompliDrop.Api.Services;

namespace CompliDrop.Api.Middleware;

public class CorrelationIdMiddleware(RequestDelegate next)
{
    private const string HeaderName = "X-Trace-Id";

    public async Task InvokeAsync(HttpContext context)
    {
        var correlationId = Resolve(context.Request.Headers[HeaderName].FirstOrDefault());

        context.Items["CorrelationId"] = correlationId;
        context.Response.Headers[HeaderName] = correlationId;

        using var scope = context.RequestServices
            .GetRequiredService<ILoggerFactory>()
            .CreateLogger<CorrelationIdMiddleware>()
            .BeginScope(new Dictionary<string, object> { ["CorrelationId"] = correlationId });

        await next(context);
    }

    /// <summary>
    /// The inbound <c>X-Trace-Id</c> is UNTRUSTED, unbounded client input that this one value then
    /// fans out to four places: <c>HttpContext.Items</c>, the echoed response header, the log scope,
    /// and — via <c>ICurrentUser.CorrelationId</c> — <c>AuditLog.CorrelationId varchar(64)</c>, which
    /// is written in the same SaveChanges as the business mutation (#372). So an unusable value is
    /// REPLACED with a freshly minted id rather than clamped, which keeps all four in agreement:
    /// <list type="bullet">
    /// <item>A truncated prefix is no longer the id the client sent, so it correlates nothing —
    /// it just looks like it does.</item>
    /// <item>Truncation manufactures collisions: the activity feed collapses rows by
    /// <c>(CorrelationId, EntityType, EntityId)</c>, so two hostile requests sharing a 64-char
    /// prefix would merge into one feed row.</item>
    /// <item>Control characters and non-ASCII are rejected outright, not shortened. This value is
    /// written straight into a RESPONSE HEADER and a structured log scope; a CR/LF there is header
    /// injection (and its own self-inflicted 500), for no legitimate trace-id use case.</item>
    /// </list>
    /// A minted id is 32 hex chars, comfortably inside the column.
    /// </summary>
    internal static string Resolve(string? inbound) =>
        IsUsableTraceId(inbound) ? inbound! : Guid.NewGuid().ToString("N");

    /// <summary>Non-blank, fits the column, and visible ASCII throughout (no space, no controls).</summary>
    internal static bool IsUsableTraceId(string? value)
    {
        if (string.IsNullOrEmpty(value)) return false;
        if (value.Length > AuditColumnLengths.CorrelationId) return false;
        foreach (var c in value)
            if (c is < '!' or > '~') return false;
        return true;
    }
}
