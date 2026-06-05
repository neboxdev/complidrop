using System.Text.Json;
using System.Threading.RateLimiting;
using CompliDrop.Api;
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
builder.Services.AddSingleton<IImageTranscoder, MagickImageTranscoder>();
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

    // Shared serializer options for the OnRejected envelope — matches
    // ExceptionHandlingMiddleware's camelCase contract (see comment
    // block below for the rationale).
    var rateLimitEnvelopeJsonOptions = new JsonSerializerOptions
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    // Write an explicit error envelope on a rate-limit rejection so
    // the response is distinguishable from a quota-exceeded 429 (which
    // the endpoint itself returns with `vendor.portal_quota_exceeded`
    // and a `Upload quota reached for this link.` message). Without
    // this hook ASP.NET emits an empty 429 body — clients have to
    // guess by body-shape whether to retry-next-hour (transient rate
    // limit) or never-retry (link permanently exhausted). See #45 and
    // ADR 0004.
    //
    // The envelope is built via JsonSerializer + an anonymous object
    // matching ExceptionHandlingMiddleware's exact shape (#45 followup
    // review): same `data: null`, same `error: { code, message,
    // correlationId }`, same camelCase JsonNamingPolicy. A hand-rolled
    // JSON literal would diverge silently if a future contributor
    // added a field to the canonical envelope (the frontend ApiEnvelope
    // type at frontend/src/lib/api.ts already expects `correlationId`
    // on errors). HasStarted guard mirrors ExceptionHandlingMiddleware
    // so a future pipeline change that started the response early
    // surfaces as a clean no-op rather than an InvalidOperationException
    // inside the limiter.
    //
    // `rate_limit.exceeded` is the only code emitted here for ALL
    // policies (portal-token, portal-ip, auth-strict, waitlist,
    // default-authed). The policy distinction is internal accounting
    // and not actionable for the client; the universal code is the
    // contract every limited endpoint surface presents — pinned by
    // the discriminator + cross-policy tests in
    // VendorPortalEndpointsTests + AuthEndpointsTests.
    opts.OnRejected = async (ctx, ct) =>
    {
        if (ctx.HttpContext.Response.HasStarted) return;

        var correlationId = ctx.HttpContext.Items["CorrelationId"] as string;
        ctx.HttpContext.Response.StatusCode = StatusCodes.Status429TooManyRequests;
        ctx.HttpContext.Response.ContentType = "application/json";

        var payload = new
        {
            data = (object?)null,
            error = new
            {
                code = "rate_limit.exceeded",
                message = "Too many requests. Please try again later.",
                correlationId
            }
        };

        await JsonSerializer.SerializeAsync(
            ctx.HttpContext.Response.Body,
            payload,
            rateLimitEnvelopeJsonOptions,
            ct);
    };

    opts.AddPolicy("auth-strict", ctx => RateLimitPartition.GetFixedWindowLimiter(
        ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 5, Window = TimeSpan.FromMinutes(1) }));

    // POST /api/auth/refresh is routine session-keepalive: the SPA calls it on
    // every 401 and whenever the 15-min cd_session expires. Keeping it on the
    // 5/min `auth-strict` (login/register brute-force) bucket meant NORMAL use
    // tripped the limiter — and because the per-IP partition COLLAPSES to a
    // single bucket behind Railway's proxy (every request shares the proxy IP
    // until ForwardedHeaders trusts it — see the ForwardedHeaders config
    // below), a handful of users' refreshes 429'd each other and the SPA logged
    // everyone out ("Too many requests" after 30s + constant logouts). Partition
    // on the cd_refresh COOKIE (hashed — never store raw token material as a
    // dictionary key) so each session gets its own generous bucket, independent
    // of IP/proxy. Requests with no refresh cookie 401 cheaply, so they share
    // one "anon" bucket. 60/min comfortably covers retry/burst while still
    // bounding a stolen-cookie replay.
    opts.AddPolicy("auth-refresh", ctx =>
    {
        var key = ctx.Request.Cookies.TryGetValue(CookieAuthSetup.RefreshCookie, out var rc)
            && !string.IsNullOrWhiteSpace(rc)
                ? HashPartitionKey(rc)
                : "anon";
        return RateLimitPartition.GetFixedWindowLimiter(key,
            _ => new FixedWindowRateLimiterOptions { PermitLimit = 60, Window = TimeSpan.FromMinutes(1) });
    });

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

    // Hash the refresh cookie before using it as a rate-limit partition key so
    // raw token material never lands in the limiter's in-memory key set (which
    // can surface in dumps / diagnostics). SHA-256 hex is a stable, bounded key.
    static string HashPartitionKey(string value) =>
        Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(value)));

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
    // Railway terminates TLS at its edge proxy and appends the real client IP
    // to X-Forwarded-For. ASP.NET only trusts XFF from loopback by default, so
    // behind Railway `Connection.RemoteIpAddress` stays the proxy's IP — which
    // collapses every per-IP rate-limit partition (auth-strict, portal-ip) into
    // ONE global bucket shared by all users. We can't enumerate Railway's proxy
    // IPs, so clear the known-proxy allowlist (this disables the loopback-only
    // trust check) and cap ForwardLimit at 1 so exactly ONE hop is honored —
    // the IP Railway appended — never a client-spoofed XFF entry to its left.
    // POST-DEPLOY CHECK: confirm RemoteIpAddress resolves to real client IPs on
    // Railway (Serilog request logs echo it); if Railway ever adds >1 forwarding
    // hop, bump ForwardLimit to match the hop count.
    opts.KnownNetworks.Clear();
    opts.KnownProxies.Clear();
    opts.ForwardLimit = 1;
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
// Startup: apply EF migrations, then seed system templates
// ============================================================
using (var scope = app.Services.CreateScope())
{
    var logger = scope.ServiceProvider.GetRequiredService<ILogger<Program>>();

    // Schema first: bring the database to the assembly's migration set (or fail-fast on drift)
    // BEFORE anything queries it. Migrations belong to AppDbContext (generated with
    // --context AppDbContext). This is deliberately NOT wrapped in a swallowing try/catch — a
    // schema that can't be brought current must abort boot rather than serve 500s on every query
    // that touches a missing column (#226: a deploy left prod 9 migrations behind and every Users
    // SELECT threw 42703, 500'ing login). MigrateAsync is pure DDL, so the tenant query filter and
    // audit interceptor on AppDbContext never engage here.
    var appDb = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    await DatabaseMigrator.MigrateAndGuardAsync(
        appDb.Database,
        DatabaseMigrator.ShouldAutoMigrate(app.Configuration),
        logger);

    // Seed: best-effort system compliance templates, after the schema is guaranteed current.
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
