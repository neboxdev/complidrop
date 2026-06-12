using System.Net;
using CompliDrop.Api.Configuration;
using CompliDrop.Api.Entities;
using CompliDrop.Api.Services;
using CompliDrop.Api.Tests.TestHelpers;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Stripe;
using Stripe.Checkout;
using Subscription = CompliDrop.Api.Entities.Subscription;

namespace CompliDrop.Api.Tests;

/// <summary>
/// Pins the checkout handler's live-subscription branch (#275, ADR 0023): entitlements
/// derive from the subscription state Stripe reports at handling time, not from
/// "checkout completed ⇒ active". Drives the real <see cref="StripeService"/> directly
/// (the webhook suite's payload-only path can't reach the outbound fetch) with the Stripe
/// SDK pointed at <see cref="StubHttpMessageHandler"/> via the internal
/// <c>ClientOverride</c> seam — same convention as <c>StripeServiceCancelTests</c> — and a
/// real Testcontainers database for the subscription row.
/// </summary>
public sealed class StripeServiceCheckoutLiveStateTests(IntegrationTestFixture fixture) : IntegrationTestBase(fixture)
{
    private const string LiveSubId = "sub_live_0001";
    private const string LiveCustomerId = "cus_live_0001";

    private static StripeService NewService(CompliDrop.Api.Data.SystemDbContext db, StubHttpMessageHandler stub, string secretKey = "sk_test_unit") =>
        new(db,
            Options.Create(new StripeSettings
            {
                SecretKey = secretKey,
                MonthlyPriceId = "price_monthly_test",
                AnnualPriceId = "price_annual_test",
                FoundingPriceId = "price_founding_test",
            }),
            NullLogger<StripeService>.Instance)
        {
            ClientOverride = new StripeClient(
                "sk_test_unit",
                httpClient: new SystemNetHttpClient(new HttpClient(stub))),
        };

    private async Task<Guid> SeedFreeOrgAsync()
    {
        var orgId = Guid.NewGuid();
        var now = DateTime.UtcNow;
        await using var db = CreateSystemDb();
        db.Organizations.Add(new Organization { Id = orgId, Name = $"Org-{orgId:N}", CreatedAt = now, UpdatedAt = now });
        db.Subscriptions.Add(new Subscription
        {
            Id = Guid.NewGuid(),
            OrganizationId = orgId,
            Plan = "free",
            Status = "active",
            DocumentLimit = 5,
            HasVendorPortal = false,
            CreatedAt = now,
            UpdatedAt = now,
        });
        await db.SaveChangesAsync();
        return orgId;
    }

    private static Event CheckoutEvent(Guid orgId, DateTime created, string? subscriptionId = LiveSubId) => new()
    {
        Id = $"evt_{Guid.NewGuid():N}",
        Type = "checkout.session.completed",
        Created = created,
        Data = new EventData
        {
            Object = new Session
            {
                Id = $"cs_{Guid.NewGuid():N}",
                ClientReferenceId = orgId.ToString(),
                CustomerId = LiveCustomerId,
                SubscriptionId = subscriptionId,
            },
        },
    };

    private static string LiveSubscriptionJson(string status, string priceId = "price_annual_test", long? currentPeriodEnd = null, long? endedAt = null) =>
        $$"""
        {
          "id": "{{LiveSubId}}",
          "object": "subscription",
          "status": "{{status}}",
          {{(currentPeriodEnd is { } cpe ? $"\"current_period_end\": {cpe}," : "")}}
          {{(endedAt is { } ea ? $"\"ended_at\": {ea}," : "")}}
          "items": {
            "object": "list",
            "data": [
              { "id": "si_1", "object": "subscription_item", "price": { "id": "{{priceId}}", "object": "price" } }
            ]
          }
        }
        """;

