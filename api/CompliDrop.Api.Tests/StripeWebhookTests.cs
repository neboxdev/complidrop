using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CompliDrop.Api.Entities;
using CompliDrop.Api.Tests.TestHelpers;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Stripe;
using Subscription = CompliDrop.Api.Entities.Subscription;

namespace CompliDrop.Api.Tests;

/// <summary>
/// Integration tests for the Stripe webhook: signature verification, event dedupe, and the
/// subscription lifecycle transitions that apply without an external Stripe API call
/// (customer.subscription.created / .updated / .deleted). Runs on the Testcontainers harness.
/// </summary>
public sealed class StripeWebhookTests(IntegrationTestFixture fixture) : IntegrationTestBase(fixture)
{
    // Must match CustomWebApplicationFactory's Stripe:WebhookSecret.
    private const string WebhookSecret = "whsec_test_secret_for_integration_tests";

    // Default `plan: "pro"` matches the post-ADR-0011 vocab. The
    // Stripe-side `MonthlyPriceId` config key is unchanged; the
    // application-side plan id is now "pro".
    private async Task<string> SeedSubscriptionAsync(string plan = "pro", string status = "active", bool hasPortal = true)
    {
        var orgId = Guid.NewGuid();
        var subId = $"sub_{Guid.NewGuid():N}";
        var now = DateTime.UtcNow;

        await using var db = CreateSystemDb();
        db.Organizations.Add(new Organization { Id = orgId, Name = $"Org-{orgId:N}", CreatedAt = now, UpdatedAt = now });
        db.Subscriptions.Add(new Subscription
        {
            Id = Guid.NewGuid(),
            OrganizationId = orgId,
            StripeSubscriptionId = subId,
            Plan = plan,
            Status = status,
            DocumentLimit = null,
            HasVendorPortal = hasPortal,
            CreatedAt = now,
            UpdatedAt = now
        });
        await db.SaveChangesAsync();
        return subId;
    }

    private static object Envelope(string eventId, string type, object dataObject) => new
    {
        id = eventId,
        @object = "event",
        api_version = StripeConfiguration.ApiVersion,
        created = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
        livemode = false,
        pending_webhooks = 0,
        request = new { id = (string?)null, idempotency_key = (string?)null },
        type,
        data = new { @object = dataObject }
    };

    private static string DeletedEvent(string eventId, string subId) => JsonSerializer.Serialize(
        Envelope(eventId, "customer.subscription.deleted",
            new { id = subId, @object = "subscription", status = "canceled" }));

    private static string SubscriptionStateEvent(string eventId, string type, string subId, string status, string priceId) => JsonSerializer.Serialize(
        Envelope(eventId, type,
        new
        {
            id = subId,
            @object = "subscription",
            status,
            items = new
            {
                @object = "list",
                data = new[] { new { id = "si_1", @object = "subscription_item", price = new { id = priceId, @object = "price" } } }
            }
        }));

