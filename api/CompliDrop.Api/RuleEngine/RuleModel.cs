using System.Text.Json.Serialization;

namespace CompliDrop.Api.RuleEngine;

// The rule DATA MODEL — POCOs matching SCHEMA.md §2 (rule document shape), §5 (cadence) and §3
// (federal/state layering via satisfiesFederal). Deserialized from the versioned JSON rule files
// with System.Text.Json. Everything here is inert data: the mechanics live in the evaluators.
//
// Frozen-schema note: this mirrors docs/rule-engine/SCHEMA.md v1 (FROZEN 2026-07-07). A change to a
// rule shape or a frozen fact/enum token is a schemaVersion bump + a note there, not a silent edit.

/// <summary>Comparison operators for an applicability leaf (SCHEMA §4). A closed set.</summary>
public enum ConditionOp
{
    Eq,
    Neq,
    Gte,
    Lte,
    In
}

/// <summary>Cadence kind (SCHEMA §5).</summary>
[JsonConverter(typeof(CadenceKindJsonConverter))]
public enum CadenceKind
{
    Renewal,
    FixedAnnual,
    OneTime,
    ConditionalFiling
}

/// <summary>What the next-due date is measured from (SCHEMA §5, plus the fixedDate anchor).</summary>
[JsonConverter(typeof(CadenceAnchorJsonConverter))]
public enum CadenceAnchor
{
    DocumentExpiration,
    IssueDate,
    CalendarDate,
    FixedDate
}

/// <summary>
/// Per-version confidence (SCHEMA §2 / §6). Only <see cref="Verified"/> loads in production; the
/// others are visible only behind the review flag. <see cref="RuleLoadOptions.VerifiedOnly"/> enforces this.
/// </summary>
[JsonConverter(typeof(RuleConfidenceJsonConverter))]
public enum RuleConfidence
{
    Verified,
    Probable,
    Uncertain
}

/// <summary>A month/day pair for a fixed-annual cadence (SCHEMA §5 <c>fixedDate</c>, e.g. franchise tax May 15).</summary>
public sealed record MonthDay(int Month, int Day);

/// <summary>The structural shape of a statutory insurance floor — exactly as the statute states it (v1.2).</summary>
[JsonConverter(typeof(InsuranceFloorKindJsonConverter))]
public enum InsuranceFloorKind
{
    /// <summary>One combined single limit per occurrence (e.g. §2151.1012's "$1 million per occurrence" CSL).</summary>
    CombinedSingleLimit,

    /// <summary>Split per-occurrence limits, optionally with a statutory aggregate (e.g. §1702.124(c)'s three limits).</summary>
    SplitLimits
}

/// <summary>Which policy line the statutory floor applies to (v1.2). Drives whether the extracted
/// general-liability limit is a valid comparison input — an auto-liability floor must NEVER be compared
/// against a document's GENERAL-liability limit (wrong policy line).</summary>
[JsonConverter(typeof(InsuranceCoverageLineJsonConverter))]
public enum InsuranceCoverageLine
{
    GeneralLiability,
    AutoLiability
}

/// <summary>
/// Statutory insurance floor for <c>category = insurance</c> rules (SCHEMA §2, reshaped v1.2 to represent
/// exactly what each statute states — no field carries a figure the cited section does not contain).
/// <see cref="Aggregate"/> is null when the statute states no aggregate (e.g. §2151.1012).
/// </summary>
public sealed record InsuranceMinimums
{
    /// <summary>NULLABLE so an omitted <c>kind</c> is detectable (same fail-safe rationale as confidence); the loader requires it.</summary>
    public InsuranceFloorKind? Kind { get; init; }

    /// <summary>NULLABLE so an omitted <c>coverageLine</c> can never silently default into the general-liability
    /// comparison path (member 0) — the loader requires it. The evaluator compares ONLY an explicit general-liability line.</summary>
    public InsuranceCoverageLine? CoverageLine { get; init; }

    /// <summary>The combined single limit per occurrence. Set iff <see cref="Kind"/> is <see cref="InsuranceFloorKind.CombinedSingleLimit"/>.</summary>
    public decimal? PerOccurrence { get; init; }

