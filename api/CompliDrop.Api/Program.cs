using System.Threading.RateLimiting;
using CompliDrop.Api.Auth;
using CompliDrop.Api.BackgroundServices;
using CompliDrop.Api.Configuration;
using CompliDrop.Api.Data;
using CompliDrop.Api.Data.Seed;
using CompliDrop.Api.Endpoints;
using CompliDrop.Api.Middleware;
using CompliDrop.Api.Services;
using CompliDrop.Api.Services.Extraction;
using CompliDrop.Api.Services.Ocr;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Serilog;
using Serilog.Formatting.Json;

var builder = WebApplication.CreateBuilder(args);

// ============================================================
// Configuration — ValidateOnStart so missing secrets fail loud
// ============================================================
builder.Services.AddOptions<JwtSettings>().Bind(builder.Configuration.GetSection("Jwt"))
    .ValidateDataAnnotations().ValidateOnStart();
builder.Services.AddOptions<CookieSettings>().Bind(builder.Configuration.GetSection("Cookies"));
builder.Services.AddOptions<AzureStorageSettings>().Bind(builder.Configuration.GetSection("AzureStorage"));
builder.Services.AddOptions<ExtractionSettings>().Bind(builder.Configuration.GetSection("Extraction"));
builder.Services.AddOptions<GeminiSettings>().Bind(builder.Configuration.GetSection("Gemini"));
builder.Services.AddOptions<AnthropicSettings>().Bind(builder.Configuration.GetSection("Anthropic"));
builder.Services.AddOptions<DocumentAiSettings>().Bind(builder.Configuration.GetSection("DocumentAi"));
builder.Services.AddOptions<StripeSettings>().Bind(builder.Configuration.GetSection("Stripe"));
builder.Services.AddOptions<ResendSettings>().Bind(builder.Configuration.GetSection("Resend"));
builder.Services.AddOptions<CostCeilings>().Bind(builder.Configuration.GetSection("CostCeilings"));
builder.Services.AddOptions<FrontendSettings>().Bind(builder.Configuration.GetSection("Frontend"));

// ============================================================
// Logging — Serilog JSON sink
// ============================================================
builder.Host.UseSerilog((ctx, services, config) =>
{
    config
        .ReadFrom.Configuration(ctx.Configuration)
        .ReadFrom.Services(services)
        .Enrich.FromLogContext()
        .WriteTo.Console(new JsonFormatter(renderMessage: true));
});

// ============================================================
// Sentry — optional, only if DSN present
// ============================================================
var sentryDsn = builder.Configuration["Sentry:Dsn"];
if (!string.IsNullOrWhiteSpace(sentryDsn))
{
    builder.WebHost.UseSentry(opts =>
    {
        opts.Dsn = sentryDsn;
        opts.Environment = builder.Environment.EnvironmentName;
        opts.TracesSampleRate = builder.Configuration.GetValue("Sentry:TracesSampleRate", 0.1);
    });
}

// ============================================================
// Core services
// ============================================================
builder.Services.AddHttpContextAccessor();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddScoped<ICurrentUser, CurrentUserService>();
builder.Services.AddSingleton<IPasswordHasher, BCryptPasswordHasher>();
builder.Services.AddSingleton<ITokenService, TokenService>();
builder.Services.AddSingleton<IBlobStorageService, BlobStorageService>();
builder.Services.AddSingleton<IFileValidationService, FileValidationService>();
builder.Services.AddScoped<IIdempotencyService, IdempotencyService>();
builder.Services.AddScoped<IAuditLogger, AuditLogger>();
builder.Services.AddScoped<ICostTrackingService, CostTrackingService>();
builder.Services.AddScoped<IComplianceCheckService, ComplianceCheckService>();

builder.Services.AddHttpClient("google", c => c.Timeout = TimeSpan.FromMinutes(2));
builder.Services.AddHttpClient("anthropic", c => c.Timeout = TimeSpan.FromMinutes(2));
builder.Services.AddHttpClient("resend", c => c.Timeout = TimeSpan.FromSeconds(30));

