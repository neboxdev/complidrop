using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Sentry.AspNetCore;
using Sentry.Extensibility;
using Serilog;
using Serilog.Extensions.Logging;

namespace CompliDrop.Api.Tests.TestHelpers;

/// <summary>
/// Production Sentry options + the production Serilog sink + a capturing <see cref="ITransport"/>
/// (#386 / ADR 0053). Nothing between a <c>logger.LogError</c> call and the wire is faked, so what a
/// test asserts on here is the envelope a real deploy would upload.
/// <para/>
/// Lives in <c>TestHelpers</c> rather than inside one test class because two suites need it —
/// <c>BackendSentryTests</c> for the PII posture and <c>ReminderBackgroundServiceTests</c> for the
/// per-tick event bound — and a second copy would be free to drift from the production options it
/// exists to exercise.
/// </summary>
/// <remarks>
/// The static <c>SentrySdk</c> hub is process-global. That is safe because the suite is serial
/// (<c>AssemblyInfo</c>'s <c>DisableTestParallelization</c>) and this harness closes the SDK on dispose.
/// </remarks>
internal sealed class SentryCaptureHarness : IDisposable
{
    internal const string FakeDsn = "https://0123456789abcdef0123456789abcdef@o0.ingest.us.sentry.io/1";

    private readonly IDisposable _sdk;
    private readonly CapturingSentryTransport _transport = new();
    private readonly Serilog.Core.Logger _serilog;

    public SentryCaptureHarness(
        bool dsnConfiguredForSink = true,
        string sinkEnvironment = "Production",
        double? tracesSampleRate = null)
    {
        var options = new SentryAspNetCoreOptions();
        // The SDK half is configured from the REAL helper; only the trace sample rate is pinned, and
        // only through the same configuration key production reads — a transaction test that set
        // options.TracesSampleRate afterwards would be asserting on a path prod never takes.
        BackendSentry.ConfigureOptions(
            options,
            tracesSampleRate is { } rate
                ? Configuration(FakeDsn, rate.ToString(CultureInfo.InvariantCulture))
                : Configuration(FakeDsn),
            Env("Production"));
        options.Transport = _transport;
        options.AutoSessionTracking = false;
        options.SendClientReports = false;
        _sdk = SentrySdk.Init(options);

        _serilog = new LoggerConfiguration()
            .MinimumLevel.Verbose()
            .Enrich.FromLogContext()
            .AddSentryErrorEvents(Configuration(dsnConfiguredForSink ? FakeDsn : null), Env(sinkEnvironment))
            .CreateLogger();

        LoggerFactory = new SerilogLoggerFactory(_serilog);
    }

    public ILoggerFactory LoggerFactory { get; }

    public Microsoft.Extensions.Logging.ILogger Logger(string category) => LoggerFactory.CreateLogger(category);

    public ILogger<T> Logger<T>() => LoggerFactory.CreateLogger<T>();

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

    public static IConfiguration Configuration(string? dsn, string? tracesSampleRate = null) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [BackendSentry.DsnKey] = dsn,
                [BackendSentry.TracesSampleRateKey] = tracesSampleRate,
            })
            .Build();

    public static IHostEnvironment Env(string environmentName) => new FakeHostEnvironment(environmentName);

    public void Dispose()
    {
        _sdk.Dispose();
        _serilog.Dispose();
    }

    private sealed class FakeHostEnvironment(string environmentName) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = environmentName;
        public string ApplicationName { get; set; } = "CompliDrop.Api";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider ContentRootFileProvider { get; set; } = null!;
    }
}
