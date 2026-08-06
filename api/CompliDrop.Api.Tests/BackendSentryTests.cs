using System.Globalization;
using System.Text;
using System.Text.Json;
using CompliDrop.Api.Middleware;
using CompliDrop.Api.Tests.TestHelpers;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Sentry.AspNetCore;
using Sentry.Extensibility;
using Serilog;
using Serilog.Extensions.Logging;

namespace CompliDrop.Api.Tests;

/// <summary>
/// #386 / ADR 0053 — backend error monitoring. Two independent breaks meant Sentry received no error
/// events at all: <c>UseSerilog</c> replaces the MEL provider pipeline so Sentry's logger provider never
/// saw a log event, and <see cref="ExceptionHandlingMiddleware"/> swallows every request-path exception
/// before Sentry's outer middleware can see it. These tests pin the fix from the OUTSIDE — through the
/// SDK's transport, i.e. the bytes that would actually leave the process — so they fail if either break
/// returns, and they fail if the PII posture regresses.
/// </summary>
/// <remarks>
/// The harness initialises the REAL production options (<see cref="BackendSentry.ConfigureOptions"/>)
/// and swaps in a capturing <see cref="ITransport"/>. Nothing is stubbed between the log call and the
/// wire: a test that passes here means a real deploy would have sent that envelope. The static
/// <c>SentrySdk</c> hub is process-global, which is safe because the suite is serial
/// (<c>AssemblyInfo</c>'s <c>DisableTestParallelization</c>) and every harness closes the SDK on
/// dispose.
/// </remarks>
public sealed class BackendSentryTests
{
    private const string UsableTraceId = "0af7651916cd43dd8448eb211c80319c";

    // ------------------------------------------------------------------
    // The two breaks the ticket is about
    // ------------------------------------------------------------------

    [Fact]
    public async Task An_error_log_line_becomes_a_Sentry_event()
    {
        using var harness = new SentryCaptureHarness(dsnConfiguredForSink: true);

        harness.Logger("ExtractionWorker").LogError(
            new InvalidOperationException("Gemini returned 503"), "ExtractionWorker process failed.");

        var events = await harness.CapturedEventsAsync();

        // Break 2: without the Serilog -> Sentry sink this list is EMPTY, which is exactly what prod did.
        events.Should().ContainSingle();
        events[0].GetProperty("level").GetString().Should().Be("error");
        events[0].GetProperty("exception").GetProperty("values")[0]
            .GetProperty("value").GetString().Should().Be("Gemini returned 503");
    }

    [Fact]
    public async Task An_unhandled_request_exception_is_reported_with_the_correlation_id_tag()
    {
        using var harness = new SentryCaptureHarness(dsnConfiguredForSink: true);

        // The REAL production pair, in the real order Program.cs composes them.
        var context = await RunThroughMiddlewareAsync(
            harness, UsableTraceId, new InvalidOperationException("boom inside the endpoint"));

        // The swallowing middleware still answers the caller its envelope (break 1 is fixed by making
        // the exception REPORTABLE, not by rethrowing — a rethrow would 500 with no body).
        context.Response.StatusCode.Should().Be(StatusCodes.Status500InternalServerError);

        var events = await harness.CapturedEventsAsync();
        events.Should().ContainSingle();
        events[0].GetProperty("exception").GetProperty("values")[0]
            .GetProperty("value").GetString().Should().Be("boom inside the endpoint");

        // The ADR 0037 join: the frontend tags its events with the SAME key and the SAME id, so a
        // browser error and the backend 500 behind it are one search away from each other.
        events[0].GetProperty("tags").GetProperty(SentryScrub.CorrelationTag)
            .GetString().Should().Be(UsableTraceId);
    }

