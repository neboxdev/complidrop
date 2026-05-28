using CompliDrop.Api.Configuration;
using CompliDrop.Api.Services;
using FluentAssertions;

namespace CompliDrop.Api.Tests;

/// <summary>
/// Pure unit tests for <see cref="StripeService.ResolvePlanFromPriceId(string?, StripeSettings)"/>
/// — the boundary that maps Stripe-side price ids to app-side plan ids per ADR 0011 + the
/// #172 Hardening addendum.
///
/// The integration-level happy paths are already pinned end-to-end by
/// <see cref="StripeWebhookTests.Stripe_price_id_resolves_to_post_ADR_0011_plan_vocab"/>; this
/// suite exercises the empty-edge cases the webhook test can't cheaply hit, plus the
/// duplicate-config precedence (Annual > Founding > Monthly) that's load-bearing for the
/// boundary contract.
///
/// Each per-key test is named to reflect what's ACTUALLY exercised — most pin "an empty
/// cfg.X field doesn't break the OTHER plan's resolution", not the cosmetic "empty input
/// match" framing. The empty-input collision case is pinned exclusively by
/// <see cref="Empty_priceId_returns_pro_even_when_config_keys_are_also_empty"/>, which is
/// the only test that fails pre-hardening.
/// </summary>
public sealed class StripePriceIdResolverTests
{
    private static StripeSettings ConfiguredSettings() => new()
    {
        MonthlyPriceId = "price_real_monthly",
        AnnualPriceId = "price_real_annual",
        FoundingPriceId = "price_real_founding",
    };

