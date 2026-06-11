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

    /// <summary>
    /// Implementations MUST be idempotent per event: the webhook endpoint marks an event
    /// processed only after this returns (#268), so Stripe retries — and the crash window
    /// between handler success and the dedupe insert — re-invoke it with the same event.
    /// Keep handlers as state-upserts (re-applying the same event yields the same row state),
    /// never as increments/appends.
    /// </summary>
    Task HandleWebhookEventAsync(Event ev, CancellationToken ct);

    /// <summary>
    /// Cancels the Stripe subscription immediately (no further invoices). Absorbs the
    /// already-gone cases (resource_missing, or Stripe-side status already canceled /
    /// incomplete_expired) as success — the goal state "no future billing" holds. Any
    /// other failure throws; callers performing irreversible local effects (account
    /// deletion, #255) must abort when this throws.
    /// </summary>
    Task CancelSubscriptionAsync(string stripeSubscriptionId, CancellationToken ct);
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

    // Test seam (#255): lets unit tests drive CancelSubscriptionAsync's error
    // discrimination against a stubbed HTTP transport (StubHttpMessageHandler). When set,
    // the global StripeConfiguration is neither read NOR written on that path, keeping the
    // unit tests parallel-safe. Null in production — the parameterless Stripe service
    // ctors then resolve the global configuration. Same InternalsVisibleTo precedent as
    // ResolvePlanFromPriceId.
    internal IStripeClient? ClientOverride { get; set; }

    private Stripe.SubscriptionService NewSubscriptionService() =>
        ClientOverride is null ? new() : new(ClientOverride);

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

    public async Task CancelSubscriptionAsync(string stripeSubscriptionId, CancellationToken ct)
    {
        EnsureConfigured();
        if (ClientOverride is null) StripeConfiguration.ApiKey = _cfg.SecretKey;

        var svc = NewSubscriptionService();
        try
        {
            await svc.CancelAsync(stripeSubscriptionId, cancellationToken: ct);
            logger.LogInformation("Canceled Stripe subscription {SubscriptionId}", stripeSubscriptionId);
        }
        catch (StripeException ex)
        {
            if (ex.StripeError?.Code == "resource_missing")
            {
                logger.LogInformation("Stripe subscription {SubscriptionId} not found on cancel — treating as already gone", stripeSubscriptionId);
                return;
            }

            // A cancel can also fail because the subscription is ALREADY terminal on
            // Stripe's side while our local row is stale (webhook lag, or a historical
            // lost webhook). Blocking on that would permanently wedge account deletion,
            // so verify the live status and absorb terminal states.
            try
            {
                var current = await svc.GetAsync(stripeSubscriptionId, cancellationToken: ct);
                if (current.Status is "canceled" or "incomplete_expired")
                {
                    logger.LogInformation("Stripe subscription {SubscriptionId} already {Status} on cancel", stripeSubscriptionId, current.Status);
                    return;
                }
            }
            catch (StripeException)
            {
                // Verification itself failed — surface the original cancel error.
            }
            throw;
        }
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

        // Deleted-org door (#255): checkout sessions stay completable for ~24h, so a
        // session minted before account deletion can complete AFTER the org is gone.
        // Never activate a deleted org — cancel the just-created subscription instead
        // so the card never starts billing an account with no login. IgnoreQueryFilters
        // is required (and permitted: system context) to SEE the soft-deleted org.
        var org = await db.Organizations.IgnoreQueryFilters().FirstOrDefaultAsync(o => o.Id == orgId, ct);
        if (org is null || org.DeletedAt is not null)
        {
            if (session.SubscriptionId is { } orphanedSubId)
            {
                if (IsEnabled)
                {
                    await CancelSubscriptionAsync(orphanedSubId, ct);
                    logger.LogWarning(
                        "Checkout completed for deleted org {OrgId}; canceled orphaned Stripe subscription {SubscriptionId}", orgId, orphanedSubId);
                }
                else
                {
                    logger.LogError(
                        "Checkout completed for deleted org {OrgId} but Stripe is not configured — subscription {SubscriptionId} must be canceled manually", orgId, orphanedSubId);
                }
            }
            return;
        }

        var sub = await db.Subscriptions.FirstOrDefaultAsync(s => s.OrganizationId == orgId, ct);
        if (sub is null) return;
        sub.StripeCustomerId = session.CustomerId ?? sub.StripeCustomerId;
        sub.StripeSubscriptionId = session.SubscriptionId ?? sub.StripeSubscriptionId;
        sub.Plan = ResolvePlanFromPriceId(await FetchPriceIdFromSubscription(session.SubscriptionId, ct), _cfg);
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
        sub.Plan = ResolvePlanFromPriceId(s.Items?.Data?.FirstOrDefault()?.Price?.Id, _cfg);
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

    // Maps Stripe-side price ids to app-side plan ids per ADR 0011. The empty-string-
    // collision hardening rationale (why both guards below exist + why StripeSettings
    // stays empty-string-as-sentinel rather than nullable) lives in ADR 0011's Hardening
    // addendum (#172). Returns "pro" on null/empty/whitespace input or any unknown price
    // id. Internal-static + tested directly via InternalsVisibleTo CompliDrop.Api.Tests —
    // same precedent as ComplianceCheckService.EvaluateRule.
    //
    // Duplicate-config priority: if the operator configures the same price id under
    // multiple keys, resolution is first-match-wins in declaration order:
    // Annual > Founding > Monthly. Pinned by the pair
    // StripePriceIdResolverTests.Duplicate_priceId_three_way_collision_resolves_to_annual_first
    // (Annual beats both) +
    // StripePriceIdResolverTests.Duplicate_priceId_for_founding_and_monthly_resolves_to_founding_first
    // (Founding beats Monthly when Annual is unique).
    internal static string ResolvePlanFromPriceId(string? priceId, StripeSettings cfg)
    {
        if (string.IsNullOrWhiteSpace(priceId)) return "pro";

        // Per-key guard: priceId is already non-whitespace by the line above, so this
        // protects the CONFIG side only — an unset cfg.XPriceId (default string.Empty)
        // must not become a wildcard. Both guards are kept as defense-in-depth so a
        // future refactor that drops one still keeps the other layer of protection.
        if (!string.IsNullOrWhiteSpace(cfg.AnnualPriceId) && priceId == cfg.AnnualPriceId) return "annual";
        if (!string.IsNullOrWhiteSpace(cfg.FoundingPriceId) && priceId == cfg.FoundingPriceId) return "founding";
        if (!string.IsNullOrWhiteSpace(cfg.MonthlyPriceId) && priceId == cfg.MonthlyPriceId) return "pro";

        return "pro";
    }

    private void EnsureConfigured()
    {
        if (!IsEnabled) throw new InvalidOperationException("Stripe is not configured.");
    }
}
