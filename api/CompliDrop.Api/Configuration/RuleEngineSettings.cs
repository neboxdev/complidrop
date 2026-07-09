namespace CompliDrop.Api.Configuration;

/// <summary>
/// Feature flags for the regulatory rule engine (SCHEMA §6: "feature flag per rule-set file so rollout is
/// per-rule-set after founder sign-off"). Both default OFF/empty — the engine ships inert.
///
/// Deliberately NOT configurable: <c>VerifiedOnly</c> (always true — a probable rule can never ship via
/// config) and <c>IncludeReviewGated</c> (always false — a review gate is lifted by REMOVING the
/// <c>reviewGate</c> marker from the rule file in a reviewed PR after the founder confirms, never by a
/// config flip). See RULES-REVIEW.md gates G1/G2.
/// </summary>
public class RuleEngineSettings
{
    public const string SectionName = "RuleEngine";

    /// <summary>Master switch. False (the default) = the engine loads nothing and evaluates nothing.
    /// Stays false until counsel clears the user-facing framing (gate G1).</summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// The rule-set FILES to load, as "jurisdiction/entity" keys matching
    /// <c>api/CompliDrop.Api/RuleData/&lt;jurisdiction&gt;/&lt;entity&gt;.json</c> — e.g.
    /// "us-fed/caterer", "us-tx/caterer", "us-tx/cross-cutting". Empty (the default) = none.
    /// Selections must be coherent: a state file whose rules declare <c>satisfiesFederal</c> requires its
    /// federal counterpart in the same selection — the fail-fast loader rejects a dangling reference at boot.
    /// </summary>
    public string[] EnabledRuleSets { get; set; } = [];
}
