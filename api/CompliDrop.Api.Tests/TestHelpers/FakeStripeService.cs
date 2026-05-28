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
public sealed class FakeStripeService(IServiceProvider rootProvider) : IStripeService
{
    private readonly ConcurrentQueue<CheckoutCall> _checkouts = new();
    private readonly ConcurrentQueue<PortalCall> _portals = new();
    private int _counter;

    public bool IsEnabled { get; set; } = true;
    public string MonthlyPriceId { get; set; } = "price_monthly_test";
    public string AnnualPriceId { get; set; } = "price_annual_test";
    public string FoundingPriceId { get; set; } = "price_founding_test";

    public IReadOnlyList<CheckoutCall> Checkouts => _checkouts.ToArray();
    public IReadOnlyList<PortalCall> Portals => _portals.ToArray();
    public CheckoutCall? LastCheckout => _checkouts.LastOrDefault();

    /// <summary>Clears captured sessions + restores defaults.</summary>
    public void Reset()
    {
        _checkouts.Clear();
        _portals.Clear();
        Interlocked.Exchange(ref _counter, 0);
        IsEnabled = true;
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

    /// <summary>Delegates to the real <see cref="StripeService"/> so the
    /// webhook path (signature verification handled in the endpoint, plus
    /// the real <c>ResolvePlanFromPriceId</c> boundary) remains
    /// exercised by the existing webhook test suite. Resolved per-call
    /// from a fresh scope because <c>StripeService</c> depends on the
    /// request-scoped <c>SystemDbContext</c> and we want a clean unit
    /// of work for each delegated invocation.</summary>
    public async Task HandleWebhookEventAsync(Event ev, CancellationToken ct)
    {
        using var scope = rootProvider.CreateScope();
        var real = scope.ServiceProvider.GetRequiredService<StripeService>();
        await real.HandleWebhookEventAsync(ev, ct);
    }

    public sealed record CheckoutCall(Guid OrganizationId, string PriceId, string SuccessUrl, string CancelUrl, string SessionUrl);
    public sealed record PortalCall(Guid OrganizationId, string ReturnUrl, string SessionUrl);
}
