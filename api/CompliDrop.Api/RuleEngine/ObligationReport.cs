namespace CompliDrop.Api.RuleEngine;

/// <summary>
/// Per-obligation status (SCHEMA §6). Deliberately a set of OBLIGATION states — never a legal conclusion
/// about the user's conduct (SCHEMA legal-req #3). There is no "violation" / "illegal" value.
/// </summary>
public enum ObligationStatus
{
    /// <summary>A matching document/attestation is present and current.</summary>
    Satisfied,

    /// <summary>Present but within the expiring-soon window (or a filing due soon).</summary>
    Expiring,

    /// <summary>Present but past its expiry / past due + grace.</summary>
    Expired,

    /// <summary>The obligation applies but no matching document/attestation is on record.</summary>
    Missing,

    /// <summary>
    /// A matching document IS on record but the engine can't confirm what <see cref="Satisfied"/> would
    /// assert — its currency (no readable expiry / no determinable due date on a renewing or fixed-annual
    /// obligation, A-1 / the fixed-annual cycle ambiguity) or, for an insurance obligation with a
    /// general-liability statutory floor, its coverage amount (unreadable, or a split-limit floor whose
    /// sub-limits one extracted figure cannot verify — v1.2, A-2/CC-4). Never a false
    /// <see cref="Satisfied"/> from an undeterminable fact. A held ONE-TIME credential with no expiry
    /// stays <see cref="Satisfied"/> (nothing to renew).
    /// </summary>
    NeedsDocumentInfo,

    /// <summary>
    /// The matched insurance document's extracted per-occurrence general-liability limit is LOWER than the
    /// statutory floor the rule carries (v1.2, A-2/CC-4). This is a numeric comparison of the certificate
    /// against the cited statute's stated minimum — a tracked-obligation status, not an adjudication of the
    /// entity's conduct. Only emitted for a general-liability floor (never graded across policy lines).
    /// </summary>
    BelowStatedMinimum,

    /// <summary>Applicability is Unknown — a profile question must be answered first (SCHEMA §4). See <see cref="ObligationResult.MissingFacts"/>.</summary>
    NeedsProfileInfo,

    /// <summary>The rule was considered and does not apply to this entity (applicability False).</summary>
    NotApplicable
}

/// <summary>Whether the entity's jurisdiction is covered by the loaded rule set (SCHEMA §3).</summary>
public enum JurisdictionCoverage
{
    Covered,

    /// <summary>The entity's state is not one the rule set covers — NEVER an empty "compliant" result (SCHEMA §3).</summary>
    NotCovered
}

/// <summary>
/// One obligation's result. Carries the user-facing framing (rationale / userAction / citation) copied
/// verbatim from the applicable rule version — the engine never synthesises legal claims. For a
/// <see cref="ObligationStatus.NeedsProfileInfo"/> result, <see cref="MissingFacts"/> lists the unset
/// facts to collect.
/// </summary>
public sealed record ObligationResult
{
    /// <summary>
    /// The unique rule <c>id</c> that produced this result (A-4). Unlike <see cref="ObligationRef"/> — a
    /// dossier cross-ref that several rules may share (e.g. the interstate vs intrastate insurance floors both
    /// map to OBL-…-TRANSPORTATION-003 / -002) — this is one-to-one with the emitted obligation, so a caller
    /// can key on it without a NotApplicable sibling shadowing an actionable result.
    /// </summary>
    public string RuleId { get; init; } = "";

    public required string ObligationRef { get; init; }
    public required string Name { get; init; }
    public required ObligationStatus Status { get; init; }
    public Citation? Citation { get; init; }
    public string Rationale { get; init; } = "";
    public string UserAction { get; init; } = "";

    /// <summary>Frozen fact names still needed (only populated for <see cref="ObligationStatus.NeedsProfileInfo"/>).</summary>
    public IReadOnlyList<string> MissingFacts { get; init; } = [];

    /// <summary>Next renewal / filing deadline, when determinable (SCHEMA §5).</summary>
    public DateOnly? NextDueDate { get; init; }

    /// <summary>The tracked document that satisfies/expires this obligation, when one matched.</summary>
    public string? MatchedDocumentId { get; init; }

    /// <summary>The statutory insurance floor the rule carries (insurance rules only), copied verbatim so
    /// the UI can display the stated minimums next to the status without re-loading the rule.</summary>
    public InsuranceMinimums? InsuranceMinimums { get; init; }
}

