using System.Text.RegularExpressions;
using CompliDrop.Api.Middleware;
using CompliDrop.Api.Services;

namespace CompliDrop.Api;

/// <summary>
/// The backend half of the Sentry PII posture (#386, ADR 0053) — the pure, unit-tested
/// <c>BeforeSend</c> scrubber every captured event passes through, and the promotion of the request's
/// correlation id onto the tag the frontend already uses.
/// <para/>
/// It exists because #386 turned every <c>LogError</c> into a Sentry event. Before that the backend
/// SDK captured essentially nothing, so what our own log lines said never left the container. Now a
/// structured log property, a rendered message, an exception value or the request URL is shipped to a
/// third-party processor — and three of those routinely carry things a compliance product must not
/// export:
/// <list type="bullet">
/// <item><b>The vendor-portal capability token.</b> <c>UseSerilogRequestLogging</c> logs a completed
/// request at <c>Error</c> when the response is a 500, and the Sentry ASP.NET integration attaches
/// <c>Request.Url</c> — both spell the real path, and <c>/api/portal/{token}</c> IS the bearer
/// credential for that vendor's upload link. This is the same vector ADR 0037 closes on the frontend
/// with a deterministic path replacement, so the backend uses the same shape rather than relying on an
/// entropy heuristic.</item>
/// <item><b>Email addresses.</b> A third-party error body echoed into a log line (Resend rejecting a
/// recipient is the live example) can name the person being mailed.</item>
/// <item><b>Session/refresh JWTs.</b> Cookies are not attached (<c>SendDefaultPii</c> is off), but a
/// token embedded in free text by some future log line would be.</item>
/// </list>
/// <para/>
/// Deliberately NARROW. This is a net for values that have a recognisable SHAPE, not a filter that can
/// make arbitrary prose safe: the standing project rule — application code never hands raw document
/// field values to Sentry (ADR 0037 § Consequences, mirrored for the backend by the audit in ADR 0053)
/// — is what covers everything else, and the log lines were audited against it in #386.
/// </summary>
/// <remarks>
/// Every regex is bounded (no unbounded quantifier that could backtrack quadratically on a long
/// <c>@</c>-less blob) and every string is length-capped BEFORE the regexes run, so a multi-megabyte
/// LLM error body cannot turn <c>BeforeSend</c> into a stall on the capturing thread. That ordering is
/// load-bearing, not incidental — same reasoning as ADR 0037's <c>maxValueLength</c>.
/// </remarks>
public static partial class SentryScrub
{
    /// <summary>
    /// Hard cap on any single string this scrubber emits, mirroring ADR 0037's <c>maxValueLength: 8192</c>.
    /// The .NET SDK has no equivalent option, so the cap is applied here. It bounds the regex work AND
    /// bounds the blast radius of a log line that hands Sentry an unbounded third-party response body
    /// (`Resend send failed {Status} {Body}` and the three extraction-client equivalents): the console
    /// sink still records the whole thing, only the copy that leaves the building is clipped.
    /// </summary>
    internal const int MaxValueLength = 8192;

    internal const string TruncationMarker = "…[truncated]";
    internal const string Redacted = "[redacted]";
    internal const string RedactedEmail = "[email redacted]";
    internal const string RedactedJwt = "[jwt redacted]";

