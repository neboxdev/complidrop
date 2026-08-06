using System.Text.Json;

namespace CompliDrop.Api.Middleware;

public class ExceptionHandlingMiddleware(
    RequestDelegate next,
    ILogger<ExceptionHandlingMiddleware> logger)
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await next(context);
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            // The CONNECTION is gone: either the client went away mid-request (tab closed, phone
            // dropped signal) or the host force-aborted what was still in flight at the end of a
            // shutdown drain. Kestrel cancels RequestAborted itself in both cases, so both land here —
            // and both have the same answer, because there is nobody left to read one. So: no
            // envelope, and a Debug line rather than Error-with-stack. An Error here is noise the
            // operator can never act on, and once the backend DSN is live it would be a phantom Sentry
            // EVENT per abandoned request (#390). Merge = prod deploy on this project, so
            // deploy-truncated requests are routine — routing them to Error + 500 would mint one such
            // event per deploy, which is precisely the alert fatigue this carve-out exists to prevent.
            //
            // The `when` clause is the whole point, and what it discriminates on is WHOSE TOKEN fired,
            // not why. A cancellation on somebody ELSE's token stays Error + 500: an HttpClient's own
            // timeout surfaces as a TaskCanceledException tied to ITS internal token, never to
            // RequestAborted, and an app-owned linked CTS (PostCommitRegrade's ceiling) is ours too. A
            // bare `catch (OperationCanceledException)` would swallow those. Same discrimination the
            // codebase already makes at BlobStorageService.UploadAsync and AuthEndpoints.DeleteAccount's
            // Stripe-cancel catch. Telling a shutdown abort apart from a client one would take
            // IHostApplicationLifetime.ApplicationStopping — which PostCommitRegrade reaches for
            // exactly when it wants that distinction; this site deliberately does not, because the two
            // differ only in blame and the response is unreadable either way.
            logger.LogDebug(
                "Request aborted before it completed (client gone, or the shutdown drain) {CorrelationId}",
                context.Items["CorrelationId"] as string);

            // 499 ("client closed request") only if nothing has been sent yet. Leaving the default 200
            // would record an abandoned request as a success. The client cannot read it; who does:
            //   • the FRAMEWORK's own request-completion log (Microsoft.AspNetCore.Hosting.Diagnostics,
            //     Information) — it sits OUTSIDE this middleware and reads Response.StatusCode after
            //     the pipeline unwinds, so it is the one in-process line that carries the 499 in prod;
            //   • the edge/proxy access log;
            //   • the integration tests (ClientAbortLoggingTests pins the line above).
            // NOT Serilog's request log: UseSerilogRequestLogging is registered INSIDE this middleware
            // and hard-codes "responded 500" on its exception path, so Program.cs demotes that line to
            // Debug rather than correcting it. The Debug trace above is likewise below the default
            // Information minimum and therefore off in prod — switchable with
            // `Serilog__MinimumLevel__Default=Debug`, no code change. Same shape as ASP.NET Core's own
            // exception handler.
            if (!context.Response.HasStarted)
                context.Response.StatusCode = StatusCodes.Status499ClientClosedRequest;
        }
        catch (Exception ex)
        {
            var correlationId = context.Items["CorrelationId"] as string;
            logger.LogError(ex, "Unhandled exception {CorrelationId}", correlationId);

            if (context.Response.HasStarted) throw;

            context.Response.Clear();
            context.Response.StatusCode = StatusCodes.Status500InternalServerError;
            context.Response.ContentType = "application/json";

            var payload = new
            {
                data = (object?)null,
                error = new
                {
                    code = "server.error",
                    message = "An unexpected error occurred.",
                    correlationId
                }
            };

            await JsonSerializer.SerializeAsync(context.Response.Body, payload, JsonOptions);
        }
    }
}
