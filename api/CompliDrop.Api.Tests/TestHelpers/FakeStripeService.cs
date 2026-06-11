using System.Collections.Concurrent;
using CompliDrop.Api.Services;
using Microsoft.Extensions.DependencyInjection;
using Stripe;

namespace CompliDrop.Api.Tests.TestHelpers;

/// <summary>
/// In-memory <see cref="IStripeService"/> for tests — lets the
/// <c>/api/billing/checkout</c> tests exercise the success path
/// (200 + sessionUrl + captured priceId) without hitting the live
/// Stripe API.
///
/// Decorator over the real <see cref="StripeService"/> for the
/// webhook path: <see cref="HandleWebhookEventAsync"/> DELEGATES to
/// the real implementation (resolved per-call from a fresh DI scope,
/// because the real service depends on the request-scoped
/// <c>SystemDbContext</c>). The <see cref="StripeWebhookTests"/>
/// suite therefore continues to exercise the genuine price-id →
/// plan-id resolution (the ADR 0011 boundary). Checkout / portal /
/// IsEnabled / price-id getters are stubbed locally so the checkout
/// tests get deterministic behaviour with no network.
///
/// Registered as a Singleton in <see cref="CustomWebApplicationFactory"/>
/// so the captured-calls queue persists across requests within one
/// test, then <see cref="Reset"/>-ed between tests.
///
/// Default behaviour:
///   - <see cref="IsEnabled"/> = true so the <c>!stripe.IsEnabled</c>
///     gate in <c>BillingEndpoints</c> does NOT short-circuit. Toggle
///     to false in a per-test scope to exercise the 503 branch.
///   - <see cref="MonthlyPriceId"/> / <see cref="AnnualPriceId"/> /
///     <see cref="FoundingPriceId"/> mirror the test-config price ids
///     installed by <c>CustomWebApplicationFactory</c>.
///   - <see cref="CreateCheckoutSessionAsync"/> returns a deterministic
///     stub URL like <c>https://checkout.stripe.test/cs_test_000001</c>
///     and captures every invocation in <see cref="Checkouts"/>.
///   - <see cref="CreatePortalSessionAsync"/> mirrors the shape.
/// </summary>
public sealed class FakeStripeService(IServiceScopeFactory scopeFactory) : IStripeService
{
    // Default price-id literals — shared between the property
    // initializers and Reset() so the construction-time defaults stay
    // in one place. Mirrors the test-config values installed by
    // CustomWebApplicationFactory.
    private const string DefaultMonthlyPriceId = "price_monthly_test";
    private const string DefaultAnnualPriceId = "price_annual_test";
    private const string DefaultFoundingPriceId = "price_founding_test";

    private readonly ConcurrentQueue<CheckoutCall> _checkouts = new();
    private readonly ConcurrentQueue<PortalCall> _portals = new();
    private readonly ConcurrentQueue<string> _canceledSubscriptions = new();
    private int _counter;

    public bool IsEnabled { get; set; } = true;
    public string MonthlyPriceId { get; set; } = DefaultMonthlyPriceId;
    public string AnnualPriceId { get; set; } = DefaultAnnualPriceId;
    public string FoundingPriceId { get; set; } = DefaultFoundingPriceId;

    /// <summary>When true, the next <see cref="HandleWebhookEventAsync"/> call throws
    /// (simulating a transient handler failure — a DB blip or the outbound Stripe call
    /// inside the checkout handler failing) and the flag auto-clears, so the following
    /// call succeeds. Mirrors Stripe's deliver → fail → retry sequence (#268).
    /// <see cref="Reset"/> also clears it.</summary>
    public bool FailNextWebhookHandling { get; set; }

    public IReadOnlyList<CheckoutCall> Checkouts => _checkouts.ToArray();
    public IReadOnlyList<PortalCall> Portals => _portals.ToArray();
    public CheckoutCall? LastCheckout => _checkouts.LastOrDefault();
    public IReadOnlyList<string> CanceledSubscriptions => _canceledSubscriptions.ToArray();

