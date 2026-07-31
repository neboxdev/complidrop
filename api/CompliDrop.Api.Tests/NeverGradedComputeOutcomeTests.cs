using System.Text.Json;
using CompliDrop.Api.Entities;
using CompliDrop.Api.Services;
using FluentAssertions;

namespace CompliDrop.Api.Tests;

/// <summary>
/// Pure unit tests for the WRITE side of #443 / ADR 0048 — that the never-graded state is REACHABLE, what
/// <see cref="ComplianceCheckService.ComputeOutcome"/> stores for it, and what a reader then sees.
/// <para/>
/// The design contract, the same one ADR 0041 established: the demotion is a READ-only overlay. ComputeOutcome
/// keeps storing the real date verdict (never a manufactured Pending — writing Pending is also how the
/// extraction worker claims a document), and <see cref="ComplianceStatusDeriver.Effective"/> demotes on read,
/// so the doc self-heals the instant something actually grades it. No DB.
/// </summary>
public sealed class NeverGradedComputeOutcomeTests
{
    private static readonly DateTime Today = new(2026, 6, 15, 0, 0, 0, DateTimeKind.Utc);

    /// <summary>A COI on a vendor whose checklist carries one rule governing <paramref name="ruleDocType"/>.</summary>
    private static Document Coi(string docType, string ruleDocType, DateTime? expiration)
    {
        var template = new ComplianceTemplate
        {
            Id = Guid.NewGuid(),
            Name = "T",
            Rules =
            [
                new ComplianceRule
                {
                    Id = Guid.NewGuid(), DocumentType = ruleDocType, FieldName = "general_liability_limit",
                    Operator = "min_value", ExpectedValue = "1000000", SortOrder = 0
                }
            ]
        };
        return new Document
        {
            Id = Guid.NewGuid(),
            DocumentType = docType,
            Vendor = new Vendor { Id = Guid.NewGuid(), Name = "V", ComplianceTemplate = template },
            GeneralLiabilityLimit = 2_000_000m,
            ExtractionFields = JsonDocument.Parse("{\"general_liability_limit\":\"2000000\"}"),
            ExpirationDate = expiration,
        };
    }

    [Fact]
    public void A_case_variant_document_type_still_matches_zero_rules_so_the_state_is_reachable()
    {
        // The ticket's live path, and the reason "never-graded" is not a hypothetical: the applicable-rules
        // filter compares DocumentType with ordinal `==` (case-SENSITIVE) while the vendor rollup matches a
        // required type with OrdinalIgnoreCase. A doc typed "COI" against a "coi" rule therefore matches
        // ZERO rules — yet is still counted as a document OF that required type by the rollup. It passes
        // every rule it is measured against, because it is measured against none.
        var doc = Coi(docType: "COI", ruleDocType: "coi", expiration: Today.AddDays(10));

        var outcome = ComplianceCheckService.ComputeOutcome(doc, Today);

        outcome.NewChecks.Should().BeEmpty("no rule governs this document's type");
        outcome.ClearExistingChecks.Should().BeTrue("certifying nothing must also leave no stale evidence");
    }

    [Fact]
    public void ComputeOutcome_still_stores_the_real_date_verdict_for_a_never_graded_doc()
    {
        // The read-only-overlay contract. Inside the 30-day window the zero-applicable-rules branch stores
        // ExpiringSoon — deliberately unchanged by #443. Storing Pending instead would be a WRITE, and
        // ComplianceStatus.Pending is a load-bearing value: the extraction worker claims on it.
        var doc = Coi(docType: "COI", ruleDocType: "coi", expiration: Today.AddDays(10));

        var outcome = ComplianceCheckService.ComputeOutcome(doc, Today);

        outcome.Status.Should().Be(ComplianceStatus.ExpiringSoon, "the stored date verdict stays real");

        // ...and the effective status a READER sees is Pending — the fix.
        ComplianceStatusDeriver.Effective(
                outcome.Status, doc.ExpirationDate, doc.EffectiveDate,
                DocumentGrading.IsGraded(outcome.NewChecks.Count), Today)
            .Should().Be(ComplianceStatus.Pending,
                "nothing was ever measured against this document, so it can assert no affirmative verdict");
    }

    [Fact]
    public void The_same_never_graded_doc_outside_the_window_already_stored_Pending()
    {
        // The asymmetry #443 removes, at the write site: identical absence of grading, but a far-future
        // expiry already stored Pending. Only the date differed — which is exactly why the READ overlay,
        // not a new stored value, is the right place to make the two agree.
        var doc = Coi(docType: "COI", ruleDocType: "coi", expiration: Today.AddDays(200));

        var outcome = ComplianceCheckService.ComputeOutcome(doc, Today);

        outcome.Status.Should().Be(ComplianceStatus.Pending);
        ComplianceStatusDeriver.Effective(
                outcome.Status, doc.ExpirationDate, doc.EffectiveDate,
                DocumentGrading.IsGraded(outcome.NewChecks.Count), Today)
            .Should().Be(ComplianceStatus.Pending, "both sides of the window boundary now read the same");
    }

    [Fact]
    public void A_matching_rule_grades_the_document_and_its_verdict_reads_through()
    {
        // The control, and the self-heal: correct the type (or add a governing rule) and the very same
        // document is measured, gets its check row, and its verdict reads through un-demoted.
        var doc = Coi(docType: "coi", ruleDocType: "coi", expiration: Today.AddDays(10));

        var outcome = ComplianceCheckService.ComputeOutcome(doc, Today);

        outcome.NewChecks.Should().ContainSingle();
        DocumentGrading.IsGraded(outcome.NewChecks.Count).Should().BeTrue();
        ComplianceStatusDeriver.Effective(
                outcome.Status, doc.ExpirationDate, doc.EffectiveDate,
                DocumentGrading.IsGraded(outcome.NewChecks.Count), Today)
            .Should().Be(ComplianceStatus.ExpiringSoon);
    }

    [Fact]
    public void A_vendor_with_no_checklist_at_all_is_also_never_graded()
    {
        // The other zero-check branch (no template / an empty one). Same treatment — the point of keying on
        // the CHECK ROWS rather than on "zero applicable rules" is that every way of certifying nothing
        // lands in one state, so no branch can be added later that quietly escapes the demotion.
        var doc = new Document
        {
            Id = Guid.NewGuid(),
            DocumentType = "coi",
            Vendor = new Vendor { Id = Guid.NewGuid(), Name = "V" },
            ExpirationDate = Today.AddDays(10),
        };

        var outcome = ComplianceCheckService.ComputeOutcome(doc, Today);

        outcome.NewChecks.Should().BeEmpty();
        ComplianceStatusDeriver.Effective(
                outcome.Status, doc.ExpirationDate, doc.EffectiveDate,
                DocumentGrading.IsGraded(outcome.NewChecks.Count), Today)
            .Should().Be(ComplianceStatus.Pending);
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, true)]
    [InlineData(7, true)]
    public void IsGraded_is_the_one_threshold(int checkCount, bool expected) =>
        // A SINGLE check row is enough: a document graded against one rule HAS been graded, pass or fail.
        // Pinned so the threshold can't drift into "enough checks" territory at some call site.
        DocumentGrading.IsGraded(checkCount).Should().Be(expected);
}
