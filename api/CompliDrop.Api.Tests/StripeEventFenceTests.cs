using CompliDrop.Api.Entities;
using CompliDrop.Api.Services;
using FluentAssertions;

namespace CompliDrop.Api.Tests;

/// <summary>
/// Boundary table for <see cref="StripeService.IsStaleEvent"/> (#275, ADR 0023) — the
/// order-resilience fence. Strictly-older is stale; ties are NOT (Stripe `created` is
/// second-granularity, and equal-timestamp re-application is what keeps ADR 0020's
/// crash-window re-apply benign). Tested directly via InternalsVisibleTo — same precedent
/// as <c>StripePriceIdResolverTests</c>.
/// </summary>
public sealed class StripeEventFenceTests
{
    private static readonly DateTime Fence = new(2026, 6, 12, 8, 0, 0, DateTimeKind.Utc);

    private static Subscription SubWithFence(DateTime? fence) => new() { LastStripeEventAt = fence };

    [Fact]
    public void Null_fence_is_open_so_nothing_is_stale()
    {
        StripeService.IsStaleEvent(SubWithFence(null), DateTime.MinValue).Should().BeFalse(
            "pre-#275 rows and orgs that never received a webhook must apply their first event");
    }

    [Fact]
    public void Event_created_strictly_before_the_fence_is_stale()
    {
        StripeService.IsStaleEvent(SubWithFence(Fence), Fence.AddSeconds(-1)).Should().BeTrue();
        StripeService.IsStaleEvent(SubWithFence(Fence), Fence.AddDays(-3)).Should().BeTrue(
            "Stripe's full retry horizon is the window the fence exists to close");
    }

    [Fact]
    public void Event_created_exactly_at_the_fence_is_not_stale()
    {
        StripeService.IsStaleEvent(SubWithFence(Fence), Fence).Should().BeFalse(
            "ties must re-apply: crash-window re-delivery (ADR 0020) and same-second pairs share a `created`");
    }

    [Fact]
    public void Event_created_after_the_fence_is_not_stale()
    {
        StripeService.IsStaleEvent(SubWithFence(Fence), Fence.AddSeconds(1)).Should().BeFalse();
    }
}