    /// <summary>When set, the next <see cref="CancelSubscriptionAsync"/> call throws this
    /// exception (simulating a transient Stripe API failure — set a StripeException for an
    /// API error, a TaskCanceledException for the SDK's own HTTP timeout) and the knob
    /// auto-clears so the following call succeeds — mirrors a user retrying account
    /// deletion (#255). <see cref="Reset"/> also clears it.</summary>
    public Exception? FailNextCancelSubscriptionWith { get; set; }

    /// <summary>Clears captured sessions + restores defaults. A test
    /// that mutated any public property — IsEnabled, MonthlyPriceId,
    /// AnnualPriceId, FoundingPriceId — has its change reverted here
    /// so the next test in the same fixture sees the construction-
    /// time state.</summary>
    public void Reset()
    {
        _checkouts.Clear();
        _portals.Clear();
        _canceledSubscriptions.Clear();
        Interlocked.Exchange(ref _counter, 0);
        IsEnabled = true;
        MonthlyPriceId = DefaultMonthlyPriceId;
        AnnualPriceId = DefaultAnnualPriceId;
        FoundingPriceId = DefaultFoundingPriceId;
        FailNextWebhookHandling = false;
        FailNextCancelSubscriptionWith = null;
    }

    public Task<string> CreateCheckoutSessionAsync(
        Guid organizationId,
        string priceId,
        string successUrl,
        string cancelUrl,
        CancellationToken ct)
    {
        var sessionId = $"cs_test_{Interlocked.Increment(ref _counter):D6}";
        var url = $"https://checkout.stripe.test/{sessionId}";
        _checkouts.Enqueue(new CheckoutCall(organizationId, priceId, successUrl, cancelUrl, url));
        return Task.FromResult(url);
    }

    public Task<string> CreatePortalSessionAsync(Guid organizationId, string returnUrl, CancellationToken ct)
    {
        var sessionId = $"bps_test_{Interlocked.Increment(ref _counter):D6}";
        var url = $"https://billing.stripe.test/{sessionId}";
        _portals.Enqueue(new PortalCall(organizationId, returnUrl, url));
        return Task.FromResult(url);
    }

    public Task CancelSubscriptionAsync(string stripeSubscriptionId, CancellationToken ct)
    {
        if (FailNextCancelSubscriptionWith is { } failure)
        {
            FailNextCancelSubscriptionWith = null;
            throw failure;
        }
        _canceledSubscriptions.Enqueue(stripeSubscriptionId);
        return Task.CompletedTask;
    }

    /// <summary>Delegates to the real <see cref="StripeService"/> so the
    /// webhook path (signature verification handled in the endpoint, plus
    /// the real <c>ResolvePlanFromPriceId</c> boundary) remains
    /// exercised by the existing webhook test suite. Resolved per-call
    /// from a fresh scope because <c>StripeService</c> depends on the
    /// request-scoped <c>SystemDbContext</c> and we want a clean unit
    /// of work for each delegated invocation.
    ///
    /// Injects <see cref="IServiceScopeFactory"/> rather than the raw
    /// <c>IServiceProvider</c> — matches the codebase convention from
    /// <c>ExtractionWorker</c> and <c>ReminderBackgroundService</c>
    /// (the only other Singleton-scope creators) and makes the "I
    /// create scopes" intent visible at the type level.</summary>
    public async Task HandleWebhookEventAsync(Event ev, CancellationToken ct)
    {
        if (FailNextWebhookHandling)
        {
            FailNextWebhookHandling = false;
            throw new InvalidOperationException(
                "Simulated transient webhook handler failure (FakeStripeService.FailNextWebhookHandling).");
        }

        using var scope = scopeFactory.CreateScope();
        var real = scope.ServiceProvider.GetRequiredService<StripeService>();
        await real.HandleWebhookEventAsync(ev, ct);
    }

    public sealed record CheckoutCall(Guid OrganizationId, string PriceId, string SuccessUrl, string CancelUrl, string SessionUrl);
    public sealed record PortalCall(Guid OrganizationId, string ReturnUrl, string SessionUrl);
}
