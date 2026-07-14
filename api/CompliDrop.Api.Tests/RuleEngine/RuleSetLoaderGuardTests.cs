using CompliDrop.Api.RuleEngine;
using FluentAssertions;

namespace CompliDrop.Api.Tests.RuleEngine;

/// <summary>
/// Pass-5 fail-fast guards: the silent-default and authoring-trap classes the review found in the loader
/// net (UNVER-9/16/17/18/19/20/21/22 and the fixed-date grace guard), plus the reviewGate mechanics that
/// previously had no SYNTHETIC coverage (UNVER-23 — the only gate tests rode on the real TX security data,
/// so lifting that gate would have silently deleted all coverage of the mechanism).
/// </summary>
public class RuleSetLoaderGuardTests
{
    private static RuleSet LoadInline(string json, RuleLoadOptions? options = null) =>
        RuleSetLoader.LoadFromJson("inline-guard-test.json", json, options);

    // Mirrors RuleSetLoaderTests.MinimalValidRule (kept local so each file reads standalone).
    private const string MinimalValidRule = """
    {
      "schemaVersion": 1,
      "rules": [{
        "id": "test-min",
        "obligationRef": "OBL-MIN",
        "jurisdiction": "us-tx",
        "entityTypes": ["test-widget"],
        "category": "license",
        "basis": "regulatory",
        "versions": [{
          "version": 1,
          "validFrom": "2026-01-01",
          "validTo": null,
          "confidence": "verified",
          "applicability": { "fact": "operatesInterstate", "op": "eq", "value": true },
          "obligation": { "name": "Minimal", "documentType": "license", "documentSubType": "test-min" },
          "cadence": { "kind": "one-time", "anchor": "documentExpiration", "gracePeriodDays": 0 },
          "citation": { "section": "Synthetic §1" },
          "rationale": "Synthetic minimal rule.",
          "userAction": "Ask the vendor."
        }]
      }]
    }
    """;

    private static string AsInsurance(string json) => json
        .Replace("\"category\": \"license\"", "\"category\": \"insurance\"")
        .Replace("\"documentType\": \"license\"", "\"documentType\": \"coi\"");

    private static string WithMinimums(string json, string minimums) => json.Replace(
        "\"citation\": { \"section\": \"Synthetic §1\" }",
        $"\"insuranceMinimums\": {minimums}, \"citation\": {{ \"section\": \"Synthetic §1\" }}");

    private static string WithCadence(string json, string cadence) => json.Replace(
        "\"cadence\": { \"kind\": \"one-time\", \"anchor\": \"documentExpiration\", \"gracePeriodDays\": 0 }",
        $"\"cadence\": {cadence}");

    // ---------------- silent-default traps ----------------

    [Fact]
    public void Rejects_a_version_that_omits_confidence()
    {
        // UNVER-17: a non-nullable enum would silently default to Verified (member 0) and SHIP an
        // unreviewed rule in the verified-only posture — the worst possible direction.
        var json = MinimalValidRule.Replace("\"confidence\": \"verified\",", "");

        var act = () => LoadInline(json);
        act.Should().Throw<RuleSchemaException>().WithMessage("*'confidence' is required*");
    }

    [Fact]
    public void Rejects_a_version_that_omits_validFrom()
    {
        // UNVER-19: an omitted validFrom deserializes to DateOnly.MinValue — an open-since-year-1 window
        // that also inverts the latest-validFrom-wins version precedence.
        var json = MinimalValidRule.Replace("\"validFrom\": \"2026-01-01\",", "");

        var act = () => LoadInline(json);
        act.Should().Throw<RuleSchemaException>().WithMessage("*'validFrom' is required*");
    }

    [Fact]
    public void Rejects_a_verified_version_without_a_citation()
    {
        // UNVER-20: a verified rule's whole claim to shipping is its primary-source citation.
        var json = MinimalValidRule.Replace("\"citation\": { \"section\": \"Synthetic §1\" },", "");

        var act = () => LoadInline(json);
        act.Should().Throw<RuleSchemaException>().WithMessage("*verified*citation*");
    }

    [Fact]
    public void Rejects_a_typoed_property_key_instead_of_silently_ignoring_it()
    {
        // UNVER-18: the schema is FROZEN and closed — a misspelled documentSubType key would otherwise
        // silently null the field and broaden document matching to DocumentType alone (a false-Satisfied path).
        var json = MinimalValidRule.Replace("\"documentSubType\": \"test-min\"", "\"documentSubTye\": \"test-min\"");

        var act = () => LoadInline(json);
        act.Should().Throw<RuleSchemaException>();
    }

    // ---------------- insurance-minimums shape (v1.2) ----------------

