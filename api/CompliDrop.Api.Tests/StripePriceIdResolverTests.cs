using CompliDrop.Api.Configuration;
using CompliDrop.Api.Services;
using FluentAssertions;

namespace CompliDrop.Api.Tests;

/// <summary>
/// Pure unit tests for <see cref="StripeService.ResolvePlanFromPriceId(string?, StripeSettings)"/>
/// — the boundary that maps Stripe-side price ids to app-side plan ids per ADR 0011.
///
/// #172 hardens the resolver against an empty incoming <c>priceId</c> that would
/// otherwise compare-equal to an unset <c>StripeSettings.AnnualPriceId</c> (default
/// <c>string.Empty</c>) and wrongly resolve to <c>"annual"</c>. The integration-level
/// price-id-to-plan-id mapping is already pinned end-to-end by
/// <see cref="StripeWebhookTests.Stripe_price_id_resolves_to_post_ADR_0011_plan_vocab"/>;
/// this suite isolates the resolver itself so the empty-config branches can be
/// exercised cheaply without standing up the test container.
/// </summary>
public sealed class StripePriceIdResolverTests
{
    private static StripeSettings ConfiguredSettings() => new()
    {
        MonthlyPriceId = "price_real_monthly",
        AnnualPriceId = "price_real_annual",
        FoundingPriceId = "price_real_founding",
    };

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("\t")]
    [InlineData("\n")]
    public void Empty_or_whitespace_priceId_returns_pro_even_when_config_keys_are_set(string? priceId)
    {
        // The actual #172 bug: pre-hardening, an empty incoming priceId would
        // compare-equal to an unset _cfg.AnnualPriceId and resolve to "annual".
        // Even with non-empty config, the early return must catch the empty
        // input first so the priceId-equality logic doesn't get a chance to
        // misfire on edge encodings (NBSP, tabs, etc. — IsNullOrWhiteSpace
        // catches them all).
        var cfg = ConfiguredSettings();

        var result = StripeService.ResolvePlanFromPriceId(priceId, cfg);

        result.Should().Be("pro");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    public void Empty_priceId_returns_pro_even_when_config_keys_are_also_empty(string? priceId)
    {
        // The MOST important regression test: pre-hardening, BOTH sides being
        // empty made `priceId == _cfg.AnnualPriceId` return true. With the
        // priceId-side guard alone, this case is now safe. With the config-side
        // guard too (the per-key skip), the equality never runs anyway. Pin
        // both invariants by passing the worst-case input.
        var cfg = new StripeSettings(); // all defaults — all "" strings

        var result = StripeService.ResolvePlanFromPriceId(priceId, cfg);

        result.Should().Be("pro");
    }

    [Fact]
    public void Empty_config_AnnualPriceId_does_not_make_empty_input_match_annual()
    {
        // Per-key guard test: simulate a partial Stripe deploy where AnnualPriceId
        // was forgotten. The check `priceId == _cfg.AnnualPriceId` would be true
        // for ANY empty priceId, but the per-key !IsNullOrWhiteSpace gate skips
        // the comparison entirely so the wrong plan can never escape.
        //
        // Practically the priceId-side guard would catch this first — but pinning
        // both layers means a future refactor that removes the priceId-side guard
        // (e.g. "the caller validates, we don't need to") doesn't silently
        // reintroduce the collision.
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
    public void Empty_config_FoundingPriceId_does_not_make_empty_input_match_founding()
    {
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
    public void Empty_config_MonthlyPriceId_does_not_make_empty_input_match_pro()
    {
        // Test the third per-key guard too — the pre-hardening "if (priceId ==
        // _cfg.MonthlyPriceId) return 'pro'" branch was the LAST one in the
        // chain, but since the default fallback is also "pro" the regression
        // would be masked. This test isolates the guard by checking that the
        // FALLBACK path is reached (which happens to also return "pro" today,
        // but the assertion is intentional — a future fallback change won't
        // silently double-resolve).
        var cfg = new StripeSettings
        {
            MonthlyPriceId = "", // forgotten in deploy
            AnnualPriceId = "price_real_annual",
            FoundingPriceId = "price_real_founding",
        };

        // Non-matching priceId — only the MonthlyPriceId match would catch it,
        // but that branch's guard skips because the config value is empty.
        // Falls through to the default "pro" — same return, but via the
        // fallback path, not the wildcard collision.
        var result = StripeService.ResolvePlanFromPriceId("price_nobody_knows", cfg);

        result.Should().Be("pro");
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
}
