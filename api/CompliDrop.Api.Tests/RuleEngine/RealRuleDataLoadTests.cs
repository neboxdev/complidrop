using CompliDrop.Api.RuleEngine;
using FluentAssertions;

namespace CompliDrop.Api.Tests.RuleEngine;

/// <summary>
/// Loads EVERY REAL RuleData file (api/CompliDrop.Api/RuleData/**) through the same fail-fast
/// <see cref="RuleSetLoader"/> the boot path uses, and asserts the SHIPPED rule content is well-formed:
/// no unknown fact (frozen §4 registry), no dangling <c>satisfiesFederal</c> reference, no bad date or
/// out-of-vocabulary category/documentType/jurisdiction. This is the data counterpart to the synthetic
/// <see cref="RuleSetLoaderTests"/> — those prove the MECHANICS reject bad input; this proves the actual
/// encoded obligations pass.
/// </summary>
public class RealRuleDataLoadTests
{
    // The FULL shipped set: every rule regardless of confidence or review gate. Validation runs over the
    // full merged set before any filtering, so this is what proves ALL shipped rules satisfy the schema.
    private static readonly RuleLoadOptions FullSet = new(VerifiedOnly: false, IncludeReviewGated: true);

    [Fact]
    public void Every_embedded_rule_data_file_loads_and_validates()
    {
        // A single dangling satisfiesFederal ref, unknown fact, or bad date anywhere in the shipped data
        // would throw here — the whole set is validated as one merged unit (SCHEMA §1-§5, §3).
        var act = () => EmbeddedRuleData.LoadAll(FullSet);
        act.Should().NotThrow("every shipped RuleData file must satisfy the frozen schema");
    }

    [Fact]
    public void All_eleven_rule_data_files_are_embedded()
    {
        // 6 entity types across (us-fed + us-tx), minus the (jurisdiction, entity) pairs with no
        // encodable obligation (no us-fed/security-service, no us-tx/photographer-videographer),
        // plus us-tx/cross-cutting.json (the multi-entity sales-tax rule, CONF-17).
        EmbeddedRuleData.ResourceNames.Should().HaveCount(11);
    }

    [Fact]
    public void The_full_and_production_sets_have_the_expected_rule_counts()
    {
        var all = EmbeddedRuleData.LoadAll(FullSet);
        var prod = EmbeddedRuleData.LoadAll(); // DEFAULT = production posture: verified-only + review-gated excluded
        var verifiedInclGated = EmbeddedRuleData.LoadAll(new RuleLoadOptions(VerifiedOnly: true, IncludeReviewGated: true));

        all.Rules.Should().HaveCount(40, "37 verified + 3 probable obligations are encoded (the 40th is the Pass-5 tx-venue-wc-coverage-notice, SPLIT-3)");
        verifiedInclGated.Rules.Should().HaveCount(37, "the 3 probable rules do not ship in the verified posture");

        // The TX security reviewGate (G2) was lifted 2026-07-09 after the delegated official-host
        // confirmation (docs/rule-engine/audit/evidence/g2/), so the production posture is now the full
        // verified set: 40 minus the 3 probable = 37.
        prod.Rules.Should().HaveCount(37, "minus the 3 probable rules; no rule set is review-gated since G2 closed");
        prod.Rules.Should().OnlyContain(
            r => r.Versions.All(v => v.Confidence == RuleConfidence.Verified),
            "the default load keeps only verified versions");
        prod.Rules.Should().Contain(r => r.EntityTypes.Contains("security-service"),
            "the TX security rule-set ships since its G2 gate closed (2026-07-09)");
    }

    [Fact]
    public void Every_rule_carries_a_mandatory_obligation_ref_and_regulatory_basis()
    {
        var all = EmbeddedRuleData.LoadAll(FullSet);

        all.Rules.Should().OnlyContain(r => !string.IsNullOrWhiteSpace(r.ObligationRef));
        all.Rules.Should().OnlyContain(r => r.Basis == "regulatory");
        all.Rules.Should().OnlyContain(r => r.Jurisdiction == "us-fed" || r.Jurisdiction == "us-tx");
    }

    [Fact]
    public void Satisfies_federal_references_resolve_against_loaded_federal_rules()
    {
        // Full set (incl. probable + gated) so the TX intrastate medical certificate's satisfiesFederal is
        // checked too; LoadAll would already have thrown on a dangling ref, this documents it explicitly.
        var all = EmbeddedRuleData.LoadAll(FullSet);
        var federalIds = all.Rules.Where(r => r.Jurisdiction == "us-fed").Select(r => r.Id).ToHashSet();

        var refs = all.Rules
            .SelectMany(r => r.Versions)
            .SelectMany(v => v.SatisfiesFederal)
            .Distinct()
            .ToList();

        refs.Should().NotBeEmpty();
        refs.Should().OnlyContain(id => federalIds.Contains(id));
        refs.Should().Contain("fed-transportation-cdl-passenger-endorsement",
            "the Texas CDL implements the federal CDL floor so it isn't double-emitted");
    }

    [Fact]
    public void Insurance_minimums_appear_exactly_on_insurance_rules_with_a_declared_shape()
    {
        // BOTH directions (UNVER-21): minimums only on insurance rules AND every insurance rule carries them.
        var all = EmbeddedRuleData.LoadAll(FullSet);

        foreach (var rule in all.Rules)
        foreach (var version in rule.Versions)
        {
            if (rule.Category == "insurance")
            {
                version.InsuranceMinimums.Should().NotBeNull($"{rule.Id} is an insurance rule — the statutory floor is its point");
                version.InsuranceMinimums!.Kind.Should().NotBeNull($"{rule.Id} must declare the floor's statutory shape");
                version.InsuranceMinimums.CoverageLine.Should().NotBeNull($"{rule.Id} must declare which policy line the floor binds");
                version.InsuranceMinimums.Currency.Should().Be("USD");
            }
            else
            {
                version.InsuranceMinimums.Should().BeNull($"{rule.Id} is not an insurance rule");
            }
        }
    }

