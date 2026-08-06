using System.Net;
using System.Text.RegularExpressions;
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
        int afterLiveness, afterReadiness;
        connections.Armed = true;
        try
        {
            health = await client.GetAsync("/health");
            live = await client.GetAsync("/health/live");
            afterLiveness = connections.FaultCount;

            // POSITIVE CONTROL, inside the SAME armed window. A fault count of zero is also exactly what
            // a mis-wired harness yields — an interceptor never registered, or a second host resolving a
            // different singleton — so on its own the assertion below cannot tell "liveness asked
            // nothing" from "the hook was never live". One readiness probe settles it: that is the one
            // endpoint that DOES open a connection, so a count that MOVES here proves the count that
            // stayed put above was a real observation.
            await client.GetAsync("/health/ready");
            afterReadiness = connections.FaultCount;
        }
        finally
        {
            connections.Reset();
        }

        // Asserted after the try, not inside the finally: a finally that throws its own assertion
        // REPLACES whatever the try was failing with, so the harness's real error would never be seen.
        afterLiveness.Should().Be(0,
            "a liveness probe that opens a database connection lets a transient Neon blip restart a "
            + "healthy container — and the restart re-enters the fail-fast boot while the database is "
            + "still blipping, turning a 30-second blip into a hard outage (ADR 0053)");
        afterReadiness.Should().Be(1,
            "the fault hook has to have been ARMED for the zero above to mean anything — the readiness "
            + "probe is the one endpoint that opens a connection, so it is the control");

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
        //
        // Pinned by SHAPE, not by a blacklist of spellings. The rule is "nothing the data layer said
        // reaches the response", and two literals cannot express it: ex.InnerException.Message,
        // ex.GetBaseException().Message, $"{ex}", Results.Problem(detail: $"{ex}") and
        // Results.Json(new { error = ex }) — serializing an exception emits its message — all sail past
        // one, and renaming the caught variable defeats any of them (#390 review, S2). So instead:
        // enumerate what the recovery path may RETURN, and where the exception may be READ.
        var source = File.ReadAllText(SourceScan.ProductionFile("Program.cs"));
        var handler = SourceScan.ExtractMethodBody(source, "app.MapGet(\"/health/ready\"");
        var recovery = SourceScan.ExtractMethodBody(handler, "catch (");

        // 0. The two spellings that were actually there before #390, kept for the legible failure.
        handler.Should().NotContain("ex.Message",
            "an exception message from the data layer names the host, database and user it could not reach");
        handler.Should().NotContain("ex.ToString");

        // 1. The recovery path produces exactly one response, and that response carries no payload at
        //    all. Counting every `Results.` is what makes this a whitelist: a second one, whatever it
        //    is spelled and whatever it wraps, fails here.
        SourceScan.Count(recovery, "Results.").Should().Be(1,
            "the failure branch answers with one thing and one thing only");
        recovery.Should().Contain("Results.StatusCode(503)",
            "a bare status — anything with a body is a body we would have to audit for what it names");

        // 2. The caught exception may be read in exactly one place: handed WHOLE to the logger as its
        //    exception argument. Every other mention is a candidate response payload — formatted into a
        //    string, projected into an anonymous object, unwrapped through InnerException. Keyed on the
        //    name the catch clause actually declares, so renaming `ex` cannot slip past.
        var caught = Regex.Match(handler, @"catch\s*\(\s*\w[\w.<>]*\s+(?<name>\w+)\s*\)");
        caught.Success.Should().BeTrue(
            "the recovery path must name its exception for this pin to be able to follow it");
        var name = caught.Groups["name"].Value;

        var mentions = handler.Split('\n')
            .Select(line => line.Trim())
            .Where(line => Regex.IsMatch(line, $@"\b{Regex.Escape(name)}\b"))
            .ToList();

        mentions.Should().OnlyContain(
            line => line.StartsWith("catch (", StringComparison.Ordinal)
                || (line.StartsWith("logger.Log", StringComparison.Ordinal)
                    && line.Contains($"({name},", StringComparison.Ordinal)),
            "the exception may be declared and handed to the logger, and nothing else — every other "
            + "mention is a value that could end up in a body served to an unauthenticated caller");

        // 3. …and it does have to reach the logger: a bare 503 that records nothing is the silence this
        //    ticket is about, on the branch where we know the most.
        handler.Should().Contain("logger.Log",
            "the cause has to go somewhere — a bare 503 that logs nothing is the silence this ticket is about");
        recovery.Should().Contain($"logger.Log", "including on the branch EF did not swallow");
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
