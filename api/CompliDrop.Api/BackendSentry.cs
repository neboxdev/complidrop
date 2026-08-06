using Sentry.AspNetCore;
using Sentry.Extensibility;
using Serilog;
using Serilog.Events;

namespace CompliDrop.Api;

/// <summary>
/// Composes the backend's Sentry integration (#386, ADR 0053) — the ONE gate that decides whether
/// anything is captured at all, the SDK options, and the Serilog sink that is the only path by which a
/// log event ever becomes a Sentry event.
/// <para/>
/// The sink is not a nice-to-have. <c>builder.Host.UseSerilog(...)</c> replaces the MEL provider
/// pipeline (<c>writeToProviders</c> defaults <c>false</c>), so Sentry's own <c>SentryLoggerProvider</c>
/// — the thing that would otherwise turn every <c>LogError</c> into an event — never sees a log event.
/// And <see cref="Middleware.ExceptionHandlingMiddleware"/> catches every request-path exception and
/// does not rethrow, so nothing propagates up to Sentry's outer middleware either. Before #386 those
/// two independent breaks meant the dashboard showed performance traces and NOT ONE error: no unhandled
/// 500, no extraction failure, no reminder-tick failure, no sweep failure. Wiring the sink closes both,
/// because the middleware and every worker tick already call <c>logger.LogError(ex, …)</c>.
/// </summary>
public static class BackendSentry
{
    internal const string DsnKey = "Sentry:Dsn";
    internal const string TracesSampleRateKey = "Sentry:TracesSampleRate";
    internal const double DefaultTracesSampleRate = 0.1;

    /// <summary>
    /// Whether Sentry is wired at all — the ONE gate, asked identically by the SDK and by the sink so
    /// the two can never disagree about whether this process reports.
    /// <para/>
    /// TWO conditions, both required: a non-blank DSN (whitespace reads as absent, so a blank env var
    /// is the same as an unset one) AND an environment that is not Development.
    /// <para/>
    /// The Development half is not belt-and-braces. Presence of a DSN alone was ASSUMED to keep dev
    /// silent — <c>appsettings.json</c> ships an empty <c>Sentry:Dsn</c> — but configuration layers
    /// above it, and #386 found the real production DSN sitting in the local <c>user-secrets</c> store,
    /// where it also reaches the integration-test host (same <c>UserSecretsId</c>, Development
    /// environment). That was harmless while the backend captured nothing but performance traces; the
    /// moment every <c>LogError</c> became an event it stopped being harmless, because the dev database
    /// is a CLONE OF PROD DATA (docs/dev-environment.md) — so a dev-side exception naming a real vendor
    /// would have been exported to the production Sentry project by simply running the test suite.
    /// Gating on the environment makes dev silent BY CONSTRUCTION rather than by everyone keeping a
    /// secret store tidy, and mirrors ADR 0037's frontend rule (<c>dsn &amp;&amp; NODE_ENV ===
    /// production</c>).
    /// <para/>
    /// Spelled as NOT-Development rather than IS-Production on purpose: the failure directions are not
    /// symmetric. Unset <c>ASPNETCORE_ENVIRONMENT</c> already means Production, and a Staging box should
    /// report; silently going dark because prod's environment name is spelled differently is the exact
    /// failure this ticket exists to end, while a prod box literally naming itself Development would
    /// already be serving OpenAPI and skipping HTTPS redirection.
    /// </summary>
    public static bool IsEnabled(IConfiguration configuration, IHostEnvironment environment) =>
        !string.IsNullOrWhiteSpace(configuration[DsnKey]) && !environment.IsDevelopment();