    /// <summary>
    /// The vendor-portal capability token as it appears in a URL or a logged request path. Matches the
    /// segment after <c>/portal/</c> or <c>/api/portal/</c> and stops at the next path separator, so
    /// <c>/api/portal/{token}/status/{uploadId}</c> keeps the shape that makes the event triageable and
    /// loses only the credential. Deterministic — never dependent on the token's charset or entropy
    /// (ADR 0037 rejected the entropy route for exactly this value).
    /// </summary>
    [GeneratedRegex(@"(/(?:api/)?portal/)[^/?#\s""'<>\\]{1,256}",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex PortalTokenPath();

    /// <summary>
    /// Credential-ish query parameters: reset/verify links (<c>token</c>), the Azure blob SAS
    /// signature (<c>sig</c>), and anything spelled <c>email</c>/<c>key</c>/<c>secret</c>/<c>password</c>.
    /// The NAME survives so the event still says which parameter was present.
    /// <para/>
    /// The alternation anchors on start-of-string as well as <c>?</c>/<c>&amp;</c> because
    /// <c>SentryRequest.QueryString</c> is a BARE query string — the SDK strips the leading <c>?</c>, so
    /// a first parameter would otherwise be the one parameter this net never covered.
    /// </summary>
    [GeneratedRegex(@"((?:^|[?&])(?:token|email|sig|signature|key|secret|password|code)=)[^&\s""'#]{1,512}",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SensitiveQueryParam();

    /// <summary>
    /// A JWT in free text — the <c>cd_session</c> / <c>cd_refresh</c> cookies are JWTs, so anything that
    /// ever echoes one into a message must not carry it out. Anchored on the <c>eyJ</c> header prefix
    /// with bounded segments.
    /// </summary>
    [GeneratedRegex(@"eyJ[A-Za-z0-9_\-]{8,4000}\.[A-Za-z0-9_\-]{8,4000}(?:\.[A-Za-z0-9_\-]{0,4000})?",
        RegexOptions.CultureInvariant)]
    private static partial Regex Jwt();

    /// <summary>
    /// An email address in free text. Deliberately LAX (unlike <c>Services/ContactEmail.cs</c>, which
    /// decides whether an address is acceptable to STORE): a redactor must over-match, not under-match.
    /// </summary>
    [GeneratedRegex(@"[A-Za-z0-9._%+\-]{1,64}@[A-Za-z0-9\-]{1,63}(?:\.[A-Za-z0-9\-]{1,63}){0,8}\.[A-Za-z]{2,24}",
        RegexOptions.CultureInvariant)]
    private static partial Regex Email();

    /// <summary>
    /// Redacts one free-text value. Length cap FIRST (see the class remarks), then the four nets, in an
    /// order chosen so an earlier replacement cannot manufacture a later match.
    /// <para/>
    /// The cap goes through <see cref="ColumnClamp.To"/> — this codebase's ONE surrogate-safe truncation
    /// (ADR 0044) — rather than a raw slice. A fixed code-unit cut can land between the halves of a
    /// surrogate pair (an emoji straddling the boundary) and emit a LONE HIGH SURROGATE; the strict
    /// UTF-8 encoder behind <c>Utf8JsonWriter</c> refuses that, so the envelope fails to serialize and
    /// the error event is silently lost — the exact loss #386 exists to end. <c>ColumnClamp</c>'s
    /// contract is a WIDTH, not a database column, so reusing it here keeps one truncation rule instead
    /// of a second, subtly different one.
    /// </summary>
    internal static string? Redact(string? value)
    {
        if (string.IsNullOrEmpty(value)) return value;

        var text = value.Length > MaxValueLength
            ? ColumnClamp.To(value, MaxValueLength) + TruncationMarker
            : value;

        text = PortalTokenPath().Replace(text, "$1" + Redacted);
        text = SensitiveQueryParam().Replace(text, "$1" + Redacted);
        text = Jwt().Replace(text, RedactedJwt);
        text = Email().Replace(text, RedactedEmail);
        return text;
    }

    /// <summary>
    /// The <c>BeforeSend</c> hook itself: redacts every string the SDK is about to transmit, then — and
    /// only then, exactly like ADR 0037's <c>tagCorrelationId</c> — promotes the request's correlation
    /// id onto the <c>correlation_id</c> tag so a browser event and the backend 500 that caused it join
    /// up on one searchable key.
    /// <para/>
    /// Never drops an event: returning <c>null</c> here would mean an unhandled 500 is silently
    /// discarded, which is the failure this ticket exists to end.
    /// </summary>
    public static SentryEvent Scrub(SentryEvent evt)
    {
        if (evt.Message is { } message)
        {
            evt.Message = new SentryMessage
            {
                Message = Redact(message.Message),
                Formatted = Redact(message.Formatted),
                Params = message.Params?.Select(p => p is string s ? (object)Redact(s)! : p).ToArray(),
            };
        }

        // Materialised and reassigned, not mutated in place through the property. The SDK builds this
        // collection with LINQ; if a future version leaves it deferred, mutating what a foreach yields
        // would edit throwaway instances and the redaction would silently not reach the wire — the
        // quietest way for this whole net to stop working.
        if (evt.SentryExceptions is { } exceptions)
        {
            var scrubbed = exceptions.ToList();
            foreach (var exception in scrubbed)
                exception.Value = Redact(exception.Value);
            evt.SentryExceptions = scrubbed;
        }

        // Materialise before mutating — SetExtra/SetTag write to the dictionary being enumerated.
        foreach (var (key, value) in evt.Extra.ToArray())
            if (value is string text)
                evt.SetExtra(key, Redact(text));

        foreach (var (key, value) in evt.Tags.ToArray())
            evt.SetTag(key, Redact(value) ?? string.Empty);

        var request = evt.Request;
        request.Url = Redact(request.Url);
        request.QueryString = Redact(request.QueryString);
        foreach (var (key, value) in request.Headers.ToArray())
            request.Headers[key] = Redact(value) ?? string.Empty;

        PromoteCorrelationId(evt);
        return evt;
    }

    /// <summary>
    /// Copies the request's correlation id from the structured log property
    /// (<see cref="CorrelationIdMiddleware.LogPropertyName"/>, which <c>Enrich.FromLogContext</c> puts on
    /// EVERY log event raised during a request and the Serilog sink forwards as an extra) onto the
    /// <c>correlation_id</c> tag.
    /// <para/>
    /// The tag is deliberately NOT redacted — a correlation id is an opaque identifier, and redacting it
    /// would defeat the join it exists for. What makes that safe is its SHAPE, and the shape is decided
    /// in exactly one place: <see cref="CorrelationIdMiddleware.IsUsableTraceId"/>, the ADR 0044 charset
    /// guard that REPLACES an inbound <c>X-Trace-Id</c> that isn't drawn from <c>[A-Za-z0-9_-]</c>. Re-asking
    /// it here rather than trusting the property means a client cannot land free text on this tag even if
    /// some future path put an unvetted value into the log scope — the same reasoning that makes the
    /// frontend's un-redacted <c>correlation_id</c> tag safe, applied to the end that mints the id.
    /// </summary>
    private static void PromoteCorrelationId(SentryEvent evt)
    {
        if (evt.Tags.ContainsKey(CorrelationTag)) return;
        if (!evt.Extra.TryGetValue(CorrelationIdMiddleware.LogPropertyName, out var raw)) return;
        if (raw is not string correlationId || !CorrelationIdMiddleware.IsUsableTraceId(correlationId)) return;

        evt.SetTag(CorrelationTag, correlationId);
    }

    /// <summary>
    /// The tag name the frontend already writes (<c>frontend/src/lib/sentry/scrub.ts</c>
    /// <c>tagCorrelationId</c>, ADR 0037). Both ends must spell it identically or the join silently
    /// yields nothing.
    /// </summary>
    internal const string CorrelationTag = "correlation_id";
}
