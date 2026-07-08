namespace CompliDrop.Api.RuleEngine;

/// <summary>
/// The minimal shape the regulatory evaluator needs from a tracked document, kept DELIBERATELY decoupled
/// from the EF <c>Document</c> entity so the engine core stays pure and DB-free (SCHEMA §6). The adapter
/// that projects a real <c>Document</c> onto this interface lives outside the engine (the persistence /
/// wiring step). Dates are <see cref="DateOnly"/> — the engine reasons in civil dates, not instants.
/// </summary>
public interface IDocumentLike
{
    /// <summary>Stable identifier, echoed back on the obligation it satisfies.</summary>
    string Id { get; }

    /// <summary>Maps to <c>Document.DocumentType</c> (coi|license|certification|other, RD-c).</summary>
    string DocumentType { get; }

    /// <summary>Maps to <c>Document.DocumentSubType</c> — the specificity an obligation matches on (RD-c).</summary>
    string? DocumentSubType { get; }

    /// <summary>Printed expiry, when the document carries one.</summary>
    DateOnly? ExpirationDate { get; }

    /// <summary>Issue date, for cadences anchored on <see cref="CadenceAnchor.IssueDate"/>. Often unset in v1.</summary>
    DateOnly? IssueDate { get; }

    /// <summary>
    /// The extracted per-occurrence GENERAL-liability limit (maps to <c>Document.GeneralLiabilityLimit</c>).
    /// Compared against a rule's <see cref="InsuranceMinimums.ComparableFloor"/> ONLY when the floor's
    /// <see cref="InsuranceMinimums.CoverageLine"/> is general-liability — an auto-liability floor must
    /// never be graded against this figure (wrong policy line). Null = amount not readable (v1.2, A-2/CC-4).
    /// </summary>
    decimal? GeneralLiabilityLimit { get; }
}

/// <summary>A plain record implementation of <see cref="IDocumentLike"/> for callers and tests.</summary>
public sealed record DocumentLike(
    string Id,
    string DocumentType,
    string? DocumentSubType = null,
    DateOnly? ExpirationDate = null,
    DateOnly? IssueDate = null,
    decimal? GeneralLiabilityLimit = null) : IDocumentLike;
