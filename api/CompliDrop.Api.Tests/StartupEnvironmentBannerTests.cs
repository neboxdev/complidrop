using CompliDrop.Api;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace CompliDrop.Api.Tests;

/// <summary>
/// Pure unit tests for <see cref="StartupEnvironmentBanner"/> — the #271 boot guard. Two contracts
/// matter and both are pinned here: (1) the SECURITY invariant that the banner never echoes a secret
/// (DB password, storage account key, Resend/Stripe key), and (2) the loud Development misconfig
/// guard that warns for any LIVE-looking target while staying silent in prod.
/// </summary>
/// <remarks>
/// Harness mirrors <see cref="RateLimitingGateTests"/>: a few in-memory config entries, a stub
/// <see cref="IHostEnvironment"/>, and a logger that captures level+message. No host boot needed —
/// the banner is a static helper.
/// </remarks>
public sealed class StartupEnvironmentBannerTests
{
    // A connection string whose password / account key / api keys are distinctive sentinels, so a
    // single not-contains assertion proves redaction regardless of how the field is rendered.
    private const string DbSecret = "SUPER_SECRET_DB_PASSWORD_42";
    // A realistic base64 account-key shape: contains '/', '+' and trailing '==' padding. The '=' chars
    // are the load-bearing case for the segment scan (it splits on the FIRST '='), so a real-account
    // connection string proves the key value can't leak even with internal/padding '='.
    private const string BlobKey = "AAAABBBBccccDDDD/eeee+FFFF_ACCOUNT_KEY==";
    private const string DbConnString =
        "Host=ep-sparkling-shape-a4inp0of.us-east-1.aws.neon.tech;Database=complidrop;Username=app;"
        + "Password=" + DbSecret + ";SSL Mode=Require";
    private const string RealBlobConnString =
        "DefaultEndpointsProtocol=https;AccountName=complidropstorage;AccountKey=" + BlobKey
        + ";EndpointSuffix=core.windows.net";

    // ---- security invariant: never echo a secret ------------------------------------------------

    [Fact]
    public void Describe_names_the_db_host_but_never_the_password()
    {
        var summary = StartupEnvironmentBanner.Describe(Config(("ConnectionStrings:Database", DbConnString)));

        summary.Database.Should().Contain("ep-sparkling-shape-a4inp0of.us-east-1.aws.neon.tech")
            .And.Contain("complidrop");
        summary.Database.Should().NotContain(DbSecret, "the DB password must never reach a log line");
    }

    [Fact]
    public void Describe_names_the_blob_account_but_never_the_account_key()
    {
        var summary = StartupEnvironmentBanner.Describe(Config(("AzureStorage:ConnectionString", RealBlobConnString)));

        summary.BlobStorage.Should().Contain("complidropstorage");
        summary.BlobStorage.Should().NotContain(BlobKey, "the storage account key must never reach a log line");
    }

    [Fact]
    public void Describe_reports_stripe_mode_but_never_the_key()
    {
        var summary = StartupEnvironmentBanner.Describe(Config(("Stripe:SecretKey", "sk_live_ABCDEF1234567890")));

        summary.Stripe.Should().Be("LIVE mode");
        summary.Stripe.Should().NotContain("ABCDEF1234567890");
    }

    [Fact]
    public void Describe_reports_email_live_but_never_the_resend_key()
    {
        var summary = StartupEnvironmentBanner.Describe(Config(("Resend:ApiKey", "re_secret_key_value")));

        summary.Email.Should().Contain("LIVE");
        summary.Email.Should().NotContain("re_secret_key_value");
    }

    [Fact]
    public void Log_emits_no_secret_in_any_line_even_with_a_fully_live_config()
    {
        var captured = new CapturingLogger();

        StartupEnvironmentBanner.Log(FullyLiveConfig(), Env("Development"), captured);

        var everything = string.Join("\n", captured.Messages.Select(m => m.Message));
        everything.Should().NotContain(DbSecret);
        everything.Should().NotContain(BlobKey);
        everything.Should().NotContain("sk_live_ABCDEF1234567890");
        everything.Should().NotContain("re_secret_key_value");
    }

    // ---- database describer ---------------------------------------------------------------------

    [Fact]
    public void Describe_database_handles_absent_connection_string()
    {
        StartupEnvironmentBanner.Describe(Config()).Database.Should().Be("not configured");
    }

    [Fact]
    public void Describe_database_does_not_crash_on_a_malformed_connection_string()
    {
        // The banner is a boot-path diagnostic: a garbage value must degrade to a safe label, not throw
        // (which would take startup down) and not echo the value.
        var summary = StartupEnvironmentBanner.Describe(Config(("ConnectionStrings:Database", "Port=not-a-number;@@@")));
        summary.Database.Should().Be("unparseable connection string");
    }