    [Fact]
    public void Rejects_an_insurance_rule_without_minimums()
    {
        // UNVER-21: the statutory floor is the point of an insurance rule.
        var act = () => LoadInline(AsInsurance(MinimalValidRule));
        act.Should().Throw<RuleSchemaException>().WithMessage("*must carry insuranceMinimums*");
    }

    [Fact]
    public void Rejects_insurance_minimums_that_omit_kind()
    {
        var json = WithMinimums(AsInsurance(MinimalValidRule),
            """{ "coverageLine": "general-liability", "perOccurrence": 1000000, "currency": "USD" }""");

        var act = () => LoadInline(json);
        act.Should().Throw<RuleSchemaException>().WithMessage("*kind is required*");
    }

    [Fact]
    public void Rejects_insurance_minimums_that_omit_coverage_line()
    {
        // The same silent-default trap as confidence: an omitted coverageLine must never default into
        // the general-liability comparison path.
        var json = WithMinimums(AsInsurance(MinimalValidRule),
            """{ "kind": "combined-single-limit", "perOccurrence": 1000000, "currency": "USD" }""");

        var act = () => LoadInline(json);
        act.Should().Throw<RuleSchemaException>().WithMessage("*coverageLine is required*");
    }

    [Fact]
    public void Rejects_a_combined_single_limit_floor_carrying_split_limit_fields()
    {
        var json = WithMinimums(AsInsurance(MinimalValidRule),
            """{ "kind": "combined-single-limit", "coverageLine": "general-liability", "perOccurrence": 1000000, "perOccurrencePersonalInjury": 50000, "currency": "USD" }""");

        var act = () => LoadInline(json);
        act.Should().Throw<RuleSchemaException>().WithMessage("*must not carry split-limit fields*");
    }

    [Fact]
    public void Rejects_a_split_limits_floor_missing_its_bi_pd_component()
    {
        var json = WithMinimums(AsInsurance(MinimalValidRule),
            """{ "kind": "split-limits", "coverageLine": "general-liability", "aggregate": 200000, "currency": "USD" }""");

        var act = () => LoadInline(json);
        act.Should().Throw<RuleSchemaException>().WithMessage("*requires perOccurrenceBodilyInjuryAndPropertyDamage*");
    }

    [Fact]
    public void Accepts_the_two_real_statutory_shapes()
    {
        var csl = WithMinimums(AsInsurance(MinimalValidRule),
            """{ "kind": "combined-single-limit", "coverageLine": "general-liability", "perOccurrence": 1000000, "currency": "USD" }""");
        ((Action)(() => LoadInline(csl))).Should().NotThrow("§2151.1012-shaped CSL floors are valid");

        var split = WithMinimums(AsInsurance(MinimalValidRule),
            """{ "kind": "split-limits", "coverageLine": "general-liability", "perOccurrenceBodilyInjuryAndPropertyDamage": 100000, "perOccurrencePersonalInjury": 50000, "aggregate": 200000, "currency": "USD" }""");
        ((Action)(() => LoadInline(split))).Should().NotThrow("§1702.124(c)-shaped split floors are valid");
    }

    // ---------------- authoring traps ----------------

    [Fact]
    public void Rejects_an_explicit_empty_document_subtype()
    {
        // UNVER-22: "" silently behaves as no-subtype (matching broadens to DocumentType alone);
        // null is the one way to say "no subtype".
        var json = MinimalValidRule.Replace("\"documentSubType\": \"test-min\"", "\"documentSubType\": \"\"");

        var act = () => LoadInline(json);
        act.Should().Throw<RuleSchemaException>().WithMessage("*documentSubType must be a non-empty token*");
    }

