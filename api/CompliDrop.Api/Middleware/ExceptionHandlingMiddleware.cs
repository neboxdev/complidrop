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
