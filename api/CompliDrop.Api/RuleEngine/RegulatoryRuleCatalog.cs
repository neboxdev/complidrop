using CompliDrop.Api.Configuration;

namespace CompliDrop.Api.RuleEngine;

/// <summary>
/// The application's loaded regulatory rule set, resolved ONCE at boot from
/// <see cref="RuleEngineSettings"/> (the per-rule-set feature flags, SCHEMA §6). With the engine disabled
/// (the shipping default) this is <see cref="Disabled"/> — nothing is loaded and nothing evaluates. With it
/// enabled, the selected rule-set files load through the fail-fast loader in the HARD production posture:
/// <c>VerifiedOnly = true</c> and <c>IncludeReviewGated = false</c>, non-configurable — a probable or
/// review-gated rule can never reach a customer via config (A-5/CC-8; gates G1/G2 in RULES-REVIEW.md).
/// </summary>
public sealed class RegulatoryRuleCatalog
{
    /// <summary>The engine-off catalog: no rules, evaluates nothing.</summary>
    public static RegulatoryRuleCatalog Disabled { get; } = new(false, RuleSet.Empty, []);

    public bool Enabled { get; }
    public RuleSet RuleSet { get; }
    public IReadOnlyList<string> EnabledRuleSets { get; }

    private RegulatoryRuleCatalog(bool enabled, RuleSet ruleSet, IReadOnlyList<string> enabledRuleSets)
    {
        Enabled = enabled;
        RuleSet = ruleSet;
        EnabledRuleSets = enabledRuleSets;
    }

    /// <summary>
    /// Resolves the catalog from settings. Throws <see cref="RuleSchemaException"/> on an unknown rule-set
    /// key or invalid rule data — when the engine is ENABLED a bad configuration must stop the boot
    /// (mirroring the migration drift guard), never silently mis-evaluate obligations.
    /// </summary>
    public static RegulatoryRuleCatalog Create(RuleEngineSettings settings)
    {
        if (!settings.Enabled)
            return Disabled;

        var keys = settings.EnabledRuleSets
            .Where(k => !string.IsNullOrWhiteSpace(k))
            .Select(k => k.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        // The safe posture is hard-coded: verified-only, review-gated excluded. Not configurable.
        var ruleSet = EmbeddedRuleData.LoadSelected(keys, new RuleLoadOptions(VerifiedOnly: true, IncludeReviewGated: false));
        return new RegulatoryRuleCatalog(true, ruleSet, keys);
    }
}