    [Fact]
    public void Rejects_an_empty_any_combinator_but_keeps_empty_all_legal()
    {
        // UNVER-9: any([]) is constant-False — an authoring slip that silently drops the obligation for
        // everyone. all([]) stays legal as the documented always-applies idiom.
        var emptyAny = MinimalValidRule.Replace(
            """"applicability": { "fact": "operatesInterstate", "op": "eq", "value": true }"""",
            """"applicability": { "any": [] }"""");
        ((Action)(() => LoadInline(emptyAny))).Should().Throw<RuleSchemaException>().WithMessage("*'any' combinator with zero children*");

        var emptyAll = MinimalValidRule.Replace(
            """"applicability": { "fact": "operatesInterstate", "op": "eq", "value": true }"""",
            """"applicability": { "all": [] }"""");
        ((Action)(() => LoadInline(emptyAll))).Should().NotThrow("empty all is the deliberate always-applies idiom");
    }

    [Fact]
    public void Rejects_a_calendar_impossible_fixed_date_but_allows_leap_day()
    {
        // UNVER-16: Feb 30 must fail fast rather than silently clamp; Feb 29 stays legal (leap target).
        var febThirty = WithCadence(MinimalValidRule,
            """{ "kind": "fixed-annual", "anchor": "fixedDate", "fixedDate": { "month": 2, "day": 30 }, "gracePeriodDays": 0 }""");
        ((Action)(() => LoadInline(febThirty))).Should().Throw<RuleSchemaException>().WithMessage("*not a real calendar date*");

        var leapDay = WithCadence(MinimalValidRule,
            """{ "kind": "fixed-annual", "anchor": "fixedDate", "fixedDate": { "month": 2, "day": 29 }, "gracePeriodDays": 0 }""");
        ((Action)(() => LoadInline(leapDay))).Should().NotThrow("Feb 29 is the deliberate clamp target");
    }

    [Fact]
    public void Rejects_a_grace_period_on_a_fixed_date_anchor_until_the_engine_honors_it()
    {
        // UNVER-14/15: the next-occurrence computation re-anchors on the evaluation date, so grace on a
        // fixed-date anchor is structurally inert — reject rather than silently misclassify the first
        // grace-bearing fixed-date rule.
        var json = WithCadence(MinimalValidRule,
            """{ "kind": "fixed-annual", "anchor": "fixedDate", "fixedDate": { "month": 5, "day": 15 }, "gracePeriodDays": 30 }""");

        var act = () => LoadInline(json);
        act.Should().Throw<RuleSchemaException>().WithMessage("*gracePeriodDays is not honored*");
    }

    [Fact]
    public void Rejects_round_to_month_end_without_a_period()
    {
        var json = WithCadence(MinimalValidRule,
            """{ "kind": "one-time", "anchor": "documentExpiration", "roundToMonthEnd": true, "gracePeriodDays": 0 }""");

        var act = () => LoadInline(json);
        act.Should().Throw<RuleSchemaException>().WithMessage("*roundToMonthEnd only applies to a period-based cadence*");
    }

    // ---------------- reviewGate mechanics on SYNTHETIC data (UNVER-23) ----------------

    private const string GatedFixture = """
    {
      "schemaVersion": 1,
      "reviewGate": "test-gate",
      "rules": [{
        "id": "test-gated-license",
        "obligationRef": "OBL-GATED",
        "jurisdiction": "us-tx",
        "entityTypes": ["test-widget"],
        "category": "license",
        "basis": "regulatory",
        "versions": [{
          "version": 1,
          "validFrom": "2026-01-01",
          "validTo": null,
          "confidence": "verified",
          "applicability": { "all": [] },
          "obligation": { "name": "Gated", "documentType": "license", "documentSubType": "test-gated" },
          "cadence": { "kind": "one-time", "anchor": "documentExpiration", "gracePeriodDays": 0 },
          "citation": { "section": "Synthetic Gated §1" },
          "rationale": "Synthetic gated rule.",
          "userAction": "Ask the vendor."
        },
        {
          "version": 2,
          "validFrom": "2026-02-01",
          "validTo": null,
          "confidence": "probable",
          "applicability": { "all": [] },
          "obligation": { "name": "Gated v2 (probable)", "documentType": "license", "documentSubType": "test-gated" },
          "cadence": { "kind": "one-time", "anchor": "documentExpiration", "gracePeriodDays": 0 },
          "citation": { "section": "Synthetic Gated §1 (draft)" },
          "rationale": "Synthetic gated probable version.",
          "userAction": "Ask the vendor."
        }]
      }]
    }
    """;

    [Fact]
    public void A_review_gated_file_is_excluded_by_default_and_included_only_on_request()
    {
        var byDefault = LoadInline(GatedFixture);
        byDefault.Rules.Should().BeEmpty("the default posture excludes review-gated rule-sets (A-5/CC-8)");

        var withGate = LoadInline(GatedFixture, new RuleLoadOptions(VerifiedOnly: true, IncludeReviewGated: true));
        withGate.Rules.Should().ContainSingle().Which.Id.Should().Be("test-gated-license");
    }

    [Fact]
    public void A_probable_version_inside_a_gated_file_is_still_dropped_when_the_gate_is_lifted()
    {
        // Gate filter and confidence filter compose: lifting the gate must not leak probable versions.
        var withGate = LoadInline(GatedFixture, new RuleLoadOptions(VerifiedOnly: true, IncludeReviewGated: true));

        var versions = withGate.Rules.Single().Versions;
        versions.Should().ContainSingle("the probable v2 is dropped by VerifiedOnly");
        versions[0].Confidence.Should().Be(RuleConfidence.Verified);
    }

    [Fact]
    public void The_default_load_options_are_the_safe_posture()
    {
        var options = new RuleLoadOptions();
        options.VerifiedOnly.Should().BeTrue();
        options.IncludeReviewGated.Should().BeFalse();
    }
}
