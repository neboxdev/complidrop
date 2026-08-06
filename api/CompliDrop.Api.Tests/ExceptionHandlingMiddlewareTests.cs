using System.Text.Json;
using CompliDrop.Api.Middleware;
using CompliDrop.Api.Tests.TestHelpers;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Logging;

namespace CompliDrop.Api.Tests;

/// <summary>
/// The one place that decides whether a request that threw is OUR failure or the client's departure
/// (#390 item 2). Before the carve-out every <c>OperationCanceledException</c> — including the one every
/// closed tab produces on an endpoint bound to <c>RequestAborted</c> — was logged Error-with-stack and
/// answered with the 500 envelope, to a socket nobody was reading. Log noise today; once the backend
/// Sentry DSN is live (#386) it is a phantom error EVENT per abandoned request.
/// <para/>
/// These are unit tests on purpose: the discrimination lives in the <c>when</c> clause, and the only way
/// to prove the clause DISCRIMINATES is to throw the same exception TYPE on both sides of it. An
/// integration test can construct the client-abort side (<see cref="ClientAbortLoggingTests"/> does) but
/// not the other one, where a cancellation belongs to somebody else's token — an <c>HttpClient</c>'s own
/// 30s timeout, a shutdown token — and must still be a loud 500.
/// </summary>
public sealed class ExceptionHandlingMiddlewareTests
{
    private static (HttpContext Context, MemoryStream Body) NewContext()
    {
        var body = new MemoryStream();
        var context = new DefaultHttpContext();
        context.Response.Body = body;
        context.Items["CorrelationId"] = "corr-123";
        return (context, body);
    }

    private static async Task<string> InvokeAsync(
        HttpContext context, MemoryStream body, ListLogger<ExceptionHandlingMiddleware> logger, Exception thrown)
    {
        var middleware = new ExceptionHandlingMiddleware(_ => Task.FromException(thrown), logger);
        await middleware.InvokeAsync(context);
        return System.Text.Encoding.UTF8.GetString(body.ToArray());
    }

    [Fact]
    public async Task A_client_abort_answers_499_with_no_body_and_logs_at_debug()
    {
        var (context, body) = NewContext();
        using var abort = new CancellationTokenSource();
        context.RequestAborted = abort.Token;
        await abort.CancelAsync();
        var logger = new ListLogger<ExceptionHandlingMiddleware>();

        var payload = await InvokeAsync(context, body, logger, new OperationCanceledException(abort.Token));

        payload.Should().BeEmpty("there is nobody left to read an error envelope");
        context.Response.StatusCode.Should().Be(499,
            "the request log must not record an abandoned request as the 200 it would otherwise default to");
        logger.Entries.Should().NotContain(e => e.Level >= LogLevel.Error,
            "a closed tab is not a server fault — Error-with-stack here is noise the operator cannot act on");
        logger.Entries.Should().Contain(e => e.Level == LogLevel.Debug && e.Message.Contains("aborted"),
            "silence is worse than noise: the abort still has to be observable when someone goes looking");
    }

    [Fact]
    public async Task A_cancellation_that_is_not_the_client_leaving_is_still_a_server_error()
    {
        // THE discriminating case. `TaskCanceledException` is an `OperationCanceledException`, and it is
        // what an HttpClient raises on its OWN timeout — tied to its internal token, never to
        // RequestAborted. A bare `catch (OperationCanceledException)` swallows this one too: the caller
        // gets a silent 499 for a failure that is entirely ours, and the log says nothing. The same
        // distinction the codebase already draws at BlobStorageService.UploadAsync (#248) and
        // VendorEndpoints' invite send (#249).
        var (context, body) = NewContext();
        context.RequestAborted.IsCancellationRequested.Should().BeFalse("precondition: the client is still here");
        var logger = new ListLogger<ExceptionHandlingMiddleware>();

        var payload = await InvokeAsync(
            context, body, logger, new TaskCanceledException("simulated 30s outbound HTTP timeout"));

        context.Response.StatusCode.Should().Be(StatusCodes.Status500InternalServerError);
        JsonDocument.Parse(payload).RootElement.GetProperty("error").GetProperty("code").GetString()
            .Should().Be("server.error");
        logger.Entries.Should().Contain(e => e.Level == LogLevel.Error,
            "a timeout inside our own request IS a fault, and it must keep its stack");
    }

    [Fact]
    public async Task An_ordinary_exception_still_returns_the_500_envelope_with_the_correlation_id()
    {
        var (context, body) = NewContext();
        var logger = new ListLogger<ExceptionHandlingMiddleware>();

        var payload = await InvokeAsync(context, body, logger, new InvalidOperationException("boom"));

        context.Response.StatusCode.Should().Be(StatusCodes.Status500InternalServerError);
        var error = JsonDocument.Parse(payload).RootElement.GetProperty("error");
        error.GetProperty("code").GetString().Should().Be("server.error");
        error.GetProperty("message").GetString().Should().Be("An unexpected error occurred.");
        error.GetProperty("correlationId").GetString().Should().Be("corr-123");
        logger.Entries.Should().Contain(e => e.Level == LogLevel.Error);
    }

    [Fact]
    public async Task A_client_abort_after_the_response_started_writes_nothing_and_does_not_rethrow()
    {
        // Kestrel throws if you set a status code on a response whose headers already went out, so the
        // 499 is guarded by HasStarted. Without the guard the abort branch would itself throw — out of
        // the exception handler, past Serilog's request log, and back into the Error line this whole
        // carve-out exists to remove. The fake response feature reproduces Kestrel's refusal rather than
        // trusting DefaultHttpContext, whose in-memory feature accepts the write silently.
        var (context, body) = NewContext();
        context.Features.Set<IHttpResponseFeature>(new StartedResponseFeature { Body = body });
        using var abort = new CancellationTokenSource();
        context.RequestAborted = abort.Token;
        await abort.CancelAsync();
        var logger = new ListLogger<ExceptionHandlingMiddleware>();

        var act = async () => await InvokeAsync(context, body, logger, new OperationCanceledException(abort.Token));

        await act.Should().NotThrowAsync();
        context.Response.StatusCode.Should().Be(StatusCodes.Status200OK, "the status was already on the wire");
        logger.Entries.Should().NotContain(e => e.Level >= LogLevel.Error);
    }

    /// <summary>
    /// A response feature that is already on the wire: <see cref="HasStarted"/> is true and, like
    /// Kestrel's, the status-code setter refuses the write instead of accepting it into a field.
    /// </summary>
    private sealed class StartedResponseFeature : IHttpResponseFeature
    {
        public int StatusCode
        {
            get => StatusCodes.Status200OK;
            set => throw new InvalidOperationException(
                "StatusCode cannot be set because the response has already started.");
        }

        public string? ReasonPhrase { get; set; }

        public IHeaderDictionary Headers { get; set; } = new HeaderDictionary();

        public Stream Body { get; set; } = Stream.Null;

        public bool HasStarted => true;

        public void OnStarting(Func<object, Task> callback, object state) { }

        public void OnCompleted(Func<object, Task> callback, object state) { }
    }
}
