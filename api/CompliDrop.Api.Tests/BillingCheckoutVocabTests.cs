using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CompliDrop.Api.Entities;
using CompliDrop.Api.Services;
using CompliDrop.Api.Tests.TestHelpers;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace CompliDrop.Api.Tests;

/// <summary>
/// Pins the <c>/api/billing/checkout</c> wire-vocab contract introduced in #147 + ADR 0011.
///
/// The endpoint accepts exactly <c>{ pro, annual, founding }</c> (the
/// <c>KNOWN_CHECKOUT_PLAN_IDS</c> set):
///   - Good plans → 200 with a sessionUrl, the captured priceId on the
///     fake Stripe service matches the expected mapping
///     (pro → MonthlyPriceId, annual → AnnualPriceId, founding →
///     FoundingPriceId).
///   - The legacy <c>"monthly"</c> wire vocab is REJECTED with
///     <c>400 billing.plan_unknown</c> (no silent fallback).
///   - Unknown / empty / attack-shaped inputs are similarly rejected.
///   - Case-variants of valid plans are accepted (the endpoint
///     normalises via ToLowerInvariant).
///   - Plan validation runs BEFORE the IsEnabled gate AND BEFORE the
///     idempotency lookup, so a stale Idempotency-Key + invalid plan
///     gets 400 (not the cached prior response).
///   - When Stripe isn't configured (IsEnabled=false), valid plans
///     still get 503 billing.unavailable AFTER passing validation —
///     proves the two-stage gate order.
/// </summary>
public sealed class BillingCheckoutVocabTests(IntegrationTestFixture fixture) : IntegrationTestBase(fixture)
{
    private FakeStripeService FakeStripe =>
        (FakeStripeService)Fixture.Factory.Services.GetRequiredService<IStripeService>();

