using System.Net;
using CompliDrop.Api.Tests.TestHelpers;
using FluentAssertions;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Serilog.Core;
using Serilog.Events;

namespace CompliDrop.Api.Tests;

/// <summary>
/// The liveness/readiness split (#390 items 1 and 4, ADR 0053). Three endpoints answer two different
/// questions, and which one a consumer polls decides what an outage looks like:
/// <list type="bullet">
/// <item><c>/health</c> + <c>/health/live</c> — "the process is up". DB-BLIND, deliberately: Railway's
/// restart/deploy healthcheck path is configured outside this repo, and a DB-touching liveness probe
/// would let a transient Neon blip kill a healthy container into ADR 0016's fail-fast boot.</item>
/// <item><c>/health/ready</c> — "the process can serve". The only one that touches the database, so it
/// is the one an uptime monitor belongs on, and the only one that goes red in the #226 shape.</item>
/// </list>
/// Both halves are pinned here because both are silently reversible: folding a database check into
/// <c>/health</c> looks like a strict improvement, and dropping the readiness probe's DB touch looks
/// like a simplification.
/// </summary>
public sealed class HealthProbeTests(IntegrationTestFixture fixture) : IntegrationTestBase(fixture)
{
    [Fact]
    public async Task Liveness_probes_stay_green_and_never_touch_the_database()
    {
        var connections = Fixture.Factory.Services.GetRequiredService<SystemConnectionFaultInterceptor>();
        var client = CreateClient();

        HttpResponseMessage health, live;
        connections.Armed = true;
        try
        {
            health = await client.GetAsync("/health");
            live = await client.GetAsync("/health/live");
        }
        finally
        {
            var faults = connections.FaultCount;
            connections.Reset();
            faults.Should().Be(0,
                "a liveness probe that opens a database connection lets a transient Neon blip restart a "
                + "healthy container — and the restart re-enters the fail-fast boot while the database is "
                + "still blipping, turning a 30-second blip into a hard outage (ADR 0053)");
        }

        health.StatusCode.Should().Be(HttpStatusCode.OK, "the process is up, which is all /health claims");
        live.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Readiness_answers_503_when_the_database_is_unreachable_and_names_the_cause_only_in_the_log()
    {
        var sink = new CapturingLogEventSink();
        await using var factory = Fixture.Factory.WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services => services.AddSingleton<ILogEventSink>(sink)));
        var connections = factory.Services.GetRequiredService<SystemConnectionFaultInterceptor>();

        HttpResponseMessage resp;
        connections.Armed = true;
        try
        {
            resp = await factory.CreateClient().GetAsync("/health/ready");
        }
        finally
        {
            connections.FaultCount.Should().BeGreaterThan(0,
                "precondition: the probe must have actually tried to reach the database — a readiness "
                + "check that never opens a connection cannot report readiness at all");
            connections.Reset();
        }

        resp.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);

        // The cause is not thrown away: this branch answered a SILENT 503 before #390, so a real
        // outage left no trace on our side at all — the same blindness that made #226 invisible.
        sink.Events.Should().Contain(
            e => e.Level >= LogEventLevel.Warning && e.RenderMessage().Contains("Readiness probe failed"),
            "the reason belongs server-side, where the operator is");

        // …and not in the body either. Belt-and-braces here rather than the pin: EF's CanConnectAsync
        // swallows connection/auth failures and answers FALSE, so this run — like every ordinary DB
        // incident — takes the bare-503 branch above and never reaches the catch that used to echo
        // `ex.Message`. The narrow residual surface is pinned in the source instead, by
        // The_readiness_handler_never_puts_an_exception_message_in_its_response below.
        var body = await resp.Content.ReadAsStringAsync();
        body.Should().NotContain(SystemConnectionFaultInterceptor.FaultMessage);
        body.Should().NotContain("host=", "a 503 must not fingerprint the infrastructure it failed to reach");
    }

    [Fact]
    public void The_readiness_handler_never_puts_an_exception_message_in_its_response()
    {
        // The behavioural test above cannot reach the catch: EF answers false for the exceptions a real
        // DB incident raises, so "password authentication failed for user …" never gets as far as the
        // response — the ticket's own red-team correction, and the reason item 4 is Low rather than a
        // broad infra disclosure. What survives is narrow (whatever EF does NOT swallow) but the rule is
        // absolute for a PUBLIC, unauthenticated endpoint, so it is pinned where it is decidable.
        var source = File.ReadAllText(SourceScan.ProductionFile("Program.cs"));
        var handler = SourceScan.ExtractMethodBody(source, "app.MapGet(\"/health/ready\"");

        handler.Should().NotContain("ex.Message",
            "an exception message from the data layer names the host, database and user it could not reach");
        handler.Should().NotContain("ex.ToString");
        handler.Should().Contain("logger.Log",
            "the cause has to go somewhere — a bare 503 that logs nothing is the silence this ticket is about");
    }

    [Fact]
    public async Task Readiness_answers_200_when_the_database_is_reachable()
    {
        // The other side of the decision, so neither branch can be lost: an always-503 readiness probe
        // would satisfy the test above and page the founder on every poll.
        var resp = await CreateClient().GetAsync("/health/ready");

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        (await resp.Content.ReadAsStringAsync()).Should().Contain("ready");
    }
}
