namespace CompliDrop.Api.Configuration;

/// <summary>
/// Feature flag for the #396 corrected additional-insured CLAIM wording — counsel-gate item CLM-1
/// (docs/rule-engine/G1-COUNSEL-BRIEF.md §0, TRR §3). Defaults OFF: the corrected copy ships merged
/// but INERT, pending a licensed Texas attorney's sign-off. Mirrors the inert-until-cleared posture
/// of <see cref="RuleEngineSettings"/> / <see cref="TemplateCorrectionsSettings"/>.
///
/// It is DELIBERATELY DISTINCT from <see cref="TemplateCorrectionsSettings"/>: CLM-1 unlocks on a
/// different sign-off (the additional-insured claim wording — an attorney/liability question) than
/// TPL-A/B (the corrected dollar minimums — a broker/attorney question), so the two must be flippable
/// independently. Do not fold them together.
///
/// OFF (the default): the additional-insured UI sentence stays the categorical
/// "Names '{name}' as additional insured", the failure copy stays "'{name}' was not found as an
/// additional insured.", and the affirmative-flag (ACORD checkbox fallback) check NOTE keeps its
/// pre-#396 wording — so a flag-off deploy is behaviorally identical to pre-#396 production, pinned by
/// test. Flipping to true swaps ONLY those copy strings for the honest "a certificate only INDICATES
/// additional-insured status; it does not GRANT coverage — request the endorsement" framing. It NEVER
/// changes a pass/fail verdict — copy only. See ADR 0042.
/// </summary>
public class ComplianceClaimsSettings
{
    public const string SectionName = "ComplianceClaims";

    /// <summary>Master switch. False (the default) = the legacy pre-#396 additional-insured claim
    /// copy + check note stays active. Stays false until a TX attorney signs off on CLM-1
    /// (G1-COUNSEL-BRIEF §0). Copy-only when flipped — never a verdict change.</summary>
    public bool CorrectedAdditionalInsuredWording { get; set; }
}