    private async Task<(HttpResponseMessage Response, JsonElement Body)> PostCheckoutAsync(
        HttpClient client,
        string plan,
        string? idempotencyKey = null)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, "/api/billing/checkout")
        {
            Content = JsonContent.Create(new { plan })
        };
        if (!string.IsNullOrEmpty(idempotencyKey))
            req.Headers.Add("Idempotency-Key", idempotencyKey);
        var response = await client.SendAsync(req);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        return (response, body);
    }

    private static string? ErrorCode(JsonElement body) =>
        body.TryGetProperty("error", out var errorEl) && errorEl.ValueKind == JsonValueKind.Object
            && errorEl.TryGetProperty("code", out var codeEl)
            ? codeEl.GetString()
            : null;

    private static string? SessionUrl(JsonElement body) =>
        body.TryGetProperty("data", out var dataEl) && dataEl.ValueKind == JsonValueKind.Object
            && dataEl.TryGetProperty("sessionUrl", out var urlEl)
            ? urlEl.GetString()
            : null;

    [Theory]
    [InlineData("pro", "price_monthly_test")]
    [InlineData("annual", "price_annual_test")]
    [InlineData("founding", "price_founding_test")]
    public async Task Accepts_post_ADR_0011_plan_ids_and_resolves_to_expected_priceId(string plan, string expectedPriceId)
    {
        // The endpoint should accept the plan, pass the IsEnabled gate (fake reports
        // IsEnabled=true), reach CreateCheckoutSessionAsync, and resolve the plan id
        // through the BillingEndpoints switch into the right Stripe priceId. The fake
        // captures the priceId so we can assert the mapping directly — this is the
        // tightest possible pin on the boundary BillingEndpoints owns between the
        // plan-id wire vocab and the Stripe-side price id.
        var auth = await RegisterAndLoginAsync();

        var (response, body) = await PostCheckoutAsync(auth.Client, plan);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        SessionUrl(body).Should().StartWith("https://checkout.stripe.test/cs_test_");
        FakeStripe.LastCheckout.Should().NotBeNull();
        FakeStripe.LastCheckout!.PriceId.Should().Be(expectedPriceId);
        FakeStripe.LastCheckout.OrganizationId.Should().Be(auth.OrgId);
    }

    [Theory]
    [InlineData("PRO", "price_monthly_test")]
    [InlineData("Annual", "price_annual_test")]
    [InlineData("FOUNDING", "price_founding_test")]
    public async Task Accepts_post_ADR_0011_plan_ids_case_insensitively(string plan, string expectedPriceId)
    {
        // The endpoint normalises via ToLowerInvariant before matching the switch,
        // so mixed-case input from a manually-crafted client (or a query param that
        // got copy-pasted with shift held) is accepted. Pin this so a future
        // contributor "tightening" by removing the normalisation has to update the
        // test deliberately rather than silently breaking client tolerance.
        var auth = await RegisterAndLoginAsync();

        var (response, body) = await PostCheckoutAsync(auth.Client, plan);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        SessionUrl(body).Should().NotBeNullOrWhiteSpace();
        FakeStripe.LastCheckout!.PriceId.Should().Be(expectedPriceId);
    }

    [Fact]
    public async Task Rejects_legacy_monthly_with_billing_plan_unknown()
    {
        // ADR 0011 retired the `"monthly"` wire vocab. The frontend updated in the same PR;
        // any caller that still sends `"monthly"` gets an actionable 400 rather than a
        // silent-fallback-to-monthly behaviour. This is the most important rejection: it
        // proves the legacy vocab is REMOVED, not aliased.
        var auth = await RegisterAndLoginAsync();

        var (response, body) = await PostCheckoutAsync(auth.Client, "monthly");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        ErrorCode(body).Should().Be("billing.plan_unknown");
        FakeStripe.LastCheckout.Should().BeNull(); // never reached the Stripe layer
    }

    [Theory]
    [InlineData("free")]              // not checkout-eligible — no checkout for free
    [InlineData("enterprise")]        // never existed
    [InlineData("")]                  // empty body
    [InlineData(" ")]                 // whitespace-only
    [InlineData("'; DROP TABLE Subscriptions;--")] // attack-shaped — the allow-list catches it
    public async Task Rejects_unknown_plan_strings_with_billing_plan_unknown(string plan)
    {
        var auth = await RegisterAndLoginAsync();

        var (response, body) = await PostCheckoutAsync(auth.Client, plan);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        ErrorCode(body).Should().Be("billing.plan_unknown");
    }

    [Fact]
    public async Task Returns_503_when_Stripe_is_not_configured_but_plan_is_valid()
    {
        // Validation runs BEFORE the IsEnabled gate, so a valid plan with Stripe
        // disabled still produces the 503 billing.unavailable — and proves the two-
        // stage ordering (good plan does NOT 400, only 503).
        FakeStripe.IsEnabled = false;
        var auth = await RegisterAndLoginAsync();

        var (response, body) = await PostCheckoutAsync(auth.Client, "pro");

        response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        ErrorCode(body).Should().Be("billing.unavailable");
        FakeStripe.LastCheckout.Should().BeNull();
    }

    [Fact]
    public async Task Plan_validation_pre_empts_a_stale_idempotency_hit()
    {
        // ADR 0011 + the BillingEndpoints reorder say input validation precedes the
        // idempotency lookup. To genuinely pin the ordering we have to set up a
        // cached IdempotencyRecord first, then re-POST with the SAME key but an
        // INVALID plan. If validation ran AFTER idempotency, the cached prior
        // response would come back (200 or 503 from the first call). The reorder
        // means the second POST gets 400 plan_unknown — the new request's bad input
        // wins over the stale cache.
        var auth = await RegisterAndLoginAsync();
        var key = $"ikey_{Guid.NewGuid():N}";

        // Seed a cached 200 response under (orgId, key) — same shape the endpoint
        // would have stored after a successful prior POST. Storing directly via the
        // DB (rather than via a real POST) keeps the test focused on the ordering
        // contract; the real-POST path is covered by Accepts_*.
        await using (var db = CreateSystemDb())
        {
            db.IdempotencyRecords.Add(new IdempotencyRecord
            {
                Id = Guid.NewGuid(),
                OrganizationId = auth.OrgId,
                Key = key,
                RequestPath = "/api/billing/checkout",
                StatusCode = 200,
                ResponseJson = "{\"data\":{\"sessionUrl\":\"https://stale.cached.url/cs_test_stale\"},\"error\":null}",
                CreatedAt = DateTime.UtcNow,
                ExpiresAt = DateTime.UtcNow.AddHours(24),
            });
            await db.SaveChangesAsync();
        }

        // Re-POST with the SAME key but a bad plan. The reorder means validation
        // catches the bad plan BEFORE the idempotency lookup runs, so the response
        // is 400 plan_unknown — NOT the cached 200/sessionUrl.
        var (response, body) = await PostCheckoutAsync(auth.Client, "monthly", idempotencyKey: key);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        ErrorCode(body).Should().Be("billing.plan_unknown");
        SessionUrl(body).Should().BeNull(); // proves the cached response was NOT returned
        FakeStripe.LastCheckout.Should().BeNull();
    }
}
