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
    /// processed only after this returns (#268, ADR 0020), so Stripe retries — and the crash
    /// window between handler success and the dedupe insert — re-invoke it with the same
    /// event. Keep handlers as state-upserts (re-applying the same event yields the same row
    /// state, except the checkout handler, which deliberately converges on LIVE Stripe truth
    /// at handling time — still a pure upsert), never as increments/appends/one-shot effects.
    /// <para/>
    /// Handlers MUST also be order-resilient (#275, ADR 0023): every subscription-mutating
    /// handler skips events created strictly before <c>Subscription.LastStripeEventAt</c>
    /// (<see cref="StripeService.IsStaleEvent"/>; ties apply) and stamps that fence on every
    /// applied event — at-least-once retries can deliver an event days after newer ones.
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
                if (ev.Data.Object is Session session) await ApplyCheckoutCompletedAsync(session, ev.Created, ct);
                break;
            case "customer.subscription.updated":
            case "customer.subscription.created":
                if (ev.Data.Object is Stripe.Subscription created) await ApplySubscriptionStateAsync(created, ev.Created, ct);
                break;
            case "customer.subscription.deleted":
                if (ev.Data.Object is Stripe.Subscription deleted) await ApplySubscriptionDeletedAsync(deleted, ev.Created, ct);
                break;
            case "invoice.payment_failed":
                logger.LogWarning("Stripe invoice payment failed {Id}", ev.Id);
                break;
        }
    }

    // Order-resilience fence (#275, ADR 0023). At-least-once delivery (ADR 0020) widened
    // the out-of-order window from delivery jitter to Stripe's ~3-day retry schedule: a
    // stale retried event must not overwrite state a newer event already applied. An event
    // is stale when it was created strictly BEFORE the newest applied event. Equal
    // timestamps are NOT stale — Stripe `created` is second-granularity, and ties must
    // re-apply so ADR 0020's crash-window re-delivery stays a benign re-apply and a
    // same-second checkout + subscription.created pair both land. Internal-static + tested
    // directly via InternalsVisibleTo — same precedent as ResolvePlanFromPriceId.
    internal static bool IsStaleEvent(Entities.Subscription sub, DateTime eventCreated) =>
        sub.LastStripeEventAt is { } fence && eventCreated < fence;

    private async Task ApplyCheckoutCompletedAsync(Session session, DateTime eventCreated, CancellationToken ct)
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

        if (IsStaleEvent(sub, eventCreated))
        {
            // A newer event already applied — never let this stale retry overwrite state.
            // But DO backfill identity links the newer events can't supply (#275, ADR 0023):
            // checkout is the only event carrying the org→subscription link
            // (client_reference_id), so skipping it wholesale would leave the row unlinked —
            // every later customer.subscription.* event no-ops (lookup is by
            // StripeSubscriptionId) and billing-portal session creation breaks on a null
            // StripeCustomerId. `??=` only fills nulls: a row already linked to a newer
            // subscription is never clobbered by a stale one.
            var backfilled = false;
            if (sub.StripeCustomerId is null && session.CustomerId is not null)
            {
                sub.StripeCustomerId = session.CustomerId;
                backfilled = true;
            }
            if (sub.StripeSubscriptionId is null && session.SubscriptionId is not null)
            {
                sub.StripeSubscriptionId = session.SubscriptionId;
                backfilled = true;
            }
            if (backfilled)
            {
                sub.UpdatedAt = DateTime.UtcNow;
                await db.SaveChangesAsync(ct);
            }
            logger.LogInformation(
                "Skipped stale checkout.session.completed for org {OrgId} (event created {EventCreated:O} < fence {Fence:O})",
                orgId, eventCreated, sub.LastStripeEventAt);
            return;
        }

        sub.StripeCustomerId = session.CustomerId ?? sub.StripeCustomerId;
        sub.StripeSubscriptionId = session.SubscriptionId ?? sub.StripeSubscriptionId;

        // Entitlements derive from the LIVE subscription, not from "checkout completed ⇒
        // active" (#275, ADR 0023). The fence above cannot see the reviewer's primary
        // sequence — original checkout delivery fails, customer cancels, the deleted event
        // no-ops (row never linked), so no fence exists when the stale checkout retry
        // arrives — only live truth can stop that resurrection. The outbound call was
        // already here for the price id; status + period end now ride along.
        var live = await FetchSubscriptionAsync(session.SubscriptionId, ct);
        if (live is { Status: "canceled" or "incomplete_expired" })
        {
            // The subscription this checkout minted is already terminal on Stripe's side.
            // Record identity (billing-portal access to invoice history keeps working) but
            // land on the same free-tier state ApplySubscriptionDeletedAsync would have left.
            sub.Plan = "free";
            sub.Status = "canceled";
            sub.DocumentLimit = 5;
            sub.HasVendorPortal = false;
            // Fence past the Stripe-side death, not at this (possibly days-old) checkout's
            // own `created`: the live truth applied here is as-of the subscription's end, so
            // pending retries of pre-cancel events (subscription.created/updated "active",
            // created after this checkout but before the cancel) must read as stale —
            // otherwise they re-link active/paid onto the dead subscription and re-create
            // the exact #275 resurrection through the side door.
            var terminalAt = live.EndedAt ?? live.CanceledAt ?? eventCreated;
            sub.LastStripeEventAt = terminalAt > eventCreated ? terminalAt : eventCreated;
            logger.LogWarning(
                "checkout.session.completed for org {OrgId} references subscription {SubscriptionId} already {Status} on Stripe — recorded identity, kept free tier",
                orgId, session.SubscriptionId, live.Status);
        }
        else
        {
            sub.Plan = ResolvePlanFromPriceId(live?.Items?.Data?.FirstOrDefault()?.Price?.Id, _cfg);
            sub.Status = live?.Status ?? "active";
            sub.DocumentLimit = null;
            sub.HasVendorPortal = true;
            // Stripe.net deserializes ABSENT date fields to the Unix epoch (the property
            // default), never to default(DateTime) — epoch-or-earlier means "not supplied".
            if (live is not null && live.CurrentPeriodEnd > DateTime.UnixEpoch)
                sub.CurrentPeriodEnd = live.CurrentPeriodEnd;
            sub.LastStripeEventAt = eventCreated;
        }
        sub.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
    }

    // Live snapshot for the checkout handler. Routed through NewSubscriptionService so the
    // ClientOverride seam covers it, and sets the API key itself: HandleWebhookEventAsync
    // never touched the global StripeConfiguration, so on a fresh process whose FIRST
    // Stripe operation was a checkout webhook this fetch threw "No API key provided" and
    // 5xx-looped until some other code path set the key (latent bug found in #275).
    // Unconfigured Stripe (IsEnabled false — test harness, dev without keys) skips the
    // fetch instead of throwing; callers treat null as "no live truth available".
    private async Task<Stripe.Subscription?> FetchSubscriptionAsync(string? subscriptionId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(subscriptionId) || !IsEnabled) return null;
        if (ClientOverride is null) StripeConfiguration.ApiKey = _cfg.SecretKey;
        var svc = NewSubscriptionService();
        return await svc.GetAsync(subscriptionId, cancellationToken: ct);
    }

    private async Task ApplySubscriptionStateAsync(Stripe.Subscription s, DateTime eventCreated, CancellationToken ct)
    {
        var sub = await db.Subscriptions.FirstOrDefaultAsync(x => x.StripeSubscriptionId == s.Id, ct);
        if (sub is null) return;
        if (IsStaleEvent(sub, eventCreated))
        {
            logger.LogInformation(
                "Skipped stale customer.subscription state event for subscription {SubscriptionId} (event created {EventCreated:O} < fence {Fence:O})",
                s.Id, eventCreated, sub.LastStripeEventAt);
            return;
        }
        sub.Status = s.Status;
        sub.Plan = ResolvePlanFromPriceId(s.Items?.Data?.FirstOrDefault()?.Price?.Id, _cfg);
        // Epoch-or-earlier = field absent from the payload (Stripe.net deserializes missing
        // dates to the Unix epoch, never default(DateTime) — the old `== default` guard was
        // dead code and silently wrote 1970-01-01 for absent fields).
        sub.CurrentPeriodEnd = s.CurrentPeriodEnd > DateTime.UnixEpoch ? s.CurrentPeriodEnd : null;
        // #323: an active sub set to cancel at period end stays Status="active" until the period end, so
        // surface the flag for the "Ends on" vs "Renews on" billing copy. Re-enabling the sub clears it.
        sub.CancelAtPeriodEnd = s.CancelAtPeriodEnd;
        sub.LastStripeEventAt = eventCreated;
        sub.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
    }

    private async Task ApplySubscriptionDeletedAsync(Stripe.Subscription s, DateTime eventCreated, CancellationToken ct)
    {
        var sub = await db.Subscriptions.FirstOrDefaultAsync(x => x.StripeSubscriptionId == s.Id, ct);
        if (sub is null) return;
        if (IsStaleEvent(sub, eventCreated))
        {
            logger.LogInformation(
                "Skipped stale customer.subscription.deleted for subscription {SubscriptionId} (event created {EventCreated:O} < fence {Fence:O})",
                s.Id, eventCreated, sub.LastStripeEventAt);
            return;
        }
        sub.Plan = "free";
        sub.Status = "canceled";
        sub.DocumentLimit = 5;
        sub.HasVendorPortal = false;
        sub.LastStripeEventAt = eventCreated;
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