    [Fact]
    public void Describe_database_shows_a_non_default_port()
    {
        StartupEnvironmentBanner.Describe(Config(("ConnectionStrings:Database", "Host=localhost;Port=6543;Database=cd")))
            .Database.Should().Contain("localhost:6543").And.Contain("cd");
    }

    // ---- blob describer -------------------------------------------------------------------------

    [Theory]
    [InlineData("UseDevelopmentStorage=true")]
    [InlineData("usedevelopmentstorage=true")]
    [InlineData("UseDevelopmentStorage=true;")] // trailing ';' — idiomatic, must still classify as Azurite (#271 review)
    [InlineData("UseDevelopmentStorage=true;BlobEndpoint=http://127.0.0.1:10000/devstoreaccount1")] // retargeted-host form
    [InlineData("DefaultEndpointsProtocol=http;AccountName=devstoreaccount1;AccountKey=Eby8vdM02xNOcqF==;BlobEndpoint=http://127.0.0.1:10000/devstoreaccount1")]
    public void Describe_blob_recognizes_azurite(string connectionString)
    {
        StartupEnvironmentBanner.Describe(Config(("AzureStorage:ConnectionString", connectionString)))
            .BlobStorage.Should().Be("Azurite (local emulator)");
    }

    [Fact]
    public void Describe_blob_names_a_real_account()
    {
        StartupEnvironmentBanner.Describe(Config(("AzureStorage:ConnectionString", RealBlobConnString)))
            .BlobStorage.Should().Be("account 'complidropstorage'");
    }

    [Fact]
    public void Describe_blob_handles_absent_connection_string()
    {
        StartupEnvironmentBanner.Describe(Config()).BlobStorage.Should().Be("not configured");
    }

    // ---- email describer ------------------------------------------------------------------------

    [Fact]
    public void Describe_email_is_silent_without_a_key()
    {
        StartupEnvironmentBanner.Describe(Config()).Email.Should().Contain("silent");
    }

    [Fact]
    public void Describe_email_is_live_with_a_key()
    {
        // Only ApiKey set: binding applies FromEmail's non-empty default, so this models the real send
        // gate (WouldSend = ApiKey && FromEmail) the same way the runtime IOptions<ResendSettings> does.
        StartupEnvironmentBanner.Describe(Config(("Resend:ApiKey", "re_anything")))
            .Email.Should().Contain("LIVE");
    }

    [Fact]
    public void Describe_email_is_silent_when_from_email_is_blanked()
    {
        // The banner's email mode mirrors IEmailService.IsEnabled exactly (ResendSettings.WouldSend):
        // a key present but FromEmail explicitly emptied means the service would NOT send, so the banner
        // must say "silent" and NOT warn — not over-claim LIVE. Pins the no-drift contract (#271 review).
        var config = Config(("Resend:ApiKey", "re_anything"), ("Resend:FromEmail", ""));
        StartupEnvironmentBanner.Describe(config).Email.Should().Contain("silent");
        StartupEnvironmentBanner.LiveResourceWarnings(config).Should().BeEmpty();
    }

    // ---- stripe describer -----------------------------------------------------------------------