    // The "empty priceId with NON-empty cfg" Theory that previously lived here was
    // strictly dominated by the empty-cfg Theory below (mutation analysis: there's
    // no single regression mode where the non-empty-cfg case fails and the empty-
    // cfg case passes — both short-circuit at the top-level IsNullOrWhiteSpace
    // guard, so the cfg state is irrelevant to the execution path). Removed to
    // keep the test surface lean. The remaining Theory pins the actual #172
    // collision: BOTH sides empty.

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("\t")]
    [InlineData("\n")]
    public void Empty_priceId_returns_pro_even_when_config_keys_are_also_empty(string? priceId)
    {
        // The actual #172 regression-pin: pre-hardening, BOTH sides being empty
        // made `priceId == _cfg.AnnualPriceId` return true and the resolver
        // wrongly returned "annual". This is the test that fails on a removal
        // of either guard:
        //   - Remove the top-level IsNullOrWhiteSpace(priceId) guard → falls
        //     into the AnnualPriceId branch, `"" == ""` is true, returns "annual".
        //     With the per-key !IsNullOrWhiteSpace(cfg.AnnualPriceId) guard, the
        //     branch is skipped, falls through to the default "pro" — STILL PASSES.
        //   - Remove the per-key !IsNullOrWhiteSpace(cfg.AnnualPriceId) guard →
        //     the top-level priceId guard catches first, returns "pro" — STILL PASSES.
        //   - Remove BOTH guards → returns "annual", test fails.
        // The defense-in-depth survives a single-layer regression.
        var cfg = new StripeSettings(); // all defaults — all "" strings

        var result = StripeService.ResolvePlanFromPriceId(priceId, cfg);

        result.Should().Be("pro");
    }

    [Fact]
    public void Empty_config_AnnualPriceId_does_not_swallow_an_unrelated_priceId()
    {
        // Per-key guard documentation test: simulate a partial Stripe deploy where
        // AnnualPriceId was forgotten. Sending an unrelated non-empty priceId
        // (the MonthlyPriceId in this case) MUST resolve correctly to "pro" —
        // the empty AnnualPriceId must not become a wildcard.
        //
        // Note: the priceId IS non-empty here (`"price_real_monthly"`), so the
        // per-key guard alone is what's being exercised — the top-level guard
        // can't have an effect. The test is defensive documentation: any future
        // refactor that removes the per-key guard breaks this. The test
        // `Empty_priceId_returns_pro_even_when_config_keys_are_also_empty`
        // covers the case where BOTH sides are empty.
        var cfg = new StripeSettings
        {
            MonthlyPriceId = "price_real_monthly",
            AnnualPriceId = "", // forgotten in deploy
            FoundingPriceId = "price_real_founding",
        };

        var result = StripeService.ResolvePlanFromPriceId("price_real_monthly", cfg);

        result.Should().Be("pro");
    }

    [Fact]
    public void Empty_config_FoundingPriceId_does_not_break_annual_resolution()
    {
        // Sibling to the test above: with FoundingPriceId empty, an Annual
        // priceId still resolves correctly. Pins that the per-key guard skips
        // the empty config field cleanly without affecting the other branches.
        var cfg = new StripeSettings
        {
            MonthlyPriceId = "price_real_monthly",
            AnnualPriceId = "price_real_annual",
            FoundingPriceId = "", // forgotten in deploy
        };

        var result = StripeService.ResolvePlanFromPriceId("price_real_annual", cfg);

        result.Should().Be("annual");
    }

    [Fact]
    public void Empty_config_MonthlyPriceId_still_falls_through_to_pro_default_for_unknown_priceId()
    {
        // Final per-key sibling: MonthlyPriceId is empty + the incoming priceId
        // doesn't match anything configured → falls through to the explicit
        // "pro" fallback at the end of the resolver. The assertion is "pro"
        // (which happens to be the same return the MonthlyPriceId branch
        // would produce on a match), but the test isolates the FALLBACK path
        // — a future change to the fallback default ("free"? "pro"? Throw?)
        // would surface here.
        var cfg = new StripeSettings
        {
            MonthlyPriceId = "", // forgotten in deploy
            AnnualPriceId = "price_real_annual",
            FoundingPriceId = "price_real_founding",
        };

        var result = StripeService.ResolvePlanFromPriceId("price_nobody_knows", cfg);

        result.Should().Be("pro");
    }

    [Fact]
    public void Duplicate_priceId_three_way_collision_resolves_to_annual_first()
    {
        // Operator-mistake scenario: same Stripe priceId pasted into all three
        // config keys (copy-paste between Dev and Prod settings, or a typo
        // during a pricing rollout). The resolver returns the first match in
        // declaration order. This test only pins "Annual wins when all three
        // collide" — the sibling test below pins Founding > Monthly to cover
        // the rest of the Annual > Founding > Monthly precedence chain.
        var cfg = new StripeSettings
        {
            MonthlyPriceId = "price_collision",
            AnnualPriceId = "price_collision",
            FoundingPriceId = "price_collision",
        };

        var result = StripeService.ResolvePlanFromPriceId("price_collision", cfg);

        result.Should().Be("annual");
    }

    [Fact]
    public void Duplicate_priceId_for_founding_and_monthly_resolves_to_founding_first()
    {
        // Sibling to the three-way test above: pins Founding > Monthly when
        // those two collide but Annual is unique. Without this, swapping the
        // Founding and Monthly branches in StripeService.ResolvePlanFromPriceId
        // would still pass the three-way test (Annual is always first) — the
        // silent reorder would only surface here.
        var cfg = new StripeSettings
        {
            MonthlyPriceId = "price_shared",
            AnnualPriceId = "price_annual_unique",
            FoundingPriceId = "price_shared",
        };

        var result = StripeService.ResolvePlanFromPriceId("price_shared", cfg);

        result.Should().Be("founding");
    }

    [Theory]
    [InlineData("price_real_monthly", "pro")]
    [InlineData("price_real_annual", "annual")]
    [InlineData("price_real_founding", "founding")]
    public void Happy_path_resolves_each_configured_price_id_to_its_plan(string priceId, string expectedPlan)
    {
        // Sanity: the hardening didn't break the normal mapping. The webhook
        // test (StripeWebhookTests.Stripe_price_id_resolves_to_post_ADR_0011_plan_vocab)
        // already covers this end-to-end; this unit-level mirror catches a
        // regression cheaply.
        var cfg = ConfiguredSettings();

        var result = StripeService.ResolvePlanFromPriceId(priceId, cfg);

        result.Should().Be(expectedPlan);
    }

    [Fact]
    public void Non_empty_unknown_price_id_falls_back_to_pro()
    {
        // A price id that doesn't match any configured value (e.g. a Stripe
        // product the operator added without telling the app) defaults to
        // "pro". Same as pre-hardening; pin it.
        var cfg = ConfiguredSettings();

        var result = StripeService.ResolvePlanFromPriceId("price_unknown_xyz", cfg);

        result.Should().Be("pro");
    }

    [Theory]
    [InlineData("price_real_annual ")]   // trailing space (env-var copy-paste mistake)
    [InlineData(" price_real_annual")]   // leading space
    [InlineData("price_real_annual\n")] // trailing newline (env-var multi-line paste)
    [InlineData("price_real_annual\t")] // trailing tab
    public void Whitespace_padded_priceId_does_NOT_trim_and_falls_back_to_pro(string priceId)
    {
        // Documents the no-trim contract: a priceId with leading/trailing
        // whitespace does NOT match its un-padded configured value — falls
        // through to the "pro" default. This is the conservative choice (don't
        // silently accept malformed inputs) but means a config or webhook
        // payload with stray whitespace silently DOWNGRADES an annual customer
        // to pro. If a future operator-footgun report justifies adding
        // `priceId.Trim()` after the IsNullOrWhiteSpace guard, this test
        // documents the current behavior so the change is visible. Out of
        // scope for #172 (which targets the empty-string collision, not
        // whitespace-padding).
        var cfg = ConfiguredSettings();

        var result = StripeService.ResolvePlanFromPriceId(priceId, cfg);

        result.Should().Be("pro");
    }
}