    [Fact]
    public async Task A_blank_dsn_registers_no_sink_so_nothing_reaches_the_wire()
    {
        // The SDK itself is live in this harness — only the SINK's configuration lacks a DSN. So a
        // failure here means the gate is gone, not that Sentry happened to be off.
        // The environment half of the gate is NOT what this pins: the harness runs Production. That
        // half has one owner, The_sink_is_not_registered_in_development.
        using var harness = new SentryCaptureHarness(dsnConfiguredForSink: false);

        harness.Logger("ReminderBackgroundService").LogError(
            new InvalidOperationException("Reminder tick failed."), "Reminder tick failed.");
        await RunThroughMiddlewareAsync(harness, UsableTraceId, new InvalidOperationException("boom"));

        (await harness.CapturedEventsAsync()).Should().BeEmpty();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void A_blank_dsn_reads_as_no_dsn(string? dsn)
    {
        BackendSentry.IsEnabled(Configuration(dsn), Env("Production")).Should().BeFalse(
            "an env var set to the empty string must not switch prod telemetry on");
    }

    [Fact]
    public void Development_stays_silent_even_when_a_dsn_is_configured()
    {
        // #386 found the REAL production DSN in the local user-secrets store, which also reaches the
        // integration-test host. The dev database is a clone of prod data, so a dev-side exception
        // naming a real vendor would have been exported to the production Sentry project by running
        // the test suite. Presence of a DSN is therefore NOT the whole gate.
        BackendSentry.IsEnabled(Configuration(SentryCaptureHarness.FakeDsn), Env("Development"))
            .Should().BeFalse();
    }

    [Theory]
    [InlineData("Production")]
    [InlineData("Staging")]
    public void Every_non_development_environment_with_a_dsn_reports(string environmentName)
    {
        // NOT-Development rather than IS-Production: unset ASPNETCORE_ENVIRONMENT already means
        // Production, and silently going dark because prod spells its environment differently is the
        // exact failure this ticket exists to end.
        BackendSentry.IsEnabled(Configuration(SentryCaptureHarness.FakeDsn), Env(environmentName))
            .Should().BeTrue();
    }

    [Fact]
    public async Task The_sink_is_not_registered_in_development()
    {
        using var harness = new SentryCaptureHarness(dsnConfiguredForSink: true, sinkEnvironment: "Development");

        harness.Logger("ExtractionWorker").LogError(
            new InvalidOperationException("boom"), "ExtractionWorker process failed.");

        (await harness.CapturedEventsAsync()).Should().BeEmpty();
    }

    // ------------------------------------------------------------------
    // PII posture (#386 audit / ADR 0053) — asserted on the WIRE payload
    // ------------------------------------------------------------------

    [Fact]
    public async Task The_information_and_warning_stream_never_leaves_the_process()
    {
        using var harness = new SentryCaptureHarness(dsnConfiguredForSink: true);
        var logger = harness.Logger("EmailService");

        // Both of these are REAL log lines (EmailService). #378 is still open, so the sub-Error stream
        // is knowingly full of end-user addresses; a breadcrumb would export it wholesale, and
        // SentryEvent.Breadcrumbs is read-only so the scrubber could not reach it.
        logger.LogInformation("DEV email suppressed — would send to {To}", "pat@gardenhall.example");
        logger.LogWarning("Resend not configured — skipping email to {To}", "pat@gardenhall.example");
        logger.LogError(new InvalidOperationException("send failed"), "Reminder tick failed.");

        var payloads = await harness.CapturedPayloadsAsync();

        payloads.Should().ContainSingle("only the Error line is an event");
        payloads[0].Should().NotContain("pat@gardenhall.example");
        payloads[0].Should().NotContain("breadcrumbs");
    }

    [Fact]
    public async Task A_vendor_portal_capability_token_never_reaches_the_wire()
    {
        const string token = "Xy3kQp7ZmA0bCdEfGhIjKlMn";
        using var harness = new SentryCaptureHarness(dsnConfiguredForSink: true);

        // The shape UseSerilogRequestLogging produces for a 500 on a portal route: the real path, at
        // Error level, as a structured property. The token IS the bearer credential for that link.
        harness.Logger("Serilog.AspNetCore.RequestLoggingMiddleware").LogError(
            "HTTP {Method} {RequestPath} responded 500", "POST", $"/api/portal/{token}/upload");

        var payloads = await harness.CapturedPayloadsAsync();

        payloads.Should().ContainSingle();
        payloads[0].Should().NotContain(token, "the portal token is a capability, not diagnostics");
        payloads[0].Should().Contain("/api/portal/", "the route must stay recognisable to be triageable");
    }

    [Fact]
    public async Task A_performance_transaction_carries_no_portal_token_either()
    {
        const string token = "Xy3kQp7ZmA0bCdEfGhIjKlMn";
        const string geminiKey = "AIzaSyFAKE0123456789abcdefFAKE0123456789";
        using var harness = new SentryCaptureHarness(dsnConfiguredForSink: true, tracesSampleRate: 1.0);

        // Transactions are the SECOND envelope type this process uploads. Sentry.AspNetCore
        // auto-instruments a sampled share of EVERY request (0.1 in prod) and copies the request —
        // url, query string, headers — from the same ASP.NET scope an event's Request comes from. They
        // leave through BeforeSendTransaction, a different hook, so a scrubber installed on BeforeSend
        // alone let ~a tenth of portal requests upload the vendor's bearer credential.
        SentrySdk.ConfigureScope(scope =>
        {
            scope.Request.Url = $"https://api.complidrop.com/api/portal/{token}/upload";
            scope.Request.QueryString = "email=pat@gardenhall.example";
            scope.Request.Headers["X-Forwarded-For"] = "203.0.113.9";
        });

        // Route-sourced name: the PATTERN, not the URL — that is what Sentry.AspNetCore produces for a
        // matched endpoint, and it carries no credential. The unmatched case (raw path, token and all)
        // is closed at a different seam because Name is read-only here — see the name-provider test.
        var transaction = SentrySdk.StartTransaction(
            "POST /api/portal/{token}/upload", "http.server");
        // SentryHttpMessageHandler instruments every IHttpClientFactory client this process owns, and
        // GeminiExtractionClient's URL carries the API key as a query parameter.
        var span = transaction.StartChild(
            "http.client",
            $"POST https://generativelanguage.googleapis.com/v1beta/models/x:generateContent?key={geminiKey}");
        span.Finish(SpanStatus.Ok);
        transaction.Finish(SpanStatus.Ok);

        var transactions = (await harness.CapturedPayloadsAsync())
            .Where(p => p.Contains("\"type\":\"transaction\"", StringComparison.Ordinal))
            .ToList();

        transactions.Should().ContainSingle("this harness samples every trace");
        transactions[0].Should().NotContain(token, "the portal token is a capability, not diagnostics");
        transactions[0].Should().NotContain("pat@gardenhall.example");
        transactions[0].Should().NotContain("203.0.113.9", "a proxy-supplied client address is PII");
        transactions[0].Should().NotContain(geminiKey, "an instrumented span description carries the key");
        transactions[0].Should().NotContain("{{auto}}",
            "the SDK stamps ip_address:{{auto}} on a transaction even with SendDefaultPii off — an "
            + "instruction to Sentry's ingest to fill the address in from the connecting socket");
        transactions[0].Should().Contain("/api/portal/", "the route must stay recognisable to be triageable");
    }

    [Fact]
    public void An_unroutable_request_gets_a_redacted_transaction_name()
    {
        const string token = "Xy3kQp7ZmA0bCdEfGhIjKlMn";
        var options = new SentryAspNetCoreOptions();

        BackendSentry.ConfigureOptions(options, Configuration(SentryCaptureHarness.FakeDsn), Env("Production"));

        // The gap BeforeSendTransaction cannot close: SentryTransaction.Name is READ-ONLY there (like
        // SentryEvent.Breadcrumbs) and is copied into the envelope's dynamic-sampling header besides. So
        // the name must be right where it is CHOSEN. Without a provider the SDK falls back to the raw
        // path for any request routing cannot name — a 404 on a portal path being the live case.
        var context = new DefaultHttpContext();
        context.Request.Method = "POST";
        context.Request.Path = $"/api/portal/{token}/upload";

        options.TransactionNameProvider.Should().NotBeNull();
        var name = options.TransactionNameProvider!(context);

        name.Should().NotContain(token).And.Be("POST /api/portal/[redacted]/upload");
    }

    [Fact]
    public async Task A_proxy_supplied_client_address_never_reaches_the_wire()
    {
        using var harness = new SentryCaptureHarness(dsnConfiguredForSink: true);

        // SendDefaultPii = false suppresses only the SDK's OWN user/IP/cookie attachment; the ASP.NET
        // integration still attaches every request header except Cookie and Authorization. Behind
        // Railway's ingress the caller's IP arrives in exactly those headers, and ForwardedHeaders
        // (ForwardLimit = 1) consumes at most one of them — so the residue plus the vendor-specific
        // ones reach Sentry unless something drops them. None of the four nets matches an IP literal.
        SentrySdk.ConfigureScope(scope =>
        {
            scope.Request.Url = "https://api.complidrop.com/api/documents";
            scope.Request.Headers["X-Forwarded-For"] = "203.0.113.9";
            scope.Request.Headers["X-Real-Ip"] = "198.51.100.7";
            scope.Request.Headers["CF-Connecting-IP"] = "192.0.2.44";
            scope.Request.Headers["X-Envoy-External-Address"] = "203.0.113.77";
            scope.Request.Headers["User-Agent"] = "Mozilla/5.0 (compatible)";
        });

        harness.Logger("ExtractionWorker").LogError(
            new InvalidOperationException("boom"), "ExtractionWorker process failed.");

        var payloads = await harness.CapturedPayloadsAsync();

        payloads.Should().ContainSingle();
        payloads[0].Should().NotContain("203.0.113.9").And.NotContain("198.51.100.7")
            .And.NotContain("192.0.2.44").And.NotContain("203.0.113.77");
        payloads[0].Should().Contain("Mozilla/5.0",
            "the allowlisted diagnostic headers survive — this is not 'drop all headers'");
    }

    [Fact]
    public async Task Secrets_inside_the_exception_itself_are_redacted()
    {
        const string token = "Xy3kQp7ZmA0bCdEfGhIjKlMn";
        using var harness = new SentryCaptureHarness(dsnConfiguredForSink: true);

        // The broadest vector: ExceptionHandlingMiddleware logs whatever was thrown, and an exception
        // raised anywhere can embed user content in its Message. The exception VALUES are a separate
        // place on the event from the log message, so they need their own proof.
        await RunThroughMiddlewareAsync(
            harness,
            UsableTraceId,
            new InvalidOperationException(
                $"no vendor for pat@gardenhall.example at /api/portal/{token}"));

        var payloads = await harness.CapturedPayloadsAsync();

        payloads.Should().ContainSingle();
        payloads[0].Should().NotContain("pat@gardenhall.example").And.NotContain(token);
        payloads[0].Should().Contain("InvalidOperationException", "the exception TYPE still groups the event");
    }

    [Fact]
    public async Task An_email_address_inside_a_third_party_error_body_is_redacted()
    {
        using var harness = new SentryCaptureHarness(dsnConfiguredForSink: true);

        // EmailService's real line: `Resend send failed {Status} {Body}` — Resend's 422 body names the
        // recipient it rejected.
        harness.Logger("EmailService").LogError(
            "Resend send failed {Status} {Body}",
            422,
            """{"name":"validation_error","message":"Invalid `to` field: pat@gardenhall.example"}""");

        var payloads = await harness.CapturedPayloadsAsync();

        payloads.Should().ContainSingle();
        payloads[0].Should().NotContain("pat@gardenhall.example");
        payloads[0].Should().Contain("validation_error", "the diagnosable part of the body survives");
    }

    // ------------------------------------------------------------------
    // SentryScrub — pure
    // ------------------------------------------------------------------

    [Theory]
    [InlineData("/api/portal/Xy3kQp7ZmA0bCdEfGhIjKlMn", "/api/portal/[redacted]")]
    [InlineData("/api/portal/Xy3kQp7ZmA0bCdEfGhIjKlMn/upload", "/api/portal/[redacted]/upload")]
    [InlineData("/portal/Xy3kQp7ZmA0bCdEfGhIjKlMn", "/portal/[redacted]")]
    [InlineData("/API/Portal/Xy3kQp7ZmA0bCdEfGhIjKlMn", "/API/Portal/[redacted]")]
    [InlineData("https://api.complidrop.com/api/portal/tok-abc?x=1", "https://api.complidrop.com/api/portal/[redacted]?x=1")]
    [InlineData(
        "/api/portal/Xy3kQp7ZmA0bCdEfGhIjKlMn/status/6f1b0c1e-0000-0000-0000-000000000001",
        "/api/portal/[redacted]/status/6f1b0c1e-0000-0000-0000-000000000001")]
    public void The_portal_token_segment_is_replaced_deterministically(string input, string expected)
    {
        SentryScrub.Redact(input).Should().Be(expected);
    }

    [Theory]
    [InlineData("/api/documents/6f1b0c1e-0000-0000-0000-000000000001")]
    [InlineData("/api/vendors")]
    [InlineData("Compliance evaluation failed for 6f1b0c1e-0000-0000-0000-000000000001")]
    public void Non_secret_text_survives_untouched(string input)
    {
        // Document / vendor / org ids and route shapes must stay intact or an event stops being
        // triageable — the same trade ADR 0037 makes with its entropy-blind metadata net.
        SentryScrub.Redact(input).Should().Be(input);
    }

    [Theory]
    [InlineData("contact pat@gardenhall.example now", "contact [email redacted] now")]
    [InlineData("a.b+tag@sub.example.co.uk", "[email redacted]")]
    public void Email_addresses_are_redacted(string input, string expected)
    {
        SentryScrub.Redact(input).Should().Be(expected);
    }

    [Fact]
    public void Jwts_and_credential_query_parameters_are_redacted()
    {
        SentryScrub.Redact("cd_session=eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiIxMjM0NTY3ODkwIn0.dBjftJeZ4CVP")
            .Should().Be("cd_session=[jwt redacted]");

        SentryScrub.Redact("/reset?token=abc123def456&next=/documents")
            .Should().Be("/reset?token=[redacted]&next=/documents");

        SentryScrub.Redact("https://blob.example/doc.pdf?sv=2021&sig=AbCd%2F1234")
            .Should().Be("https://blob.example/doc.pdf?sv=2021&sig=[redacted]");
    }

    [Fact]
    public void An_unbounded_value_is_capped_before_the_regexes_run()
    {
        var huge = new string('x', SentryScrub.MaxValueLength * 3);

        var redacted = SentryScrub.Redact(huge)!;

        redacted.Should().HaveLength(SentryScrub.MaxValueLength + SentryScrub.TruncationMarker.Length);
        redacted.Should().EndWith(SentryScrub.TruncationMarker);
    }

    [Fact]
    public void The_cap_drops_a_secret_that_sits_past_it()
    {
        // The cap is a REDACTION mechanism in its own right, not just a regex-cost bound: anything
        // past it never leaves the process, including the shapes no net matches. Asserted with an
        // opaque blob (matched by none of the four nets) so removing the cap would fail this — an
        // email past the cap would be caught by the email net either way and prove nothing.
        var value = new string('x', SentryScrub.MaxValueLength)
                    + " OPAQUE-BEARER-9f2c4d6a pat@gardenhall.example";

        var redacted = SentryScrub.Redact(value)!;

        redacted.Should().NotContain("OPAQUE-BEARER-9f2c4d6a").And.NotContain("pat@gardenhall.example");
        redacted.Should().EndWith(SentryScrub.TruncationMarker);
    }

    [Fact]
    public void The_cap_never_splits_a_surrogate_pair()
    {
        // An emoji straddling the cut is TWO code units. A fixed code-unit cut lands between them
        // and emits a LONE HIGH SURROGATE — an invalid UTF-16 string that a strict UTF-8 encoder
        // (Utf8JsonWriter's, during envelope serialization) refuses, so the event silently fails to
        // serialize: exactly the loss #386 exists to end. Services/ColumnClamp.To is this codebase's
        // ONE surrogate-safe truncation (ADR 0044) and this reuses it.
        var value = new string('x', SentryScrub.MaxValueLength - 1) + "\U0001F642";

        var redacted = SentryScrub.Redact(value)!;

        UnpairedSurrogateCount(redacted).Should().Be(0);
        // The proof that matters: a strict encoder accepts it. This is the operation that throws on
        // the wire, so the assertion is on the operation, not on a proxy for it.
        redacted.Invoking(text => new UTF8Encoding(false, throwOnInvalidBytes: true).GetBytes(text))
            .Should().NotThrow();
    }

    private static int UnpairedSurrogateCount(string text)
    {
        var unpaired = 0;
        for (var i = 0; i < text.Length; i++)
        {
            if (char.IsHighSurrogate(text[i]))
            {
                if (i + 1 >= text.Length || !char.IsLowSurrogate(text[i + 1])) unpaired++;
                else i++;
            }
            else if (char.IsLowSurrogate(text[i])) unpaired++;
        }
        return unpaired;
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Blank_values_pass_through(string? input)
    {
        SentryScrub.Redact(input).Should().Be(input);
    }

    [Fact]
    public void Scrub_reaches_every_string_the_sdk_would_transmit()
    {
        const string token = "Xy3kQp7ZmA0bCdEfGhIjKlMn";
        var evt = new SentryEvent
        {
            Message = new SentryMessage
            {
                Message = "HTTP {Path} failed",
                Formatted = $"HTTP /api/portal/{token} failed",
                Params = [$"/api/portal/{token}"],
            },
        };
        evt.SetExtra("RequestPath", $"/api/portal/{token}");
        evt.SetExtra("Attempt", 3);
        evt.SetTag("route", $"/api/portal/{token}");
        evt.Request.Url = $"https://api.complidrop.com/api/portal/{token}";
        evt.Request.QueryString = "email=pat@gardenhall.example";
        evt.Request.Headers["X-Portal-Token"] = token + "@x.example";
        evt.Request.Headers["User-Agent"] = "probe/1.0 pat@gardenhall.example";

        SentryScrub.Scrub(evt);

        evt.Message!.Formatted.Should().NotContain(token);
        evt.Message.Params!.Cast<object>().Single().Should().Be("/api/portal/[redacted]");
        evt.Extra["RequestPath"].Should().Be("/api/portal/[redacted]");
        evt.Extra["Attempt"].Should().Be(3,
            "a scalar whose rendering cannot embed user content keeps its own type");
        evt.Tags["route"].Should().Be("/api/portal/[redacted]");
        evt.Request.Url.Should().NotContain(token);
        evt.Request.QueryString.Should().Be("email=[redacted]",
            "SentryRequest.QueryString is BARE — the leading ? is stripped, so the first parameter must "
            + "still be reachable by the credential-parameter net");
        // Headers are an ALLOWLIST: a header nobody vouched for is DROPPED, not redacted. Strictly
        // stronger than the redaction this used to assert, and the reason is that the four nets cannot
        // recognise everything a header carries — a client IP being the case that forced it (#386 review).
        evt.Request.Headers.Should().NotContainKey("X-Portal-Token");
        evt.Request.Headers["User-Agent"].Should().Be("probe/1.0 [email redacted]",
            "an allowlisted header still passes the nets");
    }

    [Fact]
    public void A_structured_value_that_is_not_a_string_is_redacted_too()
    {
        // The nets used to touch values that were ALREADY string, so an object / array / dictionary
        // property was serialized to the wire verbatim. Nothing leaks from today's 23 Error-level call
        // sites, but the scrubber is documented as reaching every string the SDK transmits, and one
        // future `logger.LogError("… {@Vendor}", vendor)` would quietly falsify that.
        var evt = new SentryEvent();
        evt.SetExtra("Vendor", new { Email = "pat@gardenhall.example" });
        evt.SetExtra("Recipients", new[] { "pat@gardenhall.example", "sam@gardenhall.example" });
        evt.SetExtra("ByOrg", new Dictionary<string, object?> { ["acme"] = "pat@gardenhall.example" });
        evt.SetExtra("Attempt", 3);
        evt.SetExtra("DocumentId", Guid.Parse("6f1b0c1e-0000-0000-0000-000000000001"));

        SentryScrub.Scrub(evt);

        JsonSerializer.Serialize(evt.Extra).Should().NotContain("pat@gardenhall.example")
            .And.NotContain("sam@gardenhall.example");
        evt.Extra["Attempt"].Should().Be(3, "a typed count must stay a typed count");
        evt.Extra["DocumentId"].Should().Be(Guid.Parse("6f1b0c1e-0000-0000-0000-000000000001"),
            "ids are what make an event triageable — the same trade ADR 0037 makes");
    }

    [Fact]
    public async Task A_destructured_log_property_is_redacted_on_the_wire()
    {
        using var harness = new SentryCaptureHarness(dsnConfiguredForSink: true);

        harness.Logger("VendorEndpoints").LogError(
            "Vendor notify failed {@Vendor}", new { Email = "pat@gardenhall.example", Attempts = 3 });

        var payloads = await harness.CapturedPayloadsAsync();

        payloads.Should().ContainSingle();
        payloads[0].Should().NotContain("pat@gardenhall.example");
    }

    [Fact]
    public void Scrub_never_drops_an_event()
    {
        // Returning null from BeforeSend would silently discard an unhandled 500 — the exact failure
        // #386 exists to end.
        var evt = new SentryEvent();
        SentryScrub.Scrub(evt).Should().BeSameAs(evt);
    }

    [Theory]
    [InlineData("0af7651916cd43dd8448eb211c80319c", true)]
    [InlineData("trace-id_42", true)]
    [InlineData("pat@gardenhall.example", false)]
    [InlineData("has space", false)]
    [InlineData("", false)]
    public void The_correlation_tag_admits_only_a_well_formed_trace_id(string candidate, bool promoted)
    {
        // The tag is deliberately NOT redacted (ADR 0037), so the ADR 0044 charset guard is what keeps
        // client free text off it. Re-asked here rather than trusted from the log property.
        var evt = new SentryEvent();
        evt.SetExtra(CorrelationIdMiddleware.LogPropertyName, candidate);

        SentryScrub.Scrub(evt);

        evt.Tags.ContainsKey(SentryScrub.CorrelationTag).Should().Be(promoted);
        if (promoted) evt.Tags[SentryScrub.CorrelationTag].Should().Be(candidate);
    }

    [Fact]
    public void An_existing_correlation_tag_is_never_overwritten()
    {
        var evt = new SentryEvent();
        evt.SetTag(SentryScrub.CorrelationTag, "already-set");
        evt.SetExtra(CorrelationIdMiddleware.LogPropertyName, UsableTraceId);

        SentryScrub.Scrub(evt);

        evt.Tags[SentryScrub.CorrelationTag].Should().Be("already-set");
    }

    // ------------------------------------------------------------------
    // Options posture
    // ------------------------------------------------------------------

    [Fact]
    public void The_sdk_options_keep_the_privacy_posture_the_ticket_says_to_keep()
    {
        var options = new SentryAspNetCoreOptions
        {
            // HOSTILE starting values, and that is the point of the test. Both of these already hold
            // their safe value on a freshly-constructed options object, so asserting them against a
            // default would leave the test green with BOTH explicit assignments deleted from
            // ConfigureOptions — i.e. it would give exactly none of the protection ADR 0053 says those
            // explicit lines exist to give.
            SendDefaultPii = true,
            MaxRequestBodySize = RequestSize.Always,
        };

        BackendSentry.ConfigureOptions(options, Configuration(SentryCaptureHarness.FakeDsn), Env("Production"));

        options.Dsn.Should().Be(SentryCaptureHarness.FakeDsn);
        options.Environment.Should().Be("Production");
        options.SendDefaultPii.Should().BeFalse("no user identity, no cookies");
        options.MaxRequestBodySize.Should().Be(RequestSize.None, "a request body here is a COI or vendor PII");
        options.TracesSampleRate.Should().Be(BackendSentry.DefaultTracesSampleRate);
    }

    [Fact]
    public void The_trace_sample_rate_stays_configurable()
    {
        var options = new SentryAspNetCoreOptions();
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            [BackendSentry.DsnKey] = SentryCaptureHarness.FakeDsn,
            [BackendSentry.TracesSampleRateKey] = "0.5",
        }).Build();

        BackendSentry.ConfigureOptions(options, configuration, Env("Production"));

        options.TracesSampleRate.Should().Be(0.5);
    }