builder.Services.AddSingleton<IEmailService, ResendEmailService>();
builder.Services.AddScoped<IStripeService, StripeService>();
builder.Services.AddScoped<IExportService, ExportService>();

builder.Services.AddSingleton<IGoogleAuthTokenProvider, GoogleAuthTokenProvider>();
builder.Services.AddSingleton<IOcrService, DocumentAiOcrService>();
builder.Services.AddSingleton<IExtractionClient, GeminiExtractionClient>();
builder.Services.AddSingleton<IExtractionClient, AnthropicExtractionClient>();
builder.Services.AddSingleton<IExtractionClientFactory, ExtractionClientFactory>();

builder.Services.AddHostedService<ExtractionWorker>();
builder.Services.AddHostedService<ReminderBackgroundService>();

builder.Services.AddCookieJwtAuth();

builder.Services.AddDbContext<AppDbContext>((sp, options) =>
{
    options.UseNpgsql(builder.Configuration.GetConnectionString("Database"));
    options.AddInterceptors(new AuditSaveChangesInterceptor(
        () => sp.GetService<ICurrentUser>()));
});

builder.Services.AddDbContext<SystemDbContext>((sp, options) =>
{
    options.UseNpgsql(builder.Configuration.GetConnectionString("Database"));
    options.AddInterceptors(new AuditSaveChangesInterceptor(
        () => sp.GetService<ICurrentUser>()));
});

// ============================================================
// CORS
// ============================================================
builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
    {
        policy.WithOrigins(
                "https://complidrop.com",
                "https://www.complidrop.com",
                "http://localhost:3000")
            .AllowAnyHeader()
            .AllowAnyMethod()
            .AllowCredentials();
    });
});

// ============================================================
// Rate limiting — policies per §4.12
// ============================================================
builder.Services.AddRateLimiter(opts =>
{
    opts.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

    opts.AddPolicy("auth-strict", ctx => RateLimitPartition.GetFixedWindowLimiter(
        ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 5, Window = TimeSpan.FromMinutes(1) }));

    opts.AddPolicy("waitlist", ctx => RateLimitPartition.GetFixedWindowLimiter(
        ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 10, Window = TimeSpan.FromHours(1) }));

    // Vendor portal upload — TWO independent limits (10/hr per token + 30/hr per ip). Two chained
    // .RequireRateLimiting("portal-token").RequireRateLimiting("portal-ip") calls do NOT work:
    // ASP.NET reads only a single EnableRateLimitingAttribute via GetMetadata<T>() (last wins), so
    // the first policy is silently dropped. We instead register the chained limiter as the global
    // limiter and gate it to portal uploads; all other requests get a no-op partition. Named
    // policies (auth-strict, waitlist, default-authed) on other endpoints still apply additively
    // alongside the global limiter. See ADR 0004.
    opts.GlobalLimiter = PartitionedRateLimiter.CreateChained(
        PartitionedRateLimiter.Create<HttpContext, string>(ctx =>
            IsPortalUpload(ctx)
                ? RateLimitPartition.GetFixedWindowLimiter(
                    "portal-token:" + (ctx.Request.RouteValues["token"]?.ToString() ?? "unknown"),
                    _ => new FixedWindowRateLimiterOptions { PermitLimit = 10, Window = TimeSpan.FromHours(1) })
                : RateLimitPartition.GetNoLimiter("non-portal")),
        PartitionedRateLimiter.Create<HttpContext, string>(ctx =>
            IsPortalUpload(ctx)
                ? RateLimitPartition.GetFixedWindowLimiter(
                    "portal-ip:" + (ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown"),
                    _ => new FixedWindowRateLimiterOptions { PermitLimit = 30, Window = TimeSpan.FromHours(1) })
                : RateLimitPartition.GetNoLimiter("non-portal")));

    opts.AddPolicy("default-authed", ctx =>
    {
        var userId = ctx.User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value
            ?? ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        return RateLimitPartition.GetFixedWindowLimiter(userId,
            _ => new FixedWindowRateLimiterOptions { PermitLimit = 200, Window = TimeSpan.FromMinutes(1) });
    });

    static bool IsPortalUpload(HttpContext ctx) =>
        HttpMethods.IsPost(ctx.Request.Method)
            && ctx.Request.Path.StartsWithSegments("/api/portal")
            && ctx.Request.Path.Value is { } p
            // ASP.NET routing matches MapPost("/{token}/upload") case-insensitively, so the gate
            // must too. An ordinal compare would let `POST /api/portal/{token}/Upload` skip BOTH
            // limits while still reaching the handler.
            && p.EndsWith("/upload", StringComparison.OrdinalIgnoreCase);
});