    private static string SignatureFor(string payload)
    {
        var t = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(WebhookSecret));
        var hash = hmac.ComputeHash(Encoding.UTF8.GetBytes($"{t}.{payload}"));
        return $"t={t},v1={Convert.ToHexString(hash).ToLowerInvariant()}";
    }

    private async Task<HttpResponseMessage> PostWebhook(string payload, string? signature)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, "/api/billing/webhook")
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json")
        };
        if (signature is not null) req.Headers.TryAddWithoutValidation("Stripe-Signature", signature);
        return await CreateClient().SendAsync(req);
    }

    private async Task<Subscription> ReloadAsync(string subId)
    {
        await using var db = CreateSystemDb();
        return await db.Subscriptions.FirstAsync(s => s.StripeSubscriptionId == subId);
    }

    [Fact]
    public async Task Valid_signed_subscription_deleted_cancels_the_subscription()
    {
        var subId = await SeedSubscriptionAsync(plan: "pro", status: "active", hasPortal: true);
        var payload = DeletedEvent($"evt_{Guid.NewGuid():N}", subId);

        var resp = await PostWebhook(payload, SignatureFor(payload));

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var sub = await ReloadAsync(subId);
        sub.Plan.Should().Be("free");
        sub.Status.Should().Be("canceled");
        sub.HasVendorPortal.Should().BeFalse();
        sub.DocumentLimit.Should().Be(5);
    }

    [Theory]
    [InlineData("customer.subscription.updated")]
    [InlineData("customer.subscription.created")]
    public async Task Valid_signed_subscription_state_event_applies_status_and_plan(string eventType)
    {
        // Seed "free" and send the ANNUAL price (which maps to "annual", NOT the "pro" fallback)
        // so the assertion genuinely proves price->plan re-derivation, not the seeded value or fallback.
        var subId = await SeedSubscriptionAsync(plan: "free", status: "active");
        var payload = SubscriptionStateEvent($"evt_{Guid.NewGuid():N}", eventType, subId, "past_due", "price_annual_test");

        var resp = await PostWebhook(payload, SignatureFor(payload));

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var sub = await ReloadAsync(subId);
        sub.Status.Should().Be("past_due");
        sub.Plan.Should().Be("annual");
    }

    [Theory]
    [InlineData("price_monthly_test", "pro")]
    [InlineData("price_annual_test", "annual")]
    [InlineData("price_founding_test", "founding")]
    public async Task Stripe_price_id_resolves_to_post_ADR_0011_plan_vocab(string priceId, string expectedPlan)
    {
        // Pins the boundary in StripeService.ResolvePlanFromPriceId (ADR 0011): the
        // Stripe-side `MonthlyPriceId` config key resolves to the app-side `"pro"` plan id
        // (the application vocab is `free | pro | annual | founding`; Stripe-side config
        // names stay billing-cadence words). A regression would either resurrect the
        // legacy `"monthly"` literal (which the migration scrubbed) or break the
        // founding/annual mappings.
        var subId = await SeedSubscriptionAsync(plan: "free", status: "active");
        var payload = SubscriptionStateEvent($"evt_{Guid.NewGuid():N}", "customer.subscription.updated", subId, "active", priceId);

        var resp = await PostWebhook(payload, SignatureFor(payload));

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var sub = await ReloadAsync(subId);
        sub.Plan.Should().Be(expectedPlan);
    }

    [Fact]
    public async Task Unknown_event_type_is_accepted_as_a_noop()
    {
        var subId = await SeedSubscriptionAsync(plan: "pro", status: "active");
        var payload = JsonSerializer.Serialize(Envelope(
            $"evt_{Guid.NewGuid():N}", "customer.subscription.trial_will_end",
            new { id = subId, @object = "subscription", status = "trialing" }));

        var resp = await PostWebhook(payload, SignatureFor(payload));

        resp.StatusCode.Should().Be(HttpStatusCode.OK);          // accepted + recorded
        (await ReloadAsync(subId)).Status.Should().Be("active"); // but no handler ran
    }

    [Fact]
    public async Task Missing_signature_is_rejected_with_no_state_change()
    {
        var subId = await SeedSubscriptionAsync(status: "active");
        var payload = DeletedEvent($"evt_{Guid.NewGuid():N}", subId);

        var resp = await PostWebhook(payload, signature: null);

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ReloadAsync(subId)).Status.Should().Be("active");
    }

    [Fact]
    public async Task Invalid_signature_is_rejected_with_no_state_change()
    {
        var subId = await SeedSubscriptionAsync(status: "active");
        var payload = DeletedEvent($"evt_{Guid.NewGuid():N}", subId);

        // A current-timestamp signature with a corrupted hash — isolates hash-mismatch rejection
        // from the timestamp-tolerance window.
        var valid = SignatureFor(payload);
        var corrupted = valid[..^1] + (valid[^1] == '0' ? '1' : '0');

        var resp = await PostWebhook(payload, corrupted);

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ReloadAsync(subId)).Status.Should().Be("active");
    }

    [Fact]
    public async Task Duplicate_event_id_is_processed_only_once()
    {
        var subId = await SeedSubscriptionAsync(plan: "pro", status: "active");
        var eventId = $"evt_{Guid.NewGuid():N}";
        var payload = SubscriptionStateEvent(eventId, "customer.subscription.updated", subId, "past_due", "price_monthly_test");

        (await PostWebhook(payload, SignatureFor(payload))).StatusCode.Should().Be(HttpStatusCode.OK);

        // Tamper directly, then resend the SAME event id — it must be deduped (state not re-applied).
        await using (var db = CreateSystemDb())
        {
            var sub = await db.Subscriptions.FirstAsync(s => s.StripeSubscriptionId == subId);
            sub.Status = "tampered";
            await db.SaveChangesAsync();
        }

        (await PostWebhook(payload, SignatureFor(payload))).StatusCode.Should().Be(HttpStatusCode.OK);

        (await ReloadAsync(subId)).Status.Should().Be("tampered");
        await using var db2 = CreateSystemDb();
        (await db2.ProcessedStripeEvents.CountAsync(p => p.Id == eventId)).Should().Be(1);
    }
}