/// <summary>
/// The MANDATORY non-exhaustiveness notice attached to every report (SCHEMA legal-req #2). Its presence is
/// what makes a "bare compliant" structurally impossible: there is no overall compliant flag, only
/// per-obligation statuses plus this notice. <see cref="LocalObligationPointers"/> surfaces the
/// "noted, not encoded" local obligations (city/county health/food, fire-marshal, occupancy, per-event
/// permits) — the evaluator unions the <c>localObligations</c> metadata of the rule-sets that applied to
/// the entity into it (CC-7).
/// </summary>
public sealed record CompletenessNotice
{
    public required string Text { get; init; }
    public IReadOnlyList<string> LocalObligationPointers { get; init; } = [];

    /// <summary>
    /// The default disclaimer. Neutral scaffolding — states the report is not exhaustive and not advice;
    /// asserts nothing about any specific law. It always renders, including on an all-satisfied report.
    /// </summary>
    public const string DefaultText =
        "This report lists only the regulatory obligations CompliDrop tracks for the profile and documents " +
        "you provided. It is not a complete list of your legal obligations and is not legal advice. Local " +
        "requirements (for example city and county health, food, fire, occupancy, and per-event permits) and " +
        "other rules may also apply — check with your local authorities and a qualified professional.";

    public static CompletenessNotice Default(IReadOnlyList<string>? localObligationPointers = null) =>
        new() { Text = DefaultText, LocalObligationPointers = localObligationPointers ?? [] };
}

/// <summary>
/// The regulatory obligation report (SCHEMA §6). STRUCTURALLY enforces legal-req #2: it has NO overall
/// "isCompliant" boolean — only a list of per-obligation <see cref="ObligationResult"/>s and a mandatory,
/// non-empty <see cref="CompletenessNotice"/>. You cannot construct one without the notice, and the
/// constructor rejects an empty notice, so a caller can never render a completeness illusion. A
/// fully-met entity is represented as "every obligation Satisfied" + the notice, never a bare "compliant".
/// </summary>
public sealed class ObligationReport
{
    public JurisdictionCoverage Coverage { get; }

    /// <summary>Set only when <see cref="Coverage"/> is <see cref="JurisdictionCoverage.NotCovered"/>.</summary>
    public string? CoverageMessage { get; }

    public IReadOnlyList<ObligationResult> Obligations { get; }

    /// <summary>Always present, always non-empty text — the structural guard against a completeness illusion.</summary>
    public CompletenessNotice Completeness { get; }

    /// <summary>
    /// Union of every profile fact still needed: the missing facts across all <c>NeedsProfileInfo</c>
    /// obligations, plus any rule-selection facts (state / entityType) that were unset. A single place the
    /// UI can read "what to ask the user next".
    /// </summary>
    public IReadOnlyList<string> OutstandingProfileFacts { get; }

    private ObligationReport(
        JurisdictionCoverage coverage,
        string? coverageMessage,
        IReadOnlyList<ObligationResult> obligations,
        CompletenessNotice completeness,
        IEnumerable<string>? additionalOutstandingFacts)
    {
        ArgumentNullException.ThrowIfNull(completeness);
        if (string.IsNullOrWhiteSpace(completeness.Text))
            throw new ArgumentException("The completeness notice text must be non-empty (SCHEMA legal-req #2).", nameof(completeness));

        Coverage = coverage;
        CoverageMessage = coverageMessage;
        Obligations = obligations;
        Completeness = completeness;
        OutstandingProfileFacts = obligations
            .Where(o => o.Status == ObligationStatus.NeedsProfileInfo)
            .SelectMany(o => o.MissingFacts)
            .Concat(additionalOutstandingFacts ?? [])
            .Distinct(StringComparer.Ordinal)
            .OrderBy(f => f, StringComparer.Ordinal)
            .ToList();
    }

    public static ObligationReport ForObligations(
        IReadOnlyList<ObligationResult> obligations,
        CompletenessNotice completeness,
        IEnumerable<string>? additionalOutstandingFacts = null) =>
        new(JurisdictionCoverage.Covered, null, obligations, completeness, additionalOutstandingFacts);

    public static ObligationReport NotCovered(string message, CompletenessNotice completeness) =>
        new(JurisdictionCoverage.NotCovered, message, [], completeness, null);
}
