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
    /// is written in the same SaveChanges as the business mutation (#372). So a value that isn't a
    /// trace id (see <see cref="IsUsableTraceId"/>) is REPLACED with a freshly minted one rather
    /// than clamped, which keeps all four in agreement:
    /// <list type="bullet">
    /// <item>A truncated prefix is no longer the id the client sent, so it correlates nothing —
    /// it just looks like it does.</item>
    /// <item>Truncation manufactures collisions: the activity feed collapses rows by
    /// <c>(CorrelationId, EntityType, EntityId)</c>, so two hostile requests sharing a 64-char
    /// prefix would merge into one feed row.</item>
    /// <item>A value outside the trace-id charset is rejected outright, not shortened. This value is
    /// written straight into a RESPONSE HEADER and a structured log scope; a CR/LF there is header
    /// injection (and its own self-inflicted 500), for no legitimate trace-id use case.</item>
    /// </list>
    /// A minted id is 32 hex chars, comfortably inside the column and inside the charset.
    /// </summary>
    internal static string Resolve(string? inbound) =>
        IsUsableTraceId(inbound) ? inbound! : Guid.NewGuid().ToString("N");

    /// <summary>
    /// A usable trace id is non-blank, fits <c>AuditLog.CorrelationId varchar(64)</c>, and is drawn
    /// ONLY from the trace-id charset: ASCII letters, ASCII digits, <c>-</c> and <c>_</c>. Nothing
    /// else — no space, no controls, no non-ASCII, no other punctuation.
    /// <para/>
    /// The charset is narrow on purpose, and the reason is a PII boundary, not aesthetics. A value
    /// accepted here is echoed back in the <c>X-Trace-Id</c> response header, becomes
    /// <c>error.correlationId</c> in the error envelope and therefore <c>ApiError.correlationId</c>
    /// in the frontend — which ships it to Sentry as the <c>correlation_id</c> tag
    /// (<c>frontend/src/lib/sentry/scrub.ts</c> <c>tagCorrelationId</c>). By deliberate ADR 0037
    /// design that tag is applied AFTER <c>scrubEvent</c> and is NOT redacted, because a correlation
    /// id is an opaque identifier rather than user content. Before #372 the inbound header was
    /// honored verbatim at any length with any characters, so that tag took whatever a client sent;
    /// and merely requiring VISIBLE ASCII would not close it either, since
    /// <c>X-Trace-Id: pat@gardenhall.com</c> is 18 visible-ASCII characters and would still land an
    /// email address in Sentry un-redacted, breaking ADR 0037's invariant at its source. Restricting
    /// the charset to <c>[A-Za-z0-9_-]</c> makes an email- or
    /// free-text-shaped id structurally impossible to inject, while still honoring every
    /// real tracing format: 32-hex ids, W3C <c>traceparent</c> hex-and-dash, UUIDs, ULIDs and
    /// <c>_</c>-prefixed vendor ids. See ADR 0044.
    /// </summary>
    internal static bool IsUsableTraceId(string? value)
    {
        if (string.IsNullOrEmpty(value)) return false;
        if (value.Length > AuditColumnLengths.CorrelationId) return false;
        foreach (var c in value)
            if (c is not (>= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '-' or '_'))
                return false;
        return true;
    }
}
