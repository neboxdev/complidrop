using CompliDrop.Api.Tests.TestHelpers;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace CompliDrop.Api.Tests;

/// <summary>
/// The composed-host half of #386 / ADR 0053. <see cref="BackendSentryTests"/> proves the helpers
/// behave; this proves the real <c>Program.cs</c> composition — Serilog built from real configuration,
/// real user-secrets layering, real environment — comes out silent in Development.
/// </summary>
/// <remarks>
/// This is the test the #386 finding is about. The production DSN was sitting in the local user-secrets
/// store, which the integration-test host reads too (same <c>UserSecretsId</c>, Development
/// environment). Before #386 that only bought a few performance traces; afterwards every
/// <c>LogError</c> is an event, and the dev database is a CLONE OF PROD DATA — so an exception naming a
/// real vendor would have been uploaded to the production Sentry project by nothing more than running
/// the suite. The override below puts a DSN back in deliberately, because a test that relies on the
/// key being absent proves nothing about the gate.
/// </remarks>
[Collection("integration")]
public sealed class BackendSentryStartupTests(IntegrationTestFixture fixture) : IntegrationTestBase(fixture)
{
    private const string FakeDsn = "https://0123456789abcdef0123456789abcdef@o0.ingest.us.sentry.io/1";

    [Fact]
    public async Task The_composed_host_reports_nothing_in_development_even_with_a_dsn_configured()
    {
        // Boot FIRST: the host is what decides whether a Sentry sink is attached to Serilog, and if the
        // Development gate were removed its own UseSentry would initialise the SDK. Taking the hub over
        // afterwards is therefore what makes this discriminating — the capture below is reached only
        // through a sink the host actually registered.
        await using var factory = new CustomWebApplicationFactory(
            Fixture.ConnectionString,
            new Dictionary<string, string?> { ["Sentry:Dsn"] = FakeDsn });
        using var client = factory.CreateClient();

        var transport = new CapturingSentryTransport();
        using var sdk = SentrySdk.Init(options =>
        {
            options.Dsn = FakeDsn;
            options.Transport = transport;
            options.AutoSessionTracking = false;
            options.SendClientReports = false;
        });

        // The shape of every worker-tick failure: an Error line with an exception, through the host's
        // own logger factory.
        factory.Services.GetRequiredService<ILoggerFactory>()
            .CreateLogger("BackendSentryStartupTests")
            .LogError(new InvalidOperationException("boom"), "Compliance sweep failed.");

        await SentrySdk.FlushAsync(TimeSpan.FromSeconds(20));

        transport.Payloads.Should().BeEmpty(
            "a Development boot must export nothing, whatever a machine's user-secrets happen to hold");
    }
}