    private static Event SubscriptionUpdatedEvent(DateTime created, string status, string priceId = "price_annual_test") => new()
    {
        Id = $"evt_{Guid.NewGuid():N}",
        Type = "customer.subscription.updated",
        Created = created,
        Data = new EventData
        {
            Object = new Stripe.Subscription
            {
                Id = LiveSubId,
                Status = status,
                Items = new StripeList<SubscriptionItem>
                {
                    Data = [new SubscriptionItem { Id = "si_1", Price = new Price { Id = priceId } }],
                },
            },
        },
    };

    private async Task<Subscription> ReloadAsync(Guid orgId)
    {
        await using var db = CreateSystemDb();
        return await db.Subscriptions.FirstAsync(s => s.OrganizationId == orgId);
    }

    [Fact]
    public async Task Checkout_grants_paid_state_from_the_live_subscription()
    {
        var orgId = await SeedFreeOrgAsync();
        var created = DateTime.UtcNow.AddSeconds(-1);
        created = new DateTime(created.Year, created.Month, created.Day, created.Hour, created.Minute, created.Second, DateTimeKind.Utc);
        var periodEnd = DateTimeOffset.UtcNow.AddMonths(12).ToUnixTimeSeconds();
        var stub = new StubHttpMessageHandler(HttpStatusCode.OK, LiveSubscriptionJson("active", currentPeriodEnd: periodEnd));

        await using var db = CreateSystemDb();
        await NewService(db, stub).HandleWebhookEventAsync(CheckoutEvent(orgId, created), CancellationToken.None);

        stub.CallCount.Should().Be(1);
        stub.LastRequest!.RequestUri!.AbsolutePath.Should().EndWith($"/v1/subscriptions/{LiveSubId}");
        var sub = await ReloadAsync(orgId);
        sub.Plan.Should().Be("annual", "the plan derives from the live price id");
        sub.Status.Should().Be("active");
        sub.DocumentLimit.Should().BeNull();
        sub.HasVendorPortal.Should().BeTrue();
        sub.StripeCustomerId.Should().Be(LiveCustomerId);
        sub.StripeSubscriptionId.Should().Be(LiveSubId);
        sub.CurrentPeriodEnd.Should().Be(DateTimeOffset.FromUnixTimeSeconds(periodEnd).UtcDateTime,
            "checkout now records the period end so a fence-skipped same-second subscription.created loses nothing");
        sub.LastStripeEventAt.Should().Be(created, "an applied event stamps the fence");
    }

    [Fact]
    public async Task Checkout_for_a_subscription_already_terminal_on_stripe_keeps_free_tier_but_records_identity()
    {
        // The #275 reviewer's primary sequence, closed by live truth: the original checkout
        // delivery failed (row never linked), the customer canceled (deleted no-oped on the
        // unlinked row — there is NO fence), and Stripe retries the old checkout days later.
        // The live fetch is the only thing that can stop the paid-state resurrection.
        var orgId = await SeedFreeOrgAsync();
        var stub = new StubHttpMessageHandler(HttpStatusCode.OK, LiveSubscriptionJson("canceled"));

        await using var db = CreateSystemDb();
        await NewService(db, stub).HandleWebhookEventAsync(CheckoutEvent(orgId, DateTime.UtcNow), CancellationToken.None);

        var sub = await ReloadAsync(orgId);
        sub.Plan.Should().Be("free", "a checkout whose subscription is already canceled on Stripe must not grant paid state");
        sub.Status.Should().Be("canceled");
        sub.DocumentLimit.Should().Be(5);
        sub.HasVendorPortal.Should().BeFalse();
        sub.StripeCustomerId.Should().Be(LiveCustomerId, "identity is recorded so billing-portal access to invoice history works");
        sub.StripeSubscriptionId.Should().Be(LiveSubId);
        sub.LastStripeEventAt.Should().NotBeNull("the terminal-state application still moves the fence");
    }