// ============================================================
// Kestrel
// ============================================================
builder.Services.Configure<Microsoft.AspNetCore.Server.Kestrel.Core.KestrelServerOptions>(opts =>
{
    opts.Limits.MaxRequestBodySize = 10 * 1024 * 1024; // 10 MB
});

builder.Services.Configure<ForwardedHeadersOptions>(opts =>
{
    opts.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
});

// ============================================================
// QuestPDF — license acknowledgment
// ============================================================
QuestPDF.Settings.License = QuestPDF.Infrastructure.LicenseType.Community;

builder.Services.AddOpenApi();

// ============================================================
// Build
// ============================================================
var app = builder.Build();

app.UseForwardedHeaders();
app.UseMiddleware<CorrelationIdMiddleware>();
app.UseMiddleware<ExceptionHandlingMiddleware>();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}
else
{
    app.UseHttpsRedirection();
}

app.UseSerilogRequestLogging();
app.UseRouting();
app.UseCors();
// Gate behind config so integration tests (which have no client IP to partition on) can
// disable it via RateLimiting:Enabled=false. Defaults to on for dev/prod, and the gate is
// *force-on* in non-Development so a config slip can never silently drop auth-strict / portal
// limits in prod. The helper is in a static class to keep it unit-testable.
if (RateLimitingGate.ShouldEnable(app.Environment, app.Configuration, app.Logger))
{
    app.UseRateLimiter();
}
app.UseAuthentication();
app.UseAuthorization();

// ============================================================
// Health endpoints
// ============================================================
app.MapGet("/health/live", () => Results.Ok(new { status = "live", at = DateTime.UtcNow }));

app.MapGet("/health/ready", async (SystemDbContext db, CancellationToken ct) =>
{
    try
    {
        var ok = await db.Database.CanConnectAsync(ct);
        return ok
            ? Results.Ok(new { status = "ready", at = DateTime.UtcNow })
            : Results.StatusCode(503);
    }
    catch (Exception ex)
    {
        return Results.Json(new { status = "not_ready", error = ex.Message }, statusCode: 503);
    }
});

// Legacy health endpoint — kept for UptimeRobot compatibility
app.MapGet("/health", () => Results.Ok(new { status = "healthy", timestamp = DateTime.UtcNow }));

app.MapWaitlistEndpoints();
app.MapAuthEndpoints();
app.MapDocumentEndpoints();
app.MapComplianceEndpoints();
app.MapDashboardEndpoints();
app.MapVendorEndpoints();
app.MapVendorPortalEndpoints();
app.MapReminderEndpoints();
app.MapBillingEndpoints();
app.MapExportEndpoints();

// ============================================================
// Startup: seed system templates
// ============================================================
using (var scope = app.Services.CreateScope())
{
    var env = scope.ServiceProvider.GetRequiredService<IHostEnvironment>();
    var logger = scope.ServiceProvider.GetRequiredService<ILogger<Program>>();
    var sysDb = scope.ServiceProvider.GetRequiredService<SystemDbContext>();

    try
    {
        if (await sysDb.Database.CanConnectAsync())
        {
            await ComplianceTemplateSeed.EnsureAsync(sysDb);
            logger.LogInformation("Seed: system compliance templates ensured.");
        }
        else
        {
            logger.LogWarning("Seed: database not reachable at startup — skipping template seed.");
        }
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "Seed: failed to ensure system compliance templates.");
    }
}

app.Run();

public partial class Program { }
