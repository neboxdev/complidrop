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
            // The CLIENT went away mid-request (tab closed, phone dropped signal) — not a fault of
            // ours, and there is nobody left to answer. So: no envelope, and a Debug line rather than
            // Error-with-stack. An Error here is noise the operator can never act on, and once the
            // backend DSN is live it would be a phantom Sentry EVENT per abandoned request (#390).
            //
            // The `when` clause is the whole point. A bare `catch (OperationCanceledException)` would
            // also swallow the cancellations that ARE real failures — an HttpClient's own timeout
            // surfaces as a TaskCanceledException tied to ITS internal token, not to RequestAborted,
            // and a shutdown token is a genuine abort of work in flight. Those must stay Error + 500.
            // Same discrimination the codebase already makes at BlobStorageService.UploadAsync and
            // AuthEndpoints.DeleteAccount's Stripe-cancel catch.
            logger.LogDebug(
                "Request aborted by the client {CorrelationId}", context.Items["CorrelationId"] as string);

            // 499 ("client closed request") only if nothing has been sent yet. The client cannot read
            // it — the request LOG can, and leaving the default 200 would record an abandoned request
            // as a success. Same shape as ASP.NET Core's own exception handler.
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
