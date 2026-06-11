using CompliDrop.Api.Auth;
using CompliDrop.Api.Configuration;
using CompliDrop.Api.Data;
using CompliDrop.Api.Entities;
using CompliDrop.Api.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Npgsql;
using Stripe;

namespace CompliDrop.Api.Endpoints;

public static class BillingEndpoints
{
    public static void MapBillingEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/billing");

        group.MapPost("/checkout", Checkout).RequireAuthorization();
        group.MapPost("/portal", Portal).RequireAuthorization();
        group.MapGet("/subscription", GetSubscription).RequireAuthorization();
        group.MapPost("/webhook", Webhook);
    }

    private static async Task<IResult> Checkout(
        CheckoutRequest req,
        HttpContext http,
        IStripeService stripe,
        IIdempotencyService idem,
        IOptions<FrontendSettings> frontend,
        ICurrentUser currentUser,
        CancellationToken ct)
    {
        if (currentUser.OrganizationId is null) return Unauthorized();

        // Validate the plan FIRST (#147, ADR 0011). The wire vocab is
        // `pro | annual | founding`; the legacy `"monthly"` is rejected.
        // Input validation runs ahead of `IsEnabled` and idempotency so
        // a malformed client always gets a 400 plan_unknown — not a
        // 503 (service unavailable) it can't act on, and not a cached
        // 200 from a stale idempotency-key hit. Unknown plans get a
        // distinct error code from "configured-but-empty" so a deploy
        // missing a Stripe price ID is distinguishable from a
        // malformed client.
        var priceId = req.Plan?.ToLowerInvariant() switch
        {
            "pro" => stripe.MonthlyPriceId,
            "annual" => stripe.AnnualPriceId,
            "founding" => stripe.FoundingPriceId,
            _ => null
        };
        if (priceId is null)
            return Error(400, "billing.plan_unknown", "Unknown plan. Expected one of: pro, annual, founding.");
        if (string.IsNullOrWhiteSpace(priceId))
            return Error(400, "billing.price_missing", "Requested plan has no configured price.");

        if (!stripe.IsEnabled) return Error(503, "billing.unavailable", "Billing is not yet configured.");

        var orgId = currentUser.OrganizationId.Value;
        var idempotencyKey = http.Request.Headers["Idempotency-Key"].FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(idempotencyKey))
        {
            var hit = await idem.TryGetAsync(orgId, idempotencyKey, ct);
            if (hit is not null)
                return Results.Json(
                    hit.ResponseJson is null ? null : System.Text.Json.JsonSerializer.Deserialize<object>(hit.ResponseJson),
                    statusCode: hit.StatusCode);
        }

        var baseUrl = frontend.Value.BaseUrl.TrimEnd('/');
        var url = await stripe.CreateCheckoutSessionAsync(
            orgId,
            priceId,
            $"{baseUrl}/settings?upgraded=true",
            $"{baseUrl}/settings?canceled=true",
            ct);

        var response = new { data = new { sessionUrl = url }, error = (object?)null };
        if (!string.IsNullOrWhiteSpace(idempotencyKey))
            await idem.StoreAsync(orgId, idempotencyKey, http.Request.Path, StatusCodes.Status200OK, response, ct);
        return Results.Ok(response);
    }

    private static async Task<IResult> Portal(
        IStripeService stripe,
        IOptions<FrontendSettings> frontend,
        ICurrentUser currentUser,
        CancellationToken ct)
    {
        if (currentUser.OrganizationId is null) return Unauthorized();
        if (!stripe.IsEnabled) return Error(503, "billing.unavailable", "Billing is not yet configured.");
        var url = await stripe.CreatePortalSessionAsync(
            currentUser.OrganizationId.Value,
            $"{frontend.Value.BaseUrl.TrimEnd('/')}/settings",
            ct);
        return Results.Ok(new { data = new { sessionUrl = url }, error = (object?)null });
    }

    private static async Task<IResult> GetSubscription(
        SystemDbContext db,
        ICurrentUser currentUser,
        CancellationToken ct)
    {
        if (currentUser.OrganizationId is null) return Unauthorized();
        var sub = await db.Subscriptions
            .FirstOrDefaultAsync(s => s.OrganizationId == currentUser.OrganizationId.Value, ct);
        if (sub is null) return Error(404, "billing.not_found", "Subscription not found.");

        var docCount = await db.Documents.CountAsync(d => d.OrganizationId == sub.OrganizationId && d.DeletedAt == null, ct);
        return Results.Ok(new
        {
            data = new
            {
                plan = sub.Plan,
                status = sub.Status,
                documentLimit = sub.DocumentLimit,
                documentsUsed = docCount,
                hasVendorPortal = sub.HasVendorPortal,
                currentPeriodEnd = sub.CurrentPeriodEnd,
                extractionSpend = sub.ExtractionSpendThisMonthUsd
            },
            error = (object?)null
        });
    }

    private static async Task<IResult> Webhook(
        HttpContext http,
        SystemDbContext db,
        IStripeService stripe,
        IOptions<StripeSettings> settings,
        CancellationToken ct)
    {
        using var reader = new StreamReader(http.Request.Body);
        var raw = await reader.ReadToEndAsync(ct);
        var signature = http.Request.Headers["Stripe-Signature"].FirstOrDefault();
        if (string.IsNullOrWhiteSpace(settings.Value.WebhookSecret) || string.IsNullOrWhiteSpace(signature))
            return Results.BadRequest();

        Event ev;
        try
        {
            ev = EventUtility.ConstructEvent(raw, signature, settings.Value.WebhookSecret);
        }
        catch (StripeException)
        {
            return Results.BadRequest();
        }

        if (await db.ProcessedStripeEvents.AnyAsync(p => p.Id == ev.Id, ct)) return Results.Ok();

        // Handle FIRST, mark processed AFTER (#268, ADR 0020). If the handler throws, the
        // event id is never recorded, the response is a 5xx, and Stripe's retry re-runs the
        // handler. Recording before handling turned any transient failure into a permanently
        // dropped event: the retry hit the dedupe check above and got a 200 while the side
        // effects (e.g. flipping a paid checkout's org off the free cap) never ran. Safe
        // because HandleWebhookEventAsync is idempotent per event — the crash window between
        // handler success and the dedupe insert resolves to a benign re-apply on retry.
        await stripe.HandleWebhookEventAsync(ev, ct);

        db.ProcessedStripeEvents.Add(new ProcessedStripeEvent
        {
            Id = ev.Id,
            Type = ev.Type,
            ProcessedAt = DateTime.UtcNow
        });
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            // Concurrent delivery of the same event id: both requests passed the AnyAsync
            // check and ran the (idempotent) handler; the other one recorded the event first.
            // The row existing IS the deduped-success state — absorb the race instead of
            // 500ing Stripe into a spurious extra retry.
        }
        return Results.Ok();
    }

    private static IResult Unauthorized() =>
        Results.Json(new { data = (object?)null, error = new { code = "auth.unauthorized", message = "Not authenticated." } }, statusCode: 401);

    private static IResult Error(int status, string code, string message) =>
        Results.Json(new { data = (object?)null, error = new { code, message } }, statusCode: status);
}

public record CheckoutRequest(string Plan);
