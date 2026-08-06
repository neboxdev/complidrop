using System.Text;
using System.Text.Json;
using CompliDrop.Api.Middleware;
using CompliDrop.Api.Tests.TestHelpers;
using FluentAssertions;
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
        using var harness = new SentryHarness(dsnConfiguredForSink: true);

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
        using var harness = new SentryHarness(dsnConfiguredForSink: true);

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
    public async Task Dev_stays_silent_when_no_dsn_is_configured()
    {
        // The SDK itself is live in this harness — only the SINK's configuration lacks a DSN. So a
        // failure here means the gate is gone, not that Sentry happened to be off.
        using var harness = new SentryHarness(dsnConfiguredForSink: false);

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
        BackendSentry.IsEnabled(Configuration(SentryHarness.FakeDsn), Env("Development"))
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
        BackendSentry.IsEnabled(Configuration(SentryHarness.FakeDsn), Env(environmentName))
            .Should().BeTrue();
    }

    [Fact]
    public async Task The_sink_is_not_registered_in_development()
    {
        using var harness = new SentryHarness(dsnConfiguredForSink: true, sinkEnvironment: "Development");

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
        using var harness = new SentryHarness(dsnConfiguredForSink: true);
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
        using var harness = new SentryHarness(dsnConfiguredForSink: true);

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
    public async Task Secrets_inside_the_exception_itself_are_redacted()
    {
        const string token = "Xy3kQp7ZmA0bCdEfGhIjKlMn";
        using var harness = new SentryHarness(dsnConfiguredForSink: true);

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
        using var harness = new SentryHarness(dsnConfiguredForSink: true);

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

        SentryScrub.Scrub(evt);

        evt.Message!.Formatted.Should().NotContain(token);
        evt.Message.Params!.Cast<object>().Single().Should().Be("/api/portal/[redacted]");
        evt.Extra["RequestPath"].Should().Be("/api/portal/[redacted]");
        evt.Extra["Attempt"].Should().Be(3, "a non-string extra is left alone");
        evt.Tags["route"].Should().Be("/api/portal/[redacted]");
        evt.Request.Url.Should().NotContain(token);
        evt.Request.QueryString.Should().Be("email=[redacted]",
            "SentryRequest.QueryString is BARE — the leading ? is stripped, so the first parameter must "
            + "still be reachable by the credential-parameter net");
        evt.Request.Headers["X-Portal-Token"].Should().Be("[email redacted]");
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
        var options = new SentryAspNetCoreOptions();

        BackendSentry.ConfigureOptions(options, Configuration(SentryHarness.FakeDsn), Env("Production"));

        options.Dsn.Should().Be(SentryHarness.FakeDsn);
        options.Environment.Should().Be("Production");
        options.SendDefaultPii.Should().BeFalse("no user identity, no IP, no cookies");
        options.MaxRequestBodySize.Should().Be(RequestSize.None, "a request body here is a COI or vendor PII");
        options.TracesSampleRate.Should().Be(BackendSentry.DefaultTracesSampleRate);
    }

    [Fact]
    public void The_trace_sample_rate_stays_configurable()
    {
        var options = new SentryAspNetCoreOptions();
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            [BackendSentry.DsnKey] = SentryHarness.FakeDsn,
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
    }

    // ------------------------------------------------------------------
    // Harness
    // ------------------------------------------------------------------

    private static IConfiguration Configuration(string? dsn) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { [BackendSentry.DsnKey] = dsn })
            .Build();

    private static IHostEnvironment Env(string environmentName) => new FakeHostEnvironment(environmentName);

    private sealed class FakeHostEnvironment(string environmentName) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = environmentName;
        public string ApplicationName { get; set; } = "CompliDrop.Api";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider ContentRootFileProvider { get; set; } = null!;
    }

    /// <summary>
    /// Drives the real <see cref="CorrelationIdMiddleware"/> → <see cref="ExceptionHandlingMiddleware"/>
    /// pair over a throwing endpoint, exactly as <c>Program.cs</c> orders them.
    /// </summary>
    private static async Task<HttpContext> RunThroughMiddlewareAsync(
        SentryHarness harness, string traceId, Exception thrown)
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

    /// <summary>
    /// Production options + production sink + a capturing transport. Nothing between the log call and
    /// the wire is faked, so what these tests assert on is the envelope a real deploy would upload.
    /// </summary>
    private sealed class SentryHarness : IDisposable
    {
        internal const string FakeDsn = "https://0123456789abcdef0123456789abcdef@o0.ingest.us.sentry.io/1";

        private readonly IDisposable _sdk;
        private readonly CapturingSentryTransport _transport = new();
        private readonly Serilog.Core.Logger _serilog;

        public SentryHarness(bool dsnConfiguredForSink, string sinkEnvironment = "Production")
        {
            var options = new SentryAspNetCoreOptions();
            BackendSentry.ConfigureOptions(options, Configuration(FakeDsn), Env("Production"));
            options.Transport = _transport;
            options.AutoSessionTracking = false;
            options.SendClientReports = false;
            _sdk = SentrySdk.Init(options);

            _serilog = new LoggerConfiguration()
                .MinimumLevel.Verbose()
                .Enrich.FromLogContext()
                .AddSentryErrorEvents(
                    Configuration(dsnConfiguredForSink ? FakeDsn : null), Env(sinkEnvironment))
                .CreateLogger();

            LoggerFactory = new SerilogLoggerFactory(_serilog);
        }

        public ILoggerFactory LoggerFactory { get; }

        public Microsoft.Extensions.Logging.ILogger Logger(string category) =>
            LoggerFactory.CreateLogger(category);

        public async Task<IReadOnlyList<string>> CapturedPayloadsAsync()
        {
            await SentrySdk.FlushAsync(TimeSpan.FromSeconds(20));
            return _transport.Payloads;
        }

        public async Task<IReadOnlyList<JsonElement>> CapturedEventsAsync()
        {
            var payloads = await CapturedPayloadsAsync();
            return payloads.Select(CapturingSentryTransport.ParseEventItem).ToList();
        }

        public void Dispose()
        {
            _sdk.Dispose();
            _serilog.Dispose();
        }
    }
}
