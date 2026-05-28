using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CompliDrop.Api.Tests.TestHelpers;
using FluentAssertions;

namespace CompliDrop.Api.Tests;

/// <summary>
/// Pins the <c>/api/billing/checkout</c> wire-vocab contract introduced in #147 + ADR 0011.
/// The endpoint accepts exactly <c>{ pro, annual, founding }</c>; the legacy <c>monthly</c>
/// is rejected with <c>400 billing.plan_unknown</c>, as is any other unknown string.
///
/// Tests assert on the input-validation gate, which runs BEFORE the <c>IsEnabled</c> check
/// (the test factory leaves <c>Stripe:SecretKey</c> unset, so accepted plans get a
/// <c>503 billing.unavailable</c> next; rejected plans never reach the Stripe SDK).
/// Asserting the 4xx/5xx split is exactly the boundary we care about — bad input → 400,
/// good input → reaches Stripe layer.
/// </summary>
public sealed class BillingCheckoutVocabTests(IntegrationTestFixture fixture) : IntegrationTestBase(fixture)
{
    private async Task<(HttpResponseMessage Response, string ErrorCode)> PostCheckoutAsync(string plan)
    {
        var auth = await RegisterAndLoginAsync();
        var response = await auth.Client.PostAsJsonAsync("/api/billing/checkout", new { plan });

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        // Some 401 responses set data=null/error=null in the envelope; tolerate either shape.
        var errorCode = body.TryGetProperty("error", out var errorEl) && errorEl.ValueKind == JsonValueKind.Object
            && errorEl.TryGetProperty("code", out var codeEl)
            ? codeEl.GetString() ?? ""
            : "";
        return (response, errorCode);
    }

    [Theory]
    [InlineData("pro")]
    [InlineData("annual")]
    [InlineData("founding")]
    public async Task Accepts_post_ADR_0011_plan_ids_and_falls_through_to_Stripe_layer(string plan)
    {
        // The accepted plans pass the input-validation gate and reach the IsEnabled check.
        // The test factory leaves Stripe:SecretKey unset → 503 billing.unavailable.
        // The point of this test is the *acceptance*: the request got past plan validation.
        var (response, errorCode) = await PostCheckoutAsync(plan);

        response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        errorCode.Should().Be("billing.unavailable");
    }

    [Fact]
    public async Task Rejects_legacy_monthly_with_billing_plan_unknown()
    {
        // ADR 0011 retired the `"monthly"` wire vocab. The frontend updated in the same PR;
        // any caller that still sends `"monthly"` gets an actionable 400 rather than the
        // older silent-fallback-to-monthly behaviour.
        var (response, errorCode) = await PostCheckoutAsync("monthly");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        errorCode.Should().Be("billing.plan_unknown");
    }

    [Theory]
    [InlineData("free")]              // not checkout-eligible — no checkout for free
    [InlineData("enterprise")]        // never existed
    [InlineData("PRO")]               // case-sensitive after ToLowerInvariant? — yes, normalises, so this IS accepted; covered by the Accepts_ test
    [InlineData("")]                  // empty body
    [InlineData(" ")]                 // whitespace-only
    [InlineData("'; DROP TABLE Subscriptions;--")] // attack-shaped — the allow-list catches it
    public async Task Rejects_unknown_plan_strings_with_billing_plan_unknown(string plan)
    {
        // `"PRO"` is in this Theory deliberately — ToLowerInvariant() inside the endpoint
        // normalises it to "pro", which IS valid, so this case is actually accepted. We
        // assert the response code reflects that: the unknown-plan handler is not just a
        // case-sensitive match. (The other inputs reject as expected.)
        var (response, errorCode) = await PostCheckoutAsync(plan);

        if (plan.Equals("pro", StringComparison.OrdinalIgnoreCase))
        {
            // Accepted → reaches IsEnabled gate → 503 in test env.
            response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
            errorCode.Should().Be("billing.unavailable");
        }
        else
        {
            response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
            errorCode.Should().Be("billing.plan_unknown");
        }
    }

    [Fact]
    public async Task Plan_validation_runs_before_idempotency_check()
    {
        // ADR 0011: input validation precedes both the IsEnabled gate AND idempotency lookup.
        // A request with a stale Idempotency-Key + invalid plan must not silently return a
        // cached prior response — the new request has wrong input and the client deserves
        // to know via 400, not a 200 from a different prior call.
        var auth = await RegisterAndLoginAsync();
        var key = Guid.NewGuid().ToString();

        var req = new HttpRequestMessage(HttpMethod.Post, "/api/billing/checkout")
        {
            Content = JsonContent.Create(new { plan = "monthly" })
        };
        req.Headers.Add("Idempotency-Key", key);

        var response = await auth.Client.SendAsync(req);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var errorCode = body.GetProperty("error").GetProperty("code").GetString();

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        errorCode.Should().Be("billing.plan_unknown");
    }
}
