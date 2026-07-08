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

/// <summary>Statutory insurance floor for <c>category = insurance</c> rules (SCHEMA §2).</summary>
public sealed record InsuranceMinimums
{
    public decimal PerOccurrence { get; init; }
    public decimal Aggregate { get; init; }
    public string Currency { get; init; } = "USD";
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
    public RuleConfidence Confidence { get; init; }

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
