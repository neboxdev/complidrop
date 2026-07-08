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
    public void All_ten_jurisdiction_entity_files_are_embedded()
    {
        // 6 entity types across (us-fed + us-tx), minus the (jurisdiction, entity) pairs with no
        // encodable obligation: no us-fed/security-service and no us-tx/photographer-videographer.
        EmbeddedRuleData.ResourceNames.Should().HaveCount(10);
    }

    [Fact]
    public void The_full_and_production_sets_have_the_expected_rule_counts()
    {
        var all = EmbeddedRuleData.LoadAll(FullSet);
        var prod = EmbeddedRuleData.LoadAll(); // DEFAULT = production posture: verified-only + review-gated excluded
        var verifiedInclGated = EmbeddedRuleData.LoadAll(new RuleLoadOptions(VerifiedOnly: true, IncludeReviewGated: true));

        all.Rules.Should().HaveCount(39, "36 verified + 3 probable obligations are encoded");
        verifiedInclGated.Rules.Should().HaveCount(36, "the 3 probable rules do not ship in the verified posture");

        // Production DEFAULT now also holds back the review-gated TX security rule-set (A-5/CC-8): 39 minus
        // the 3 probable minus the 5 verified TX-security rules = 31.
        prod.Rules.Should().HaveCount(31, "minus 3 probable and minus the 5 review-gated TX security rules");
        prod.Rules.Should().OnlyContain(
            r => r.Versions.All(v => v.Confidence == RuleConfidence.Verified),
            "the default load keeps only verified versions");
        prod.Rules.Should().NotContain(r => r.EntityTypes.Contains("security-service"),
            "the review-gated TX security rule-set is excluded from the default production load");
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
    public void Insurance_minimums_appear_only_on_insurance_rules_and_are_positive()
    {
        var all = EmbeddedRuleData.LoadAll(FullSet);

        foreach (var rule in all.Rules)
        foreach (var version in rule.Versions)
        {
            if (version.InsuranceMinimums is null) continue;
            rule.Category.Should().Be("insurance", $"{rule.Id} carries insuranceMinimums");
            version.InsuranceMinimums.PerOccurrence.Should().BeGreaterThan(0);
            version.InsuranceMinimums.Currency.Should().Be("USD");
        }
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
    public void Only_the_tx_security_rule_set_is_review_gated()
    {
        // A-5/CC-8: the whole TX security set is held out of the default load by a rule-set reviewGate,
        // independent of confidence. Confirm exactly that set is affected and nothing else.
        var prod = EmbeddedRuleData.LoadAll(); // default: gated set excluded
        var withGated = EmbeddedRuleData.LoadAll(new RuleLoadOptions(VerifiedOnly: true, IncludeReviewGated: true));

        var onlyWhenGatedIncluded = withGated.Rules.Select(r => r.Id)
            .Except(prod.Rules.Select(r => r.Id))
            .ToList();

        onlyWhenGatedIncluded.Should().OnlyContain(id => id.StartsWith("tx-security-"),
            "the review gate holds back the TX security rule-set and nothing else");
        onlyWhenGatedIncluded.Should().HaveCount(5, "all 5 TX security rules are gated");
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
