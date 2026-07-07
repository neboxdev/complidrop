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
    [Fact]
    public void Every_embedded_rule_data_file_loads_and_validates()
    {
        // A single dangling satisfiesFederal ref, unknown fact, or bad date anywhere in the shipped data
        // would throw here — the whole set is validated as one merged unit (SCHEMA §1-§5, §3).
        var act = () => EmbeddedRuleData.LoadAll();
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
    public void The_full_and_verified_only_sets_have_the_expected_rule_counts()
    {
        var all = EmbeddedRuleData.LoadAll();
        var verifiedOnly = EmbeddedRuleData.LoadAll(new RuleLoadOptions(VerifiedOnly: true));

        all.Rules.Should().HaveCount(39, "36 verified + 3 probable obligations are encoded");
        verifiedOnly.Rules.Should().HaveCount(36, "the 3 probable rules do not ship in the production posture");
        verifiedOnly.Rules.Should().OnlyContain(
            r => r.Versions.All(v => v.Confidence == RuleConfidence.Verified),
            "the verified-only load keeps only verified versions");
    }

    [Fact]
    public void Every_rule_carries_a_mandatory_obligation_ref_and_regulatory_basis()
    {
        var all = EmbeddedRuleData.LoadAll();

        all.Rules.Should().OnlyContain(r => !string.IsNullOrWhiteSpace(r.ObligationRef));
        all.Rules.Should().OnlyContain(r => r.Basis == "regulatory");
        all.Rules.Should().OnlyContain(r => r.Jurisdiction == "us-fed" || r.Jurisdiction == "us-tx");
    }

    [Fact]
    public void Satisfies_federal_references_resolve_against_loaded_federal_rules()
    {
        // Full set (incl. probable) so the TX intrastate medical certificate's satisfiesFederal is checked
        // too; LoadAll would already have thrown on a dangling ref, this documents the linkage explicitly.
        var all = EmbeddedRuleData.LoadAll();
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
        var all = EmbeddedRuleData.LoadAll();

        foreach (var rule in all.Rules)
        foreach (var version in rule.Versions)
        {
            if (version.InsuranceMinimums is null) continue;
            rule.Category.Should().Be("insurance", $"{rule.Id} carries insuranceMinimums");
            version.InsuranceMinimums.PerOccurrence.Should().BeGreaterThan(0);
            version.InsuranceMinimums.Currency.Should().Be("USD");
        }
    }
}
