namespace CompliDrop.Api.Configuration;

/// <summary>
/// Feature flag for the #416 corrected system-checklist set (TEMPLATE-REQUIREMENTS-REVIEW.md §4,
/// findings TPL-A1..A4 / TPL-B1..B3) and its cross-org re-grade. Defaults OFF — the correction
/// ships merged but INVISIBLE, pending the legal/insurance sign-off
/// (docs/rule-engine/G1-COUNSEL-BRIEF.md §0). Mirrors the inert-until-cleared posture of
/// <see cref="RuleEngineSettings"/>.
///
/// OFF (the default): <c>ComplianceTemplateSeed.EnsureAsync</c> converges the system templates to
/// the LEGACY (pre-#416) set — a byte-level no-op against a production database seeded by main's
/// insert-only seeder — and <c>/api/auth/me</c> reports <c>features.correctedChecklists = false</c>,
/// so the SPA hides the gated surfaces (the liquor "+ Add a requirement" menu option and the
/// additional-insured nudge). The deployed product stays behaviorally identical to pre-#416 prod.
///
/// Flipping to true converges production to the §4 corrected set on the next boot and fires the
/// durable watermarked cross-org re-grade (ADR 0036 + Amendments 1–2); flipping back to false
/// converges to the legacy set again through the same convergence + watermark machinery — the flag
/// is REVERSIBLE in both directions. See ADR 0036 Amendment 3.
/// </summary>
public class TemplateCorrectionsSettings
{
    public const string SectionName = "TemplateCorrections";

    /// <summary>Master switch. False (the default) = the legacy (pre-#416) system-checklist set
    /// stays active and the gated UI stays hidden. Stays false until an attorney/broker signs off
    /// on the corrected set (G1-COUNSEL-BRIEF §0).</summary>
    public bool Enabled { get; set; }
}
