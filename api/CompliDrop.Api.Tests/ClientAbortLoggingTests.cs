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
/// error-level trace behind.
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
    }
}