    /// <summary>Split limit: per-occurrence bodily injury + property damage (e.g. §1702.124(c)(1)). Set iff <see cref="Kind"/> is <see cref="InsuranceFloorKind.SplitLimits"/>.</summary>
    public decimal? PerOccurrenceBodilyInjuryAndPropertyDamage { get; init; }

    /// <summary>Split limit: per-occurrence personal injury (e.g. §1702.124(c)(2)). Only when the statute states one.</summary>
    public decimal? PerOccurrencePersonalInjury { get; init; }

    /// <summary>Statutory aggregate (e.g. §1702.124(c)(3)'s $200,000). NULL when the statute states none — never fabricated.</summary>
    public decimal? Aggregate { get; init; }

    public string Currency { get; init; } = "USD";

    /// <summary>The single-number floor an extracted per-occurrence liability limit is compared against.</summary>
    [JsonIgnore]
    public decimal ComparableFloor => Kind == InsuranceFloorKind.CombinedSingleLimit
        ? PerOccurrence!.Value
        : PerOccurrenceBodilyInjuryAndPropertyDamage!.Value;

    /// <summary>
    /// True when meeting <see cref="ComparableFloor"/> verifies the WHOLE statutory floor from one extracted
    /// number. False for split limits / statutory aggregates: sub-limits can't be read from a single
    /// extracted general-liability figure, so the engine must not certify full adequacy.
    /// </summary>
    [JsonIgnore]
    public bool FullyVerifiableFromSingleLimit => Kind == InsuranceFloorKind.CombinedSingleLimit && Aggregate is null;
}

/// <summary>Legal citation for a rule version (SCHEMA §2).</summary>
public sealed record Citation
{
    public string Section { get; init; } = "";
    public string? Url { get; init; }
    public string? EffectiveDateOfText { get; init; }
    public string? VerifiedDate { get; init; }
}

/// <summary>
/// The concrete obligation a rule version imposes (SCHEMA §2 <c>obligation</c>). <see cref="DocumentType"/>
/// maps onto the existing <c>Document.DocumentType</c> vocabulary (coi|license|certification|other, RD-c);
/// <see cref="DocumentSubType"/> carries the specificity a tracked document is matched on.
/// </summary>
public sealed record Obligation
{
    public string Name { get; init; } = "";
    public string DocumentType { get; init; } = "";
    public string? DocumentSubType { get; init; }
    public string? Authority { get; init; }

    /// <summary>true for worker-certification rules (RD-b): drives "each worker performing X must hold …" copy.</summary>
    public bool PerWorker { get; init; }
}

/// <summary>Cadence &amp; deadline block (SCHEMA §5). All arithmetic is date-only — see <see cref="CadenceCalculator"/>.</summary>
public sealed record Cadence
{
    public CadenceKind Kind { get; init; }
    public int? PeriodMonths { get; init; }
    public CadenceAnchor Anchor { get; init; }
    public MonthDay? FixedDate { get; init; }
    public int GracePeriodDays { get; init; }

    /// <summary>
    /// When true, a period-based due date rounds to the LAST day of the due month (v1.2). Encodes
    /// "calendar months" recency language (e.g. 14 CFR 107.65's "previous 24 calendar months" runs to the
    /// end of the 24th month, not the same-day anniversary). Requires <see cref="PeriodMonths"/>.
    /// </summary>
    public bool RoundToMonthEnd { get; init; }

    /// <summary>Grace periods must be independently sourced (SCHEMA §5); null when <see cref="GracePeriodDays"/> is 0.</summary>
    public string? GraceCitation { get; init; }
}

/// <summary>
/// One append-only version of a rule (SCHEMA §1/§2). The engine evaluates the version whose
/// [<see cref="ValidFrom"/>, <see cref="ValidTo"/>] window contains the evaluation date. <see cref="ValidTo"/>
/// is INCLUSIVE (the last valid day); null = open-ended.
/// </summary>
public sealed record RuleVersion
{
    public int Version { get; init; }
    public DateOnly ValidFrom { get; init; }
    public DateOnly? ValidTo { get; init; }