    [Fact]
    public async Task Terminal_checkout_stamps_the_fence_at_the_live_ended_at_not_the_stale_checkout_created()
    {
        // Review finding on #275 (security + correctness, converged): stamping the fence at
        // the stale checkout's own `created` left every event created between the checkout
        // and the Stripe-side cancel un-fenced — a pending retried subscription.updated
        // ("active", pre-cancel) would re-apply onto the now-linked row and resurrect an
        // active/paid status on a dead subscription, wedging the 409 already-subscribed
        // guard against any re-checkout. The fence must land at the subscription's death.
        var orgId = await SeedFreeOrgAsync();
        var checkoutCreated = DateTime.UtcNow.AddDays(-2);
        checkoutCreated = new DateTime(checkoutCreated.Ticks - checkoutCreated.Ticks % TimeSpan.TicksPerSecond, DateTimeKind.Utc);
        var endedAt = checkoutCreated.AddHours(6);
        var stub = new StubHttpMessageHandler(HttpStatusCode.OK,
            LiveSubscriptionJson("canceled", endedAt: new DateTimeOffset(endedAt).ToUnixTimeSeconds()));

        await using var db = CreateSystemDb();
        await NewService(db, stub).HandleWebhookEventAsync(CheckoutEvent(orgId, checkoutCreated), CancellationToken.None);

        var sub = await ReloadAsync(orgId);
        sub.LastStripeEventAt.Should().Be(endedAt,
            "the fence is the as-of moment of the applied live truth — the subscription's end, not the checkout's created");
    }

    [Fact]
    public async Task Pre_cancel_update_retry_is_fenced_out_after_a_terminal_checkout_applied()
    {
        // The full residual sequence, end-to-end at the service level: checkout (T0) and
        // subscription.updated(active, T0+5min) both fail delivery during an outage; the
        // customer cancels (Stripe-side ended_at = T0+6h); deleted no-ops on the unlinked
        // row and is recorded; the checkout retry lands first → terminal branch applies
        // free tier, links the row, fences at ended_at; the updated(active) retry — created
        // BEFORE the cancel — must now read as stale instead of resurrecting active/paid.
        var orgId = await SeedFreeOrgAsync();
        var checkoutCreated = DateTime.UtcNow.AddDays(-2);
        checkoutCreated = new DateTime(checkoutCreated.Ticks - checkoutCreated.Ticks % TimeSpan.TicksPerSecond, DateTimeKind.Utc);
        var endedAt = checkoutCreated.AddHours(6);
        var stub = new StubHttpMessageHandler(HttpStatusCode.OK,
            LiveSubscriptionJson("canceled", endedAt: new DateTimeOffset(endedAt).ToUnixTimeSeconds()));

        await using (var db = CreateSystemDb())
            await NewService(db, stub).HandleWebhookEventAsync(CheckoutEvent(orgId, checkoutCreated), CancellationToken.None);

        // The retried pre-cancel update needs no outbound call — a fresh service without a
        // queued stub response proves the subscription-state path stays network-free.
        await using (var db = CreateSystemDb())
            await NewService(db, new StubHttpMessageHandler()).HandleWebhookEventAsync(
                SubscriptionUpdatedEvent(checkoutCreated.AddMinutes(5), "active"), CancellationToken.None);

        var sub = await ReloadAsync(orgId);
        sub.Status.Should().Be("canceled", "a pre-cancel update retry must not resurrect status on a dead subscription");
        sub.Plan.Should().Be("free");
        sub.LastStripeEventAt.Should().Be(endedAt, "the skipped event does not move the fence");
    }

    [Theory]
    [InlineData("incomplete_expired")]
    public async Task Checkout_for_an_expired_incomplete_subscription_keeps_free_tier(string terminalStatus)
    {
        var orgId = await SeedFreeOrgAsync();
        var stub = new StubHttpMessageHandler(HttpStatusCode.OK, LiveSubscriptionJson(terminalStatus));

        await using var db = CreateSystemDb();
        await NewService(db, stub).HandleWebhookEventAsync(CheckoutEvent(orgId, DateTime.UtcNow), CancellationToken.None);

        var sub = await ReloadAsync(orgId);
        sub.Plan.Should().Be("free");
        sub.Status.Should().Be("canceled");
        sub.HasVendorPortal.Should().BeFalse();
    }

