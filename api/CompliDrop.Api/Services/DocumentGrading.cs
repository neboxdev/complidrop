using CompliDrop.Api.Entities;

namespace CompliDrop.Api.Services;

/// <summary>
/// "Did anything actually GRADE this document?" — the single definition of the NEVER-GRADED state
/// (#443, ADR 0047). A document is graded when at least one <see cref="ComplianceCheck"/> row exists
/// for it: a check row is the only artifact the engine emits when it actually measured the document
/// against a requirement, and <see cref="ComplianceCheckService.ComputeOutcome"/> writes exactly one
/// per applicable rule. Every branch that certifies nothing — no checklist assigned, an empty
/// checklist, or a checklist whose rules all govern OTHER document types — returns zero checks AND
/// clears any stale ones, so "zero check rows" is precisely "no requirement was ever measured against
/// this document".
/// <para/>
/// It exists because that state used to read as an AFFIRMATIVE verdict. `ComputeOutcome`'s
/// zero-applicable-rules branch stores <c>expiringSoon ? ExpiringSoon : Pending</c>, so a never-graded
/// document inside the 30-day expiry window was stored ExpiringSoon; the read overlay promoted even a
/// stored Pending to ExpiringSoon there; the vendor rollup counts ExpiringSoon as in-force coverage;
/// and the auditor-facing export printed "Expiring soon" beside an EMPTY "What we checked" panel. The
/// same absence of grading therefore read Pending at 31 days to expiry and ExpiringSoon at 29 — the
/// date, not the evidence, decided whether the product asserted coverage.
/// <para/>
/// Callers — the decision itself lives in ONE place,
/// <see cref="ComplianceStatusDeriver.Effective"/>, which takes the answer as its <c>isGraded</c>
/// argument and demotes an affirmative verdict to Pending without it. This class only answers the
/// raw question, and it deliberately ships ONE shape: <see cref="IsGraded(int)"/>, in memory, from a
/// loaded or projected check count. Used by <c>DocumentEndpoints</c> (list projection + detail),
/// <c>VendorEndpoints</c> (both <c>DocCoverageInfo</c> projections) and <c>ExportService</c> (all
/// three artifacts).
/// <para/>
/// There is deliberately NO EF <c>Expression</c> mirror here. Every SQL read site needs the fact
/// INSIDE a composite predicate or a projection (the documents-list status arms, the dashboard
/// counts), where an EF expression cannot be invoked, so each spells it inline as
/// <c>d.ComplianceChecks.Any()</c> — the same hand-mirroring ADR 0041's future-effective bound
/// already requires, and covered the same way: by cross-surface tests pinning each SQL arm against
/// the in-memory deriver, plus <c>NeverGradedCoverageTests</c>'
/// <c>The_SQL_grading_predicate_agrees_with_the_in_memory_one_and_with_the_check_rows</c>, which
/// compares both shipping forms against the <c>ComplianceChecks</c> table itself. An
/// <c>Expression</c> property with no production caller would be a third form nothing exercises.
/// <see cref="ComplianceCheck"/> carries no <c>DeletedAt</c> and has no query filter, so
/// <c>Any()</c> counts exactly the rows <see cref="IsGraded(int)"/> counts. Same shared-predicate
/// shape as <see cref="DocumentSupersession"/>, <see cref="PlanDocumentScope"/> and
/// <see cref="DocumentFieldReadability"/>.
/// </summary>
public static class DocumentGrading
{
    /// <summary>
    /// True when at least one requirement was actually measured against the document. The one
    /// threshold: a single check row is enough — a document graded against one rule HAS been graded,
    /// pass or fail.
    /// </summary>
    public static bool IsGraded(int complianceCheckCount) => complianceCheckCount > 0;
}