    /// <summary>
    /// NULLABLE so an omitted <c>confidence</c> key is DETECTABLE: a non-nullable enum would silently
    /// default to <see cref="RuleConfidence.Verified"/> (member 0) and ship an unreviewed rule in the
    /// verified-only production posture. The loader rejects null (fail-safe direction).
    /// </summary>
    public RuleConfidence? Confidence { get; init; }

    /// <summary>The condition tree (SCHEMA §4). Evaluated with three-valued Kleene logic.</summary>
    public Applicability Applicability { get; init; } = null!;

    public Obligation Obligation { get; init; } = null!;
    public Cadence? Cadence { get; init; }
    public InsuranceMinimums? InsuranceMinimums { get; init; }
    public Citation? Citation { get; init; }

    /// <summary>User-facing (SCHEMA §2): states what the law requires and cites it; never a compliance adjudication.</summary>
    public string Rationale { get; init; } = "";
    public string UserAction { get; init; } = "";
    public string? Notes { get; init; }

    /// <summary>
    /// Federal rule ids this (state) version implements (SCHEMA §3). A federal rule listed here by an
    /// APPLICABLE state rule is suppressed from output — the credential is issued once at the state level.
    /// </summary>
    public IReadOnlyList<string> SatisfiesFederal { get; init; } = [];
}

/// <summary>A rule identified by a stable slug, carrying an array of append-only versions (SCHEMA §1/§2).</summary>
public sealed record Rule
{
    public string Id { get; init; } = "";
    public string ObligationRef { get; init; } = "";

    /// <summary>"us-fed" | "us-tx" | … (SCHEMA §3). Federal rules layer additively over state rules.</summary>
    public string Jurisdiction { get; init; } = "";

    /// <summary>Which entity types this rule applies to; empty = all. A structural pre-filter (SCHEMA §2).</summary>
    public IReadOnlyList<string> EntityTypes { get; init; } = [];

    /// <summary>
    /// The <c>localObligations</c> of the rule-set FILE this rule was loaded from (CC-7), denormalized onto
    /// the rule during the merge so the evaluator can union the pointers of every rule-set that applied to an
    /// entity. Not part of the per-rule JSON — it is copied from the file-level <see cref="RuleSet.LocalObligations"/>.
    /// </summary>
    [JsonIgnore]
    public IReadOnlyList<string> LocalObligations { get; init; } = [];

    /// <summary>license|permit|worker-certification|insurance|filing (SCHEMA §2).</summary>
    public string Category { get; init; } = "";

    /// <summary>"regulatory" — only regulatory rules ship; contractual stays in per-org checklists (SCHEMA §2).</summary>
    public string Basis { get; init; } = "regulatory";

    public IReadOnlyList<RuleVersion> Versions { get; init; } = [];
}

/// <summary>
/// A loaded, validated set of rules (SCHEMA §1). Produced by <see cref="RuleSetLoader"/>. When it merges
/// several files (e.g. us-fed + us-tx for one entity type) their rules combine here so federal/state
/// layering and satisfiesFederal suppression resolve against the whole set.
/// </summary>
public sealed record RuleSet
{
    public int SchemaVersion { get; init; } = 1;
    public IReadOnlyList<Rule> Rules { get; init; } = [];

    /// <summary>
    /// Optional rule-set-level review gate (A-5/CC-8). When a FILE sets this (e.g. the TX security set's
    /// <c>"founder-confirm-tx-security"</c>), its rules are held OUT of the production load — independent of
    /// per-rule confidence — unless <see cref="RuleLoadOptions.IncludeReviewGated"/> is set. Null = ungated.
    /// A merged rule set (several files) does not carry a single gate; this is a per-file authoring marker.
    /// </summary>
    public string? ReviewGate { get; init; }

    /// <summary>
    /// Optional rule-set-level "noted, not encoded" LOCAL obligations (CC-7) — short pointers (e.g.
    /// "City/county tent permit (IFC Ch. 31)") sourced from the entity's dossier. The evaluator unions the
    /// pointers of every rule-set that applied to an entity into <see cref="CompletenessNotice.LocalObligationPointers"/>.
    /// </summary>
    public IReadOnlyList<string> LocalObligations { get; init; } = [];

    public static RuleSet Empty { get; } = new();
}