    [Fact]
    public void Every_encoded_insurance_floor_is_pinned_to_its_statutory_figures()
    {
        // UNVER-25: pin ALL six floors numerically (incl. the review-gated security set) so a data edit
        // can never silently shift a statutory dollar amount. Each figure traces to the dossier's
        // verbatim Operative text and was re-verified against live primary sources 2026-07-08.
        var all = EmbeddedRuleData.LoadAll(new RuleLoadOptions(VerifiedOnly: true, IncludeReviewGated: true));
        InsuranceMinimums Of(string id) => all.Rules.Single(r => r.Id == id).Versions[0].InsuranceMinimums!;

        // 49 CFR 387.33T Schedule of Limits rows (a)/(b) — auto-liability, no statutory aggregate.
        var fed16 = Of("fed-transportation-financial-responsibility-16plus");
        fed16.Kind.Should().Be(InsuranceFloorKind.CombinedSingleLimit);
        fed16.CoverageLine.Should().Be(InsuranceCoverageLine.AutoLiability);
        fed16.PerOccurrence.Should().Be(5_000_000m);
        fed16.Aggregate.Should().BeNull("§387.33T states no aggregate");

        var fed15 = Of("fed-transportation-financial-responsibility-15orless");
        fed15.PerOccurrence.Should().Be(1_500_000m);
        fed15.CoverageLine.Should().Be(InsuranceCoverageLine.AutoLiability);

        // 43 TAC 218.16(a) intrastate tiers — auto-liability, no statutory aggregate.
        Of("tx-transportation-intrastate-insurance-27plus").PerOccurrence.Should().Be(5_000_000m);
        Of("tx-transportation-intrastate-insurance-16to26").PerOccurrence.Should().Be(500_000m);

        // Tex. Occ. Code 2151.1012 — $1M per-occurrence CSL, NO statutory aggregate (CONF-16).
        var inflatable = Of("tx-event-rental-amusement-ride-insurance");
        inflatable.Kind.Should().Be(InsuranceFloorKind.CombinedSingleLimit);
        inflatable.CoverageLine.Should().Be(InsuranceCoverageLine.GeneralLiability);
        inflatable.PerOccurrence.Should().Be(1_000_000m);
        inflatable.Aggregate.Should().BeNull("§2151.1012 states no aggregate — a mirrored value would be a fabricated figure");

        // Tex. Occ. Code 1702.124(c) — the three statutory limits, all machine-readable (v1.2 split shape).
        var securityGl = Of("tx-security-general-liability-insurance");
        securityGl.Kind.Should().Be(InsuranceFloorKind.SplitLimits);
        securityGl.CoverageLine.Should().Be(InsuranceCoverageLine.GeneralLiability);
        securityGl.PerOccurrenceBodilyInjuryAndPropertyDamage.Should().Be(100_000m);
        securityGl.PerOccurrencePersonalInjury.Should().Be(50_000m);
        securityGl.Aggregate.Should().Be(200_000m);
    }

    [Fact]
    public void The_modeled_entity_types_match_the_canonical_constant()
    {
        // The engine derives its "modeled entity type" set from the rule data; pin the shipped data to the
        // canonical EntityTypes.KnownModeled constant so the two never drift (A-3/C-1).
        var all = EmbeddedRuleData.LoadAll(FullSet);

        var modeled = all.Rules.SelectMany(r => r.EntityTypes).ToHashSet(StringComparer.OrdinalIgnoreCase);

        modeled.Should().BeEquivalentTo(EntityTypes.KnownModeled, "the 6 modeled entity types are the canonical set");
    }

    [Fact]
    public void No_shipped_rule_set_is_review_gated_since_g2_closed()
    {
        // The TX security reviewGate (A-5/CC-8 → gate G2) was lifted 2026-07-09 with evidence in
        // docs/rule-engine/audit/evidence/g2/. The GATING MECHANISM itself stays covered by the synthetic
        // fixtures in RuleSetLoaderGuardTests (added for exactly this moment — UNVER-23), so this test
        // now pins that the default load equals the gated-inclusive load over the shipped data.
        var prod = EmbeddedRuleData.LoadAll();
        var withGated = EmbeddedRuleData.LoadAll(new RuleLoadOptions(VerifiedOnly: true, IncludeReviewGated: true));

        prod.Rules.Select(r => r.Id).Should().BeEquivalentTo(withGated.Rules.Select(r => r.Id),
            "no shipped rule-set file declares a reviewGate anymore");
    }

    [Fact]
    public void Local_obligation_pointers_are_populated_for_each_texas_entity()
    {
        // CC-7: every Texas entity dossier's "noted, not encoded" local obligations are carried on its
        // rule-set file and denormalized onto its rules for the evaluator to union.
        var all = EmbeddedRuleData.LoadAll(FullSet);

        foreach (var entityType in EntityTypes.KnownModeled)
        {
            var rulesForType = all.Rules.Where(r => r.EntityTypes.Contains(entityType, StringComparer.OrdinalIgnoreCase));
            rulesForType.Should().Contain(r => r.LocalObligations.Count > 0,
                $"the {entityType} rule set surfaces local (city/county) obligation pointers");
        }
    }
}
