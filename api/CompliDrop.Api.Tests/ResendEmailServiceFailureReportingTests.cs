using System.Net;
using CompliDrop.Api.Configuration;
using CompliDrop.Api.Services;
using CompliDrop.Api.Tests.TestHelpers;
using FluentAssertions;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CompliDrop.Api.Tests;

/// <summary>
/// #386 / ADR 0053 — which failed sends become metered Sentry events.
/// <para/>
/// Every <c>Error</c> line is now a Sentry event, so a failure mode that fans out with RECIPIENT count
/// can burn a plan's monthly quota, after which Sentry rate-limits and genuine 500s stop arriving. The
/// reminder worker therefore aggregates (<c>ReminderBackgroundService.MaxSendFailureEventsPerTick</c>)
/// and tells this service to stay at <c>Warning</c>. The half pinned here is the OTHER direction: a
/// one-off send — a verification mail, a reset link, a vendor portal link — has no caller-side aggregate
/// to fold into, so it must still raise its own <c>Error</c>. A blanket demotion would silence it.
/// </summary>
public sealed class ResendEmailServiceFailureReportingTests
{
    [Fact]
    public async Task A_one_off_send_that_resend_rejects_is_an_error()
    {
        var logger = new ListLogger<ResendEmailService>();

        var messageId = await BuildService(logger).SendAsync(
            "pat@gardenhall.example", "Verify your email", "<p>hi</p>", CancellationToken.None);

        messageId.Should().BeNull();
        logger.Entries.Should().ContainSingle()
            .Which.Level.Should().Be(LogLevel.Error,
                "a verification / reset / portal-link failure is its own incident and nothing else "
                + "reports it — demoting this blanket-silences the sends a customer actually notices");
    }

    [Fact]
    public async Task A_bulk_send_whose_caller_aggregates_is_only_a_warning()
    {
        var logger = new ListLogger<ResendEmailService>();

        var messageId = await BuildService(logger).SendAsync(
            "pat@gardenhall.example", "Your COI expires soon", "<p>hi</p>", CancellationToken.None,
            idempotencyKey: "reminder:1", failureReporting: SendFailureReporting.CallerAggregates);

        messageId.Should().BeNull("the return contract is identical — only the log LEVEL moves");
        var entry = logger.Entries.Should().ContainSingle().Subject;
        entry.Level.Should().Be(LogLevel.Warning);
        entry.Message.Should().Contain("422").And.Contain("validation_error",
            "the console/JSON sink still records the whole line — only the metered copy is folded up");
    }

    private static ResendEmailService BuildService(ILogger<ResendEmailService> logger) =>
        new(
            new StubHttpClientFactory(new StubHandler()),
            Options.Create(new ResendSettings { ApiKey = "re_test_key", FromEmail = "no-reply@example.com" }),
            new FakeHostEnvironment(),
            logger);

    /// <summary>Resend rejecting a recipient — the live 422 shape the ADR 0053 audit calls out.</summary>
    private sealed class StubHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.UnprocessableContent)
            {
                Content = new StringContent(
                    """{"name":"validation_error","message":"Invalid `to` field"}"""),
            });
    }

    private sealed class StubHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class FakeHostEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Production";
        public string ApplicationName { get; set; } = "CompliDrop.Api";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider ContentRootFileProvider { get; set; } = null!;
    }
}
