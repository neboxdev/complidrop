using System.Collections;
using System.Globalization;
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
/// <item><b>Session/refresh JWTs.</b> The <c>Cookie</c> header and the request body are dropped here
/// outright — structurally, not because two SDK settings happen to say so — but a token embedded in
/// free text by some future log line would still be shipped, so the JWT net covers that.</item>
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
                // `!` is sound: RedactValue returns null only for a null input, and Params' element
                // type is non-nullable object.
                Params = message.Params?.Select(p => RedactValue(p)!).ToArray(),
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

        ScrubEventLike(evt);
        PromoteCorrelationId(evt);
        return evt;
    }

    /// <summary>
    /// The <c>BeforeSendTransaction</c> hook — the SECOND envelope type this process uploads, and the
    /// one the #386 review found uncovered.
    /// <para/>
    /// Performance transactions are auto-instrumented by <c>Sentry.AspNetCore</c> for a sampled share of
    /// EVERY request (<c>Sentry:TracesSampleRate</c>, 0.1 in production) and carry their own
    /// <see cref="SentryRequest"/> — url, query string, headers — copied from the same ASP.NET scope an
    /// event's does. They leave through a SEPARATE hook, so a scrubber installed only on
    /// <c>BeforeSend</c> left roughly a tenth of <c>/api/portal/{token}/…</c> requests uploading the
    /// vendor's bearer credential. Same net, both hooks — the shape ADR 0037's frontend half already
    /// uses (one <c>scrub</c> serving <c>beforeSend</c> and <c>beforeSendTransaction</c>).
    /// <para/>
    /// Spans come along: <c>SentryHttpMessageHandler</c> instruments every <c>IHttpClientFactory</c>
    /// client this process owns, and one of those URLs carries the Gemini API key as a query parameter
    /// (<c>?key=…</c>, <c>GeminiExtractionClient</c>) — which the credential-parameter net covers only
    /// if something actually runs over the span description.
    /// <para/>
    /// Never drops a transaction, for the same reason <see cref="Scrub"/> never drops an event.
    /// </summary>
    public static SentryTransaction ScrubTransaction(SentryTransaction transaction)
    {
        ScrubEventLike(transaction);

        foreach (var span in transaction.Spans)
        {
            span.Description = Redact(span.Description);
            foreach (var (key, value) in span.Tags.ToArray())
                span.SetTag(key, Redact(value) ?? string.Empty);
            foreach (var (key, value) in span.Extra.ToArray())
                span.SetExtra(key, RedactValue(value));
        }

        return transaction;
    }

    /// <summary>
    /// The name a transaction gets when <c>Sentry.AspNetCore</c> cannot derive one from routing.
    /// <para/>
    /// A route-named transaction spells the PATTERN (<c>POST /api/portal/{token}/upload</c> — the literal
    /// placeholder, no credential). When routing yields nothing — a 404 on a portal path is the live case
    /// — the SDK falls back to the raw path, which spells the real token. <see cref="ScrubTransaction"/>
    /// cannot repair that: <c>SentryTransaction.Name</c> is READ-ONLY by the time <c>BeforeSendTransaction</c>
    /// runs (the same shape as <c>SentryEvent.Breadcrumbs</c>, ADR 0053), and the name is also copied into
    /// the envelope's dynamic-sampling header, which no hook can rewrite. So the name is fixed where it is
    /// CHOSEN instead — this hook, running before the transaction exists.
    /// </summary>
    public static string TransactionName(HttpContext context) =>
        Redact($"{context.Request.Method} {context.Request.Path}") ?? string.Empty;

    /// <summary>
    /// Everything an event and a transaction have in common (<see cref="IEventLike"/>): tags, extras and
    /// the request. ONE implementation deliberately — the #386 review found the backend scrubbing events
    /// only, and two divergent copies is how that recurs.
    /// </summary>
    private static void ScrubEventLike(IEventLike target)
    {
        // Materialise before mutating — SetExtra/SetTag write to the dictionary being enumerated.
        foreach (var (key, value) in target.Extra.ToArray())
            target.SetExtra(key, RedactValue(value));

        foreach (var (key, value) in target.Tags.ToArray())
            target.SetTag(key, Redact(value) ?? string.Empty);

        ScrubRequest(target.Request);
        ScrubUser(target.User);
    }

    /// <summary>
    /// Makes "no user identity, no IP" true on the WIRE rather than true-by-option.
    /// <para/>
    /// <c>SendDefaultPii = false</c> is supposed to keep the SDK from attaching an address, but the .NET
    /// SDK stamps <c>ip_address: "{{auto}}"</c> — a server-side instruction telling Sentry's ingest to
    /// fill the address in from the connecting socket — onto transactions regardless (observed on the
    /// serialized envelope in <c>BackendSentryTests</c>). Behind Railway that socket is our own egress,
    /// not a customer, so the leak is small; the reason it goes anyway is that an option we do not
    /// control must not be the only thing standing between this product and an address.
    /// <para/>
    /// <c>Id</c> deliberately survives: it is the SDK's INSTALLATION id — one value per running
    /// container, not per person — and it is the only thing distinguishing two replicas in a
    /// side-by-side deploy. <c>Email</c> / <c>Username</c> / <c>Other</c> are never set by this codebase
    /// and pass the nets in case that ever changes.
    /// </summary>
    private static void ScrubUser(SentryUser user)
    {
        user.IpAddress = null;
        user.Email = Redact(user.Email);
        user.Username = Redact(user.Username);
        // Segment is deliberately not touched — the SDK marks it obsolete and nothing writes it.
        foreach (var (key, value) in user.Other.ToArray())
            user.Other[key] = Redact(value) ?? string.Empty;
    }

    /// <summary>
    /// Headers this event may carry OUT. An ALLOWLIST, not a denylist, and that is the whole point:
    /// <c>SendDefaultPii = false</c> suppresses only the SDK's OWN user/IP/cookie attachment, while
    /// <c>Sentry.AspNetCore</c> still attaches every request header except <c>Cookie</c> and
    /// <c>Authorization</c>. Behind Railway's ingress the end user's — or the portal vendor's — IP
    /// arrives in one of those headers (<c>X-Forwarded-For</c>, and <c>X-Real-Ip</c> /
    /// <c>CF-Connecting-IP</c> / <c>X-Envoy-External-Address</c>, which <c>ForwardedHeadersMiddleware</c>
    /// never consumes), and NONE of the four nets matches an IP literal. Enumerating proxy headers
    /// vendor by vendor is a list that goes stale silently; keeping four diagnostic headers and dropping
    /// the rest makes "no IP" structurally true instead of dependent on proxy behaviour.
    /// <para/>
    /// The frontend half already takes the strong route (<c>scrub.ts</c> deletes sensitive headers);
    /// this is the same posture spelled as an allowlist because the backend sits behind a proxy the
    /// frontend does not.
    /// </summary>
    private static readonly HashSet<string> DiagnosticHeaders =
        new(StringComparer.OrdinalIgnoreCase) { "User-Agent", "Referer", "Content-Type", "X-Trace-Id" };

    private static void ScrubRequest(SentryRequest request)
    {
        request.Url = Redact(request.Url);
        request.QueryString = Redact(request.QueryString);

        // The other two slots the ASP.NET integration can fill. Both are already empty under the
        // options we set (SendDefaultPii off, MaxRequestBodySize None), and both are cleared anyway so
        // "no cookies, never a request body" is a property of this code rather than of two settings a
        // future edit could flip without anyone connecting it to a claim made three files away. A body
        // on this product is a certificate of insurance; a cookie is a session JWT.
        request.Cookies = null;
        request.Data = null;

        foreach (var key in request.Headers.Keys.ToArray())
        {
            if (!DiagnosticHeaders.Contains(key)) request.Headers.Remove(key);
            else request.Headers[key] = Redact(request.Headers[key]) ?? string.Empty;
        }
    }

    /// <summary>
    /// How deep <see cref="RedactValue"/> walks a structured value before it stops recursing and renders
    /// the rest as one string. A bound, not a tuning knob: a self-referencing object graph handed to a
    /// structured log property would otherwise stack-overflow the capturing thread.
    /// </summary>
    private const int MaxValueDepth = 8;

    /// <summary>
    /// Redacts one structured value — a log property, a message parameter, a span extra.
    /// <para/>
    /// The nets used to touch only values that were ALREADY <c>string</c>, so a property that is an
    /// object, an array or a dictionary was serialized to the wire verbatim, bypassing redaction
    /// entirely. Nothing leaks from today's call sites (the #386 audit walked all 23 <c>Error</c>-level
    /// templates; every one passes ids, counts and codes), but this scrubber is documented as reaching
    /// "every string the SDK is about to transmit", and one future <c>logger.LogError("… {@Vendor}",
    /// vendor)</c> would quietly falsify that.
    /// <para/>
    /// Scalars whose rendering cannot embed user content pass through AS THEIR OWN TYPE, so an event
    /// keeps typed counts and status codes rather than becoming a wall of strings. Anything else is
    /// walked; a leaf that no net changes is returned unchanged, so the common case costs nothing but a
    /// comparison.
    /// </summary>
    private static object? RedactValue(object? value) => RedactValue(value, 0);

    private static object? RedactValue(object? value, int depth) => value switch
    {
        null => null,
        string text => Redact(text),
        bool or char or sbyte or byte or short or ushort or int or uint or long or ulong
            or float or double or decimal or Guid or DateTime or DateTimeOffset or TimeSpan
            or Enum => value,
        _ when depth >= MaxValueDepth => Redact(Render(value)),
        IDictionary dictionary => RedactDictionary(dictionary, depth),
        IEnumerable sequence => sequence.Cast<object?>().Select(item => RedactValue(item, depth + 1)).ToList(),
        _ => RedactRendered(value),
    };

    private static Dictionary<string, object?> RedactDictionary(IDictionary dictionary, int depth)
    {
        var redacted = new Dictionary<string, object?>();
        foreach (DictionaryEntry entry in dictionary)
            redacted[Redact(Render(entry.Key)) ?? string.Empty] = RedactValue(entry.Value, depth + 1);
        return redacted;
    }

    /// <summary>
    /// Renders an arbitrary object and redacts the rendering — but hands the ORIGINAL back when nothing
    /// was redacted, so a value the SDK would have serialized structurally keeps its structure unless it
    /// actually carried something we had to remove.
    /// </summary>
    private static object? RedactRendered(object value)
    {
        var rendered = Render(value);
        var redacted = Redact(rendered);
        return string.Equals(rendered, redacted, StringComparison.Ordinal) ? value : redacted;
    }

    private static string Render(object? value) =>
        Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;

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