    [Fact]
    public async Task Checkout_with_a_non_terminal_non_active_live_status_records_that_status_with_the_paid_grant()
    {
        // Falsifies "the status derives from the live subscription": with only 'active'
        // fixtures, reverting to the pre-#275 hardcoded literal passed the whole suite
        // (review finding). A subscription already past_due at handling time keeps the paid
        // entitlement grant (finer status handling stays the job of subscription.updated)
        // but must record the REAL status.
        var orgId = await SeedFreeOrgAsync();
        var stub = new StubHttpMessageHandler(HttpStatusCode.OK, LiveSubscriptionJson("past_due", priceId: "price_monthly_test"));

        await using var db = CreateSystemDb();
        await NewService(db, stub).HandleWebhookEventAsync(CheckoutEvent(orgId, DateTime.UtcNow), CancellationToken.None);

        var sub = await ReloadAsync(orgId);
        sub.Status.Should().Be("past_due", "the live status is recorded, not a hardcoded 'active'");
        sub.Plan.Should().Be("pro");
        sub.DocumentLimit.Should().BeNull("a non-terminal subscription keeps the paid grant");
        sub.HasVendorPortal.Should().BeTrue();
        sub.CurrentPeriodEnd.Should().BeNull(
            "this fixture omits current_period_end — absent means 'not supplied', never the Unix epoch (pins the checkout-site sentinel)");
    }

    [Fact]
    public async Task Checkout_live_fetch_failure_propagates_so_stripe_retries_the_event()
    {
        // ADR 0020's at-least-once contract: a transient outbound failure must throw (the
        // endpoint 5xxes, the event stays unrecorded, Stripe retries) — never half-apply.
        var orgId = await SeedFreeOrgAsync();
        // Responder form, not a single queued response: the Stripe SDK auto-retries 5xx, so
        // every attempt (initial + MaxNetworkRetries) must see the same failure.
        var stub = new StubHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.InternalServerError)
        {
            Content = new StringContent(
                """{"error":{"type":"api_error","message":"Stripe is having a moment."}}""",
                System.Text.Encoding.UTF8, "application/json"),
        });

        await using var db = CreateSystemDb();
        var act = () => NewService(db, stub).HandleWebhookEventAsync(CheckoutEvent(orgId, DateTime.UtcNow), CancellationToken.None);

        await act.Should().ThrowAsync<StripeException>();
        var sub = await ReloadAsync(orgId);
        sub.Plan.Should().Be("free", "nothing may be applied when the live fetch fails");
        sub.StripeCustomerId.Should().BeNull();
        sub.LastStripeEventAt.Should().BeNull("a failed handler must not move the fence");
    }

    [Fact]
    public async Task Checkout_without_stripe_configured_skips_the_fetch_and_falls_back_to_the_active_grant()
    {
        // IsEnabled=false (no secret key — e.g. the WebApplicationFactory harness) must not
        // attempt an outbound call that can only throw; the handler falls back to the
        // historical "completed ⇒ active" grant.
        var orgId = await SeedFreeOrgAsync();
        var stub = new StubHttpMessageHandler(HttpStatusCode.OK, LiveSubscriptionJson("canceled"));

        await using var db = CreateSystemDb();
        await NewService(db, stub, secretKey: "").HandleWebhookEventAsync(CheckoutEvent(orgId, DateTime.UtcNow), CancellationToken.None);

        stub.CallCount.Should().Be(0, "unconfigured Stripe must not make outbound calls");
        var sub = await ReloadAsync(orgId);
        sub.Plan.Should().Be("pro", "no live truth available → fallback grant");
        sub.Status.Should().Be("active");
        sub.StripeSubscriptionId.Should().Be(LiveSubId);
        sub.LastStripeEventAt.Should().NotBeNull();
    }
}
