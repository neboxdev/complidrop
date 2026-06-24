using System.Collections.Concurrent;
using CompliDrop.Api.Tests.TestHelpers;
using FluentAssertions;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Serilog.Core;
using Serilog.Events;
using Testcontainers.PostgreSql;

namespace CompliDrop.Api.Tests;

/// <summary>
/// Proves <see cref="StartupEnvironmentBanner"/> is actually WIRED into <c>Program.cs</c> startup
/// (#271) — not just correct in isolation. Every sibling boot guard pairs its pure-unit tests
/// (<see cref="StartupEnvironmentBannerTests"/>) with a real-host wiring test; this is the banner's.
/// </summary>
/// <remarks>
/// The banner LOGS rather than THROWS, so — unlike <see cref="DatabaseMigratorStartupTests"/>, which
/// pins its guard via an aborted boot — the wiring is pinned by capturing the host's log output.
/// <c>Program.cs</c> composes Serilog with <c>ReadFrom.Services(services)</c>, so a DI-registered
/// <see cref="ILogEventSink"/> receives every event the host logs. We register a capturing sink, boot,
/// and assert the banner line was emitted. Deleting the <c>StartupEnvironmentBanner.Log(...)</c> call
/// from <c>Program.cs</c> makes this fail.
/// <para/>
/// Owns its own container (like <see cref="DatabaseMigratorStartupTests"/>) — xUnit constructs the
/// class per test method.
/// </remarks>
public sealed class StartupEnvironmentBannerStartupTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _container =
        new PostgreSqlBuilder("postgres:17-alpine").Build();

    public Task InitializeAsync() => _container.StartAsync();

    public Task DisposeAsync() => _container.DisposeAsync().AsTask();

    [Fact]
    public void Host_logs_the_startup_environment_banner_on_boot()
    {
        var sink = new CapturingLogEventSink();

        // WithWebHostBuilder layers an extra ConfigureTestServices over CustomWebApplicationFactory's
        // base setup; the sink lands in the final service collection that ReadFrom.Services reads.
        using var factory = new CustomWebApplicationFactory(_container.GetConnectionString())
            .WithWebHostBuilder(b => b.ConfigureTestServices(s => s.AddSingleton<ILogEventSink>(sink)));

        using var _ = factory.CreateClient(); // boots the host → runs the Program.cs startup scope

        // The banner is an INFO line naming the resolved targets. At-least-one (not exactly-one) so the
        // test pins "the call happens" without coupling to host-boot internals that might log twice.
        sink.Events.Should().Contain(
            e => e.Level == LogEventLevel.Information && e.RenderMessage().Contains("Startup environment"),
            "Program.cs must call StartupEnvironmentBanner.Log at boot — the durable #271 guard");

        var banner = sink.Events.First(
            e => e.Level == LogEventLevel.Information && e.RenderMessage().Contains("Startup environment"));
        // CustomWebApplicationFactory boots in Development, and the line names the DB target.
        banner.RenderMessage().Should().Contain("Development").And.Contain("Database:");
    }

    /// <summary>Serilog sink that records every emitted event; thread-safe for the host's loggers.</summary>
    private sealed class CapturingLogEventSink : ILogEventSink
    {
        private readonly ConcurrentQueue<LogEvent> _events = new();
        public void Emit(LogEvent logEvent) => _events.Enqueue(logEvent);
        public IReadOnlyCollection<LogEvent> Events => _events.ToArray();
    }
}