    /// <summary>
    /// The SDK options. <c>SendDefaultPii</c> and the request-body capture are set EXPLICITLY even
    /// though both match the SDK default: they are the load-bearing half of the privacy posture the
    /// #386 ticket says to keep, and a default is not a decision anyone can see in a diff.
    /// </summary>
    public static void ConfigureOptions(
        SentryAspNetCoreOptions options,
        IConfiguration configuration,
        IHostEnvironment environment)
    {
        options.Dsn = configuration[DsnKey];
        options.Environment = environment.EnvironmentName;
        options.TracesSampleRate = configuration.GetValue(TracesSampleRateKey, DefaultTracesSampleRate);

        // No user identity, no IP, no cookies — and never a request body, which on this product is a
        // certificate of insurance or a vendor's contact details.
        options.SendDefaultPii = false;
        options.MaxRequestBodySize = RequestSize.None;

        // Everything that survives the two settings above still passes the scrubber (ADR 0053 §PII):
        // the portal capability token lives in the URL, which is attached regardless of SendDefaultPii,
        // and the request HEADERS are attached too (SendDefaultPii suppresses only the SDK's OWN
        // user/IP/cookie attachment) — so the proxy's X-Forwarded-For would otherwise carry the caller's
        // IP out. SentryScrub reduces the headers to a diagnostic allowlist for that reason.
        //
        // BOTH hooks. Events and performance TRANSACTIONS are separate envelope types leaving through
        // separate hooks, and a transaction carries its own Request (url / query string / headers) from
        // the same ASP.NET scope. Scrubbing only events left ~TracesSampleRate of portal requests
        // uploading the vendor's bearer token (#386 review). Same net on both, like ADR 0037's frontend.
        options.SetBeforeSend(static (evt, _) => SentryScrub.Scrub(evt));
        options.SetBeforeSendTransaction(static (transaction, _) => SentryScrub.ScrubTransaction(transaction));

        // The one thing BeforeSendTransaction cannot repair: a transaction's Name is read-only by then
        // (and is copied into the envelope's dynamic-sampling header, which no hook rewrites). When
        // routing yields no name the SDK falls back to the raw path — token and all — so the name is
        // decided HERE, before the transaction exists. See SentryScrub.TransactionName.
        options.TransactionNameProvider = SentryScrub.TransactionName;
    }

    /// <summary>
    /// Brings the Sentry hub up NOW, so the startup block — migrations, the rule-catalog resolve, the
    /// system-template seed — is inside the reporting window rather than outside it.
    /// <para/>
    /// Without this the boot window is dark. The sink emits through <c>HubAdapter.Instance</c> (it runs
    /// with <c>InitializeSdk = false</c>), i.e. through the static <c>SentrySdk</c>, and that hub is only
    /// created when something first resolves <see cref="IHub"/> — which, with the MEL provider pipeline
    /// replaced by Serilog, means Sentry's own <c>IStartupFilter</c> as the request pipeline is built,
    /// during <c>app.Run()</c>. Every <c>Error</c> line raised before that was handed to a DISABLED hub
    /// and dropped. That window is load-bearing, not theoretical: EF migrations auto-apply at startup
    /// (ADR 0016) and the seed's failure is explicitly caught-and-logged, so a Neon hiccup or a malformed
    /// system rule is exactly the prod incident class #386 exists to make visible — and it was the one
    /// class the fix still missed.
    /// <para/>
    /// Resolving is the whole mechanism: the SDK's DI registration is what initialises the hub, so asking
    /// for it early is asking for it to exist early. Gated on the same <see cref="IsEnabled"/> as
    /// everything else, so a Development boot still resolves nothing and initialises nothing.
    /// </summary>
    public static void EnsureHubInitialized(
        IServiceProvider services, IConfiguration configuration, IHostEnvironment environment)
    {
        if (!IsEnabled(configuration, environment)) return;
        _ = services.GetRequiredService<IHub>();
    }

    /// <summary>
    /// Registers the Serilog → Sentry sink, and only when <see cref="IsEnabled"/>. <c>InitializeSdk</c>
    /// is false because <c>UseSentry</c> already initialised the SDK from the same DSN — the sink
    /// piggybacks on that hub, so one process has one Sentry client and the options above (the
    /// scrubber included) govern the events this sink raises.
    /// </summary>
    /// <remarks>
    /// <b>Events at <c>Error</c> and above; breadcrumbs at none.</b> The event level is the point of the
    /// ticket. The breadcrumb level is the PII boundary: a breadcrumb would ship the <c>Information</c>
    /// and <c>Warning</c> log stream to a third party, and that stream is exactly what
    /// <see href="https://github.com/neboxdev/complidrop/issues/378">#378</see> (open) says still embeds
    /// end-user email addresses — <c>"Resend not configured — skipping email to {To}"</c> is one line of
    /// many. Worse, <c>SentryEvent.Breadcrumbs</c> is read-only, so <see cref="SentryScrub"/> could not
    /// redact them the way it redacts everything else on the event. <c>Fatal</c> is the highest
    /// <see cref="LogEventLevel"/>, and this codebase logs nothing at that level, so this is "no
    /// breadcrumbs" spelled in the units the option accepts. Revisit when #378 closes — see ADR 0053.
    /// </remarks>
    public static LoggerConfiguration AddSentryErrorEvents(
        this LoggerConfiguration loggerConfiguration,
        IConfiguration configuration,
        IHostEnvironment environment)
    {
        if (!IsEnabled(configuration, environment)) return loggerConfiguration;

        return loggerConfiguration.WriteTo.Sentry(options =>
        {
            options.InitializeSdk = false;
            options.MinimumEventLevel = LogEventLevel.Error;
            options.MinimumBreadcrumbLevel = LogEventLevel.Fatal;
        });
    }
}
