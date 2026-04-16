using CompliDrop.Api.Configuration;
using CompliDrop.Api.Data;
using CompliDrop.Api.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Stripe;
using Stripe.Checkout;

namespace CompliDrop.Api.Services;

public interface IStripeService
{
    bool IsEnabled { get; }
    Task<string> CreateCheckoutSessionAsync(Guid organizationId, string priceId, string successUrl, string cancelUrl, CancellationToken ct);
    Task<string> CreatePortalSessionAsync(Guid organizationId, string returnUrl, CancellationToken ct);
    Task HandleWebhookEventAsync(Event ev, CancellationToken ct);
    string MonthlyPriceId { get; }
    string AnnualPriceId { get; }
    string FoundingPriceId { get; }
}

public class StripeService(
    SystemDbContext db,
    IOptions<StripeSettings> settings,
    ILogger<StripeService> logger) : IStripeService
{
    private readonly StripeSettings _cfg = settings.Value;

    public bool IsEnabled => !string.IsNullOrWhiteSpace(_cfg.SecretKey);
    public string MonthlyPriceId => _cfg.MonthlyPriceId;
    public string AnnualPriceId => _cfg.AnnualPriceId;
    public string FoundingPriceId => _cfg.FoundingPriceId;

    public async Task<string> CreateCheckoutSessionAsync(
        Guid organizationId,
        string priceId,
        string successUrl,
        string cancelUrl,
        CancellationToken ct)
    {
        EnsureConfigured();
        StripeConfiguration.ApiKey = _cfg.SecretKey;

        var sub = await db.Subscriptions.FirstOrDefaultAsync(s => s.OrganizationId == organizationId, ct)
            ?? throw new InvalidOperationException("Subscription missing for organization.");
        var org = await db.Organizations.FirstAsync(o => o.Id == organizationId, ct);

        string customerId;
        if (!string.IsNullOrWhiteSpace(sub.StripeCustomerId))
        {
            customerId = sub.StripeCustomerId!;
        }
        else
        {
            var customerService = new CustomerService();
            var customer = await customerService.CreateAsync(new CustomerCreateOptions
            {
                Name = org.Name,
                Metadata = new Dictionary<string, string> { ["organization_id"] = organizationId.ToString() }
            }, cancellationToken: ct);
            customerId = customer.Id;
            sub.StripeCustomerId = customerId;
            sub.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);
        }

        var sessionService = new SessionService();
        var session = await sessionService.CreateAsync(new SessionCreateOptions
        {
            Mode = "subscription",
            Customer = customerId,
            LineItems = new List<SessionLineItemOptions>
            {
                new() { Price = priceId, Quantity = 1 }
            },
            SuccessUrl = successUrl,
            CancelUrl = cancelUrl,
            AllowPromotionCodes = true,
            ClientReferenceId = organizationId.ToString()
        }, cancellationToken: ct);

        return session.Url;
    }

    public async Task<string> CreatePortalSessionAsync(Guid organizationId, string returnUrl, CancellationToken ct)
    {
        EnsureConfigured();
        StripeConfiguration.ApiKey = _cfg.SecretKey;

        var sub = await db.Subscriptions.FirstOrDefaultAsync(s => s.OrganizationId == organizationId, ct)
            ?? throw new InvalidOperationException("Subscription missing.");
        if (string.IsNullOrWhiteSpace(sub.StripeCustomerId))
            throw new InvalidOperationException("Organization has no Stripe customer yet — complete a checkout first.");

        var portalService = new Stripe.BillingPortal.SessionService();
        var session = await portalService.CreateAsync(new Stripe.BillingPortal.SessionCreateOptions
        {
            Customer = sub.StripeCustomerId,
            ReturnUrl = returnUrl
        }, cancellationToken: ct);
        return session.Url;
    }

    public async Task HandleWebhookEventAsync(Event ev, CancellationToken ct)
    {
        logger.LogInformation("Stripe webhook {Type} {Id}", ev.Type, ev.Id);
        switch (ev.Type)
        {
            case "checkout.session.completed":
                if (ev.Data.Object is Session session) await ApplyCheckoutCompletedAsync(session, ct);
                break;
            case "customer.subscription.updated":
            case "customer.subscription.created":
                if (ev.Data.Object is Stripe.Subscription created) await ApplySubscriptionStateAsync(created, ct);
                break;
            case "customer.subscription.deleted":
                if (ev.Data.Object is Stripe.Subscription deleted) await ApplySubscriptionDeletedAsync(deleted, ct);
                break;
            case "invoice.payment_failed":
                logger.LogWarning("Stripe invoice payment failed {Id}", ev.Id);
                break;
        }
    }

    private async Task ApplyCheckoutCompletedAsync(Session session, CancellationToken ct)
    {
        if (!Guid.TryParse(session.ClientReferenceId, out var orgId)) return;
        var sub = await db.Subscriptions.FirstOrDefaultAsync(s => s.OrganizationId == orgId, ct);
        if (sub is null) return;
        sub.StripeCustomerId = session.CustomerId ?? sub.StripeCustomerId;
        sub.StripeSubscriptionId = session.SubscriptionId ?? sub.StripeSubscriptionId;
        sub.Plan = ResolvePlanFromPriceId(await FetchPriceIdFromSubscription(session.SubscriptionId, ct));
        sub.Status = "active";
        sub.DocumentLimit = null;
        sub.HasVendorPortal = true;
        sub.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
    }

    private async Task<string?> FetchPriceIdFromSubscription(string? subscriptionId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(subscriptionId)) return null;
        var svc = new Stripe.SubscriptionService();
        var s = await svc.GetAsync(subscriptionId, cancellationToken: ct);
        return s.Items?.Data?.FirstOrDefault()?.Price?.Id;
    }

    private async Task ApplySubscriptionStateAsync(Stripe.Subscription s, CancellationToken ct)
    {
        var sub = await db.Subscriptions.FirstOrDefaultAsync(x => x.StripeSubscriptionId == s.Id, ct);
        if (sub is null) return;
        sub.Status = s.Status;
        sub.Plan = ResolvePlanFromPriceId(s.Items?.Data?.FirstOrDefault()?.Price?.Id);
        sub.CurrentPeriodEnd = s.CurrentPeriodEnd == default(DateTime) ? null : s.CurrentPeriodEnd;
        sub.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
    }

    private async Task ApplySubscriptionDeletedAsync(Stripe.Subscription s, CancellationToken ct)
    {
        var sub = await db.Subscriptions.FirstOrDefaultAsync(x => x.StripeSubscriptionId == s.Id, ct);
        if (sub is null) return;
        sub.Plan = "free";
        sub.Status = "canceled";
        sub.DocumentLimit = 5;
        sub.HasVendorPortal = false;
        sub.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
    }

    private string ResolvePlanFromPriceId(string? priceId)
    {
        if (priceId == _cfg.AnnualPriceId) return "annual";
        if (priceId == _cfg.FoundingPriceId) return "founding";
        if (priceId == _cfg.MonthlyPriceId) return "monthly";
        return "monthly";
    }

    private void EnsureConfigured()
    {
        if (!IsEnabled) throw new InvalidOperationException("Stripe is not configured.");
    }
}