    [Theory]
    [InlineData("sk_test_abc", "test mode")]
    [InlineData("rk_test_abc", "test mode")]
    [InlineData("sk_live_abc", "LIVE mode")]
    [InlineData("rk_live_abc", "LIVE mode")]
    [InlineData("", "not configured")]
    [InlineData("pk_wrongprefix", "configured (unrecognized key prefix)")]
    // Real Stripe keys are ALWAYS lowercase-prefixed, so case-sensitive (Ordinal) matching is
    // intended: a wrong-case "SK_LIVE_" is not a usable Stripe key and is reported as unrecognized
    // (and so triggers no live warning). Pinned so a future case-insensitive change is deliberate.
    [InlineData("SK_LIVE_abc", "configured (unrecognized key prefix)")]
    public void Describe_stripe_classifies_by_key_prefix(string key, string expected)
    {
        StartupEnvironmentBanner.Describe(Config(("Stripe:SecretKey", key))).Stripe.Should().Be(expected);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Describe_treats_blank_db_and_blob_as_not_configured(string blank)
    {
        // The IsNullOrWhiteSpace guards on the DB + blob describers must short-circuit a whitespace-only
        // value before NpgsqlConnectionStringBuilder / the segment scan ever sees it.
        var summary = StartupEnvironmentBanner.Describe(Config(
            ("ConnectionStrings:Database", blank),
            ("AzureStorage:ConnectionString", blank)));
        summary.Database.Should().Be("not configured");
        summary.BlobStorage.Should().Be("not configured");
    }

    // ---- live-resource warnings (env-agnostic predicate set) ------------------------------------

    [Fact]
    public void No_warnings_when_every_target_is_dev_safe()
    {
        StartupEnvironmentBanner.LiveResourceWarnings(DevSafeConfig()).Should().BeEmpty();
    }

    [Fact]
    public void Warns_on_a_live_stripe_key()
    {
        StartupEnvironmentBanner.LiveResourceWarnings(Config(("Stripe:SecretKey", "sk_live_abc")))
            .Should().ContainSingle().Which.Should().Contain("Stripe:SecretKey").And.Contain("LIVE");
    }

    [Fact]
    public void Warns_on_a_present_resend_key()
    {
        StartupEnvironmentBanner.LiveResourceWarnings(Config(("Resend:ApiKey", "re_abc")))
            .Should().ContainSingle().Which.Should().Contain("Resend:ApiKey").And.Contain("REAL");
    }

    [Fact]
    public void Warns_on_a_real_blob_account()
    {
        StartupEnvironmentBanner.LiveResourceWarnings(Config(("AzureStorage:ConnectionString", RealBlobConnString)))
            .Should().ContainSingle().Which.Should().Contain("complidropstorage").And.Contain("Azurite");
    }

    [Fact]
    public void Does_not_warn_on_a_test_stripe_key_or_azurite()
    {
        StartupEnvironmentBanner.LiveResourceWarnings(Config(
            ("Stripe:SecretKey", "sk_test_abc"),
            ("AzureStorage:ConnectionString", "UseDevelopmentStorage=true")))
            .Should().BeEmpty();
    }

    [Fact]
    public void Collects_a_warning_for_every_live_target_at_once()
    {
        StartupEnvironmentBanner.LiveResourceWarnings(FullyLiveConfig()).Should().HaveCount(3);
    }

    // ---- Log: env gating ------------------------------------------------------------------------

    [Fact]
    public void Log_always_emits_the_info_banner()
    {
        var captured = new CapturingLogger();

        StartupEnvironmentBanner.Log(DevSafeConfig(), Env("Production"), captured);

        captured.Messages.Should().ContainSingle(m => m.Level == LogLevel.Information)
            .Which.Message.Should().Contain("Startup environment").And.Contain("Production");
    }

    [Fact]
    public void Log_suppresses_live_warnings_outside_development()
    {
        var captured = new CapturingLogger();

        // A fully-live config is CORRECT in Production — no warnings should fire there.
        StartupEnvironmentBanner.Log(FullyLiveConfig(), Env("Production"), captured);

        captured.Messages.Where(m => m.Level == LogLevel.Warning).Should().BeEmpty();
    }

    [Fact]
    public void Log_emits_live_warnings_in_development()
    {
        var captured = new CapturingLogger();

        StartupEnvironmentBanner.Log(FullyLiveConfig(), Env("Development"), captured);

        captured.Messages.Where(m => m.Level == LogLevel.Warning).Should().HaveCount(3);
    }

    [Fact]
    public void Log_emits_no_warnings_for_a_dev_safe_development_config()
    {
        var captured = new CapturingLogger();

        StartupEnvironmentBanner.Log(DevSafeConfig(), Env("Development"), captured);

        captured.Messages.Where(m => m.Level == LogLevel.Warning).Should().BeEmpty();
        captured.Messages.Should().Contain(m => m.Level == LogLevel.Information);
    }

    // ---- harness --------------------------------------------------------------------------------

    private static IConfiguration Config(params (string key, string value)[] entries) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(entries.Select(e => new KeyValuePair<string, string?>(e.key, e.value)))
            .Build();

    /// <summary>The #271 target end-state: isolated dev DB, Azurite, no Resend key, test Stripe.</summary>
    private static IConfiguration DevSafeConfig() => Config(
        ("ConnectionStrings:Database", "Host=dev-host.neon.tech;Database=cd_dev;Username=app;Password=" + DbSecret),
        ("AzureStorage:ConnectionString", "UseDevelopmentStorage=true"),
        ("Stripe:SecretKey", "sk_test_abc"));
    // Resend:ApiKey deliberately absent → email-silent.

    /// <summary>Every data-bearing target wired to a live/prod resource — the #271 hazard config.</summary>
    private static IConfiguration FullyLiveConfig() => Config(
        ("ConnectionStrings:Database", DbConnString),
        ("AzureStorage:ConnectionString", RealBlobConnString),
        ("Stripe:SecretKey", "sk_live_ABCDEF1234567890"),
        ("Resend:ApiKey", "re_secret_key_value"));

    private static IHostEnvironment Env(string name) => new StubEnv(name);

    private sealed class StubEnv(string environmentName) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = environmentName;
        public string ApplicationName { get; set; } = "CompliDrop.Api.Tests";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } =
            new Microsoft.Extensions.FileProviders.NullFileProvider();
    }

    /// <summary>Captures every log entry's level + formatted message.</summary>
    private sealed class CapturingLogger : ILogger
    {
        public List<(LogLevel Level, string Message)> Messages { get; } = [];

        public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            Messages.Add((logLevel, formatter(state, exception)));

        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();
            public void Dispose() { }
        }
    }
}