    // ------------------------------------------------------------------
    // Wiring gate — Program.cs must actually call the helpers
    // ------------------------------------------------------------------

    [Fact]
    public void Program_wires_the_sink_into_Serilog_and_the_scrubber_into_the_sdk()
    {
        var program = SourceScan.StripLineComments(
            File.ReadAllText(SourceScan.ProductionFile("Program.cs")));

        // Argument-exact, not just call-shape: the environment is half the gate, so a call that passed
        // only the configuration would compile against an older overload and silently re-open dev.
        SourceScan.Count(program, ".AddSentryErrorEvents(ctx.Configuration, ctx.HostingEnvironment)")
            .Should().Be(1,
                "the Serilog sink is the ONLY path by which a log event becomes a Sentry event — "
                + "without this call in the UseSerilog lambda the backend goes dark again");
        SourceScan.Count(program, "BackendSentry.ConfigureOptions(opts, builder.Configuration, builder.Environment)")
            .Should().Be(1,
                "UseSentry must configure through the helper, or the scrubber and the PII posture are gone");
        SourceScan.Count(program, "BackendSentry.IsEnabled(builder.Configuration, builder.Environment)")
            .Should().Be(1,
                "one gate for the SDK and the sink, or the two can disagree about whether this reports");

        // Position, not just presence: the hub must be live BEFORE the startup scope, which is the only
        // reason the call exists. Anchored on the scope's own opening statement.
        var hubCall = program.IndexOf(
            "BackendSentry.EnsureHubInitialized(app.Services, app.Configuration, app.Environment)",
            StringComparison.Ordinal);
        var startupScope = program.IndexOf("using (var scope = app.Services.CreateScope())", StringComparison.Ordinal);
        hubCall.Should().BeGreaterThan(0, "the boot window reports nothing until the hub exists");
        startupScope.Should().BeGreaterThan(hubCall,
            "migrations and the template seed log their failures inside that scope — after it, the call "
            + "would be decoration");
    }

