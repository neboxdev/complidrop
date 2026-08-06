using CompliDrop.Api.Tests.TestHelpers;
using FluentAssertions;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Serilog.Core;
using Serilog.Events;
using static CompliDrop.Api.Tests.TestHelpers.UploadFixtures;

namespace CompliDrop.Api.Tests;

/// <summary>
/// The wiring half of #390 item 2 (<see cref="ExceptionHandlingMiddlewareTests"/> owns the
/// discrimination): a real request through the real pipeline, aborted mid-handler, must leave NO
/// error-level trace behind — and exactly one Information-level one, carrying the 499. Both halves
/// matter: without the second, the first passes just as well on a host that logged nothing at all.
/// <para/>
/// It takes both fixes to pass, which is the point of asserting on the log rather than on the status
/// code. A client abort produced TWO error lines: Serilog's request log (which sits INSIDE the
/// exception middleware and hard-codes "responded 500" at Error for anything that throws past it) and
/// then the middleware's own <c>LogError</c>. Reverting either one alone turns this red.
/// <para/>
/// The route is the PUBLIC portal upload — no auth to arrange on the second host, and a vendor
/// uploading from a phone is the likeliest client in the product to disappear mid-request.
/// </summary>
public sealed class ClientAbortLoggingTests(IntegrationTestFixture fixture) : IntegrationTestBase(fixture)
{
    [Fact]
    public async Task An_aborted_request_is_logged_as_neither_an_error_nor_a_500()
    {
        var sink = new CapturingLogEventSink();
        await using var factory = Fixture.Factory.WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services => services.AddSingleton<ILogEventSink>(sink)));
        var link = await SeedLinkAsync();

        var req = new HttpRequestMessage(HttpMethod.Post, $"/api/portal/{link.Token}/upload")
        {
            Content = UploadForm(PdfBytes(), "coi.pdf", "application/pdf"),
        };
        req.Headers.Add(ClientAbortStartupFilter.HeaderName, "1");
        var resp = await factory.CreateClient().SendAsync(req);

        // 499 "client closed request" — unreadable by the client that left, but it keeps the request log
        // honest. The default 200 would file an abandoned upload as a success; the old 500 filed a
        // server fault that never happened.
        ((int)resp.StatusCode).Should().Be(499,
            "the abort must reach the handler (a guard refusing the request earlier would answer 4xx and "
            + "this test would prove nothing about aborts)");

        var errors = sink.Events
            .Where(e => e.Level >= LogEventLevel.Error)
            .Where(e => e.Exception is OperationCanceledException
                || e.RenderMessage().Contains(link.Token, StringComparison.Ordinal)
                || e.RenderMessage().Contains("Unhandled exception", StringComparison.Ordinal))
            .Select(e => $"{e.Level}: {e.RenderMessage()}")
            .ToList();

        errors.Should().BeEmpty(
            "a closed tab is not a server error: an Error line per abandoned request is noise the "
            + "operator cannot act on, and becomes a phantom Sentry event once the backend DSN is live");

        // POSITIVE CONTROL for the emptiness above, and the pin under the 499 (#390 review). "No error
        // line" is also what a host that logged NOTHING would produce, so the assertion has to be able to
        // tell DEMOTED from DISAPPEARED. It can, because exactly one line still carries the status: the
        // framework's own request-completion event, which is emitted by
        // Microsoft.AspNetCore.Hosting.Diagnostics OUTSIDE ExceptionHandlingMiddleware and reads
        // Response.StatusCode after the pipeline has unwound — so it sees the 499 the middleware set.
        //
        // It is also the ONLY in-process trace of the 499 in production, and it survives on an accident
        // worth pinning: appsettings.json's `Logging:LogLevel:Microsoft.AspNetCore = Warning` is MEL
        // config, and UseSerilog bypasses MEL filtering, so with no
        // `Serilog:MinimumLevel:Override:Microsoft.AspNetCore` this line runs at Information. Adding that
        // conventional override — which Serilog's own docs recommend alongside UseSerilogRequestLogging —
        // would delete it. This assertion is what fails when someone does; see the note beside
        // UseSerilogRequestLogging in Program.cs.
        //
        // Serilog's own request line cannot serve as the trace: it is registered INSIDE the exception
        // middleware and hard-codes "responded 500" on its exception path, so it is demoted to Debug
        // rather than corrected (and is absent from this sink for exactly that reason).
        sink.Events.Should().Contain(
            e => e.Level == LogEventLevel.Information
                && Scalar(e, "SourceContext") == "Microsoft.AspNetCore.Hosting.Diagnostics"
                && Scalar(e, "StatusCode") == "499",
            "the 499 is only worth setting if something records it — an aborted request must leave an "
            + "Information-level completion line carrying that status, not silence");
    }

    /// <summary>The scalar value of one Serilog property, unquoted, or null when it is absent.</summary>
    private static string? Scalar(LogEvent e, string property) =>
        e.Properties.TryGetValue(property, out var value) && value is ScalarValue { Value: { } v }
            ? v.ToString()
            : null;
}
