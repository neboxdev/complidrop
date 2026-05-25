using CompliDrop.Api.Middleware;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CompliDrop.Api.Tests;

/// <summary>
/// Pure unit tests for <see cref="RateLimitingGate"/>: covers the four (env × configured) cases
/// plus the implicit fifth — the absent-key default.
/// </summary>
/// <remarks>
/// The harness is a few in-memory configuration entries plus a stub <see cref="IHostEnvironment"/>.
/// No <see cref="WebApplication"/> boot needed — the gate is a static helper.
/// </remarks>
public sealed class RateLimitingGateTests
{
    [Fact]
    public void Defaults_to_true_when_key_absent_in_production()
    {
        RateLimitingGate.ShouldEnable(Env("Production"), Config(), NullLogger.Instance)
            .Should().BeTrue("the absent key should default to on so a missing config in prod doesn't disable the limiter");
    }

    [Fact]
    public void Defaults_to_true_when_key_absent_in_development()
    {
        RateLimitingGate.ShouldEnable(Env("Development"), Config(), NullLogger.Instance)
            .Should().BeTrue();
    }

    [Fact]
    public void Honors_true_in_production()
    {
        RateLimitingGate.ShouldEnable(Env("Production"), Config(("RateLimiting:Enabled", "true")), NullLogger.Instance)
            .Should().BeTrue();
    }

    [Fact]
    public void Honors_false_in_development()
    {
        RateLimitingGate.ShouldEnable(Env("Development"), Config(("RateLimiting:Enabled", "false")), NullLogger.Instance)
            .Should().BeFalse("Development is the only environment that may disable the limiter — integration tests rely on this");
    }

    [Fact]
    public void Force_enables_in_production_when_disable_attempted()
    {
        var captured = new CapturingLogger();

        var result = RateLimitingGate.ShouldEnable(
            Env("Production"),
            Config(("RateLimiting:Enabled", "false")),
            captured);

        result.Should().BeTrue("the gate must ignore a disable attempt outside Development");
        captured.Warnings.Should().ContainSingle()
            .Which.Should().Contain("forced ON")
            .And.Contain("Production");
    }

    [Fact]
    public void Force_enables_in_staging_when_disable_attempted()
    {
        var captured = new CapturingLogger();

        var result = RateLimitingGate.ShouldEnable(
            Env("Staging"),
            Config(("RateLimiting:Enabled", "false")),
            captured);

        result.Should().BeTrue();
        captured.Warnings.Should().ContainSingle()
            .Which.Should().Contain("Staging");
    }

    [Fact]
    public void Does_not_log_warning_when_enabled_normally()
    {
        var captured = new CapturingLogger();

        RateLimitingGate.ShouldEnable(
            Env("Production"),
            Config(("RateLimiting:Enabled", "true")),
            captured);

        captured.Warnings.Should().BeEmpty();
    }

    private static IConfiguration Config(params (string key, string value)[] entries) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(entries.Select(e => new KeyValuePair<string, string?>(e.key, e.value)))
            .Build();

    private static IHostEnvironment Env(string name) => new StubEnv(name);

    private sealed class StubEnv(string environmentName) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = environmentName;
        public string ApplicationName { get; set; } = "CompliDrop.Api.Tests";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } =
            new Microsoft.Extensions.FileProviders.NullFileProvider();
    }

    /// <summary>Minimal logger that captures warning-level message strings (formatted).</summary>
    private sealed class CapturingLogger : ILogger
    {
        public List<string> Warnings { get; } = [];

        public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Warning)
                Warnings.Add(formatter(state, exception));
        }

        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();
            public void Dispose() { }
        }
    }
}