    [Fact]
    public async Task An_error_in_the_startup_scope_is_reported()
    {
        // The last dark window (#386 review). The sink emits through the static SentrySdk
        // (InitializeSdk = false), and with the MEL providers replaced by Serilog the ONLY thing that
        // creates that hub is Sentry's own startup filter resolving IHub while the request pipeline is
        // built — inside app.Run(). So a seed or migration failure, logged before app.Run(), was handed
        // to a DISABLED hub. This composes a host the way Program.cs does and logs the seed's real line.
        var transport = new CapturingSentryTransport();
        var builder = WebApplication.CreateBuilder();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            [BackendSentry.DsnKey] = SentryCaptureHarness.FakeDsn,
        });
        builder.Host.UseSerilog((ctx, _, config) => config
            .MinimumLevel.Verbose()
            .Enrich.FromLogContext()
            .AddSentryErrorEvents(ctx.Configuration, ctx.HostingEnvironment));
        builder.WebHost.UseSentry(opts =>
        {
            BackendSentry.ConfigureOptions(opts, builder.Configuration, builder.Environment);
            opts.Transport = transport;
            opts.AutoSessionTracking = false;
            opts.SendClientReports = false;
        });

        await using var app = builder.Build();
        try
        {
            BackendSentry.EnsureHubInitialized(app.Services, app.Configuration, app.Environment);

            SentrySdk.IsEnabled.Should().BeTrue("the boot window reports nothing until the hub exists");

            // ExceptionHandlingMiddleware's peer at boot: Program.cs catches the seed failure and logs it.
            app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Program").LogError(
                new InvalidOperationException("relation \"ComplianceTemplates\" does not exist"),
                "Seed: failed to ensure system compliance templates.");

            await SentrySdk.FlushAsync(TimeSpan.FromSeconds(20));

            transport.Payloads.Should().ContainSingle();
            transport.Payloads[0].Should().Contain("Seed: failed to ensure system compliance templates.");
        }
        finally
        {
            SentrySdk.Close();
        }
    }

    // ------------------------------------------------------------------
    // Harness
    // ------------------------------------------------------------------

    private static IConfiguration Configuration(string? dsn, string? tracesSampleRate = null) =>
        SentryCaptureHarness.Configuration(dsn, tracesSampleRate);

    private static IHostEnvironment Env(string environmentName) => SentryCaptureHarness.Env(environmentName);

    /// <summary>
    /// Drives the real <see cref="CorrelationIdMiddleware"/> -> <see cref="ExceptionHandlingMiddleware"/>
    /// pair over a throwing endpoint, exactly as <c>Program.cs</c> orders them.
    /// </summary>
    private static async Task<HttpContext> RunThroughMiddlewareAsync(
        SentryCaptureHarness harness, string traceId, Exception thrown)
    {
        var services = new ServiceCollection()
            .AddSingleton<ILoggerFactory>(harness.LoggerFactory)
            .BuildServiceProvider();

        var context = new DefaultHttpContext { RequestServices = services };
        context.Request.Headers["X-Trace-Id"] = traceId;
        context.Response.Body = new MemoryStream();

        var exceptionMiddleware = new ExceptionHandlingMiddleware(
            _ => throw thrown,
            harness.LoggerFactory.CreateLogger<ExceptionHandlingMiddleware>());

        await new CorrelationIdMiddleware(exceptionMiddleware.InvokeAsync).InvokeAsync(context);
        return context;
    }
}
