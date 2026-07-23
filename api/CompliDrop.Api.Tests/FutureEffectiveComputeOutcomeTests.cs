using System.Text.Json;
using CompliDrop.Api.Entities;
using CompliDrop.Api.Services;
using FluentAssertions;

namespace CompliDrop.Api.Tests;

/// <summary>
/// Pure unit tests for the write-time verdict (<see cref="ComplianceCheckService.ComputeOutcome"/>) under a
/// future <see cref="Document.EffectiveDate"/> (#362 / ADR 0041). The KEY design contract these pin: the
/// future-effective demotion is a READ-only overlay, so ComputeOutcome stores the REAL rule verdict and the
/// effective status a reader sees (via <see cref="ComplianceStatusDeriver.Effective"/>) is Pending while the
/// doc is not yet in force. Storing Pending here would strand the doc after it becomes effective. No DB.
/// </summary>
public sealed class FutureEffectiveComputeOutcomeTests
{
    private static readonly DateTime Today = new(2026, 6, 15, 0, 0, 0, DateTimeKind.Utc);

    // A doc on a vendor whose checklist carries one "general_liability_limit >= 1,000,000" COI rule.
    private static Document Coi(decimal glLimit, DateTime? expiration, DateTime? effective)
    {
        var template = new ComplianceTemplate
        {
            Id = Guid.NewGuid(),
            Name = "T",
            Rules =
            [
                new ComplianceRule
                {
                    Id = Guid.NewGuid(), DocumentType = "coi", FieldName = "general_liability_limit",
                    Operator = "min_value", ExpectedValue = "1000000", SortOrder = 0
                }
            ]
        };
        return new Document
        {
            Id = Guid.NewGuid(),
            DocumentType = "coi",
            Vendor = new Vendor { Id = Guid.NewGuid(), Name = "V", ComplianceTemplate = template },
            GeneralLiabilityLimit = glLimit,
            ExtractionFields = JsonDocument.Parse($"{{\"general_liability_limit\":\"{glLimit}\"}}"),
            ExpirationDate = expiration,
            EffectiveDate = effective,
        };
    }

    [Fact]
    public void ComputeOutcome_stores_the_real_Compliant_verdict_for_a_future_effective_all_pass_doc()
    {
        // AC (a): a standalone future-effective COI that passes every rule. ComputeOutcome stores the REAL
        // verdict (Compliant) — NOT Pending — so the doc self-heals to it the day it becomes effective.
        var doc = Coi(glLimit: 2_000_000m, expiration: Today.AddDays(300), effective: Today.AddDays(30));

        var outcome = ComplianceCheckService.ComputeOutcome(doc, Today);

        outcome.Status.Should().Be(ComplianceStatus.Compliant,
            "the demotion is a read-only overlay; the stored verdict keeps the real rule result");

        // ...and the effective status a reader sees is Pending (not yet in force).
        ComplianceStatusDeriver.Effective(outcome.Status, doc.ExpirationDate, doc.EffectiveDate, Today)
            .Should().Be(ComplianceStatus.Pending, "the read overlay demotes the not-yet-in-force doc to Pending");
    }

    [Fact]
    public void A_future_effective_doc_that_fails_its_rules_stores_and_reads_NonCompliant()
    {
        // AC (d): a not-yet-active deficient cert is accurately not-compliant — the demotion must never mask
        // a hard fail. Both the stored verdict AND the read overlay stay NonCompliant.
        var doc = Coi(glLimit: 500_000m, expiration: Today.AddDays(300), effective: Today.AddDays(30));

        var outcome = ComplianceCheckService.ComputeOutcome(doc, Today);

        outcome.Status.Should().Be(ComplianceStatus.NonCompliant);
        ComplianceStatusDeriver.Effective(outcome.Status, doc.ExpirationDate, doc.EffectiveDate, Today)
            .Should().Be(ComplianceStatus.NonCompliant, "a future-effective failing cert is not masked to Pending");
    }

    [Fact]
    public void Expired_wins_over_a_future_effective_date_at_compute_time()
    {
        // AC (e): a malformed cert (EffectiveDate after today AND ExpirationDate before today, eff > exp).
        // ComputeOutcome's top-precedence expiry branch returns Expired before the effective date matters.
        var doc = Coi(glLimit: 2_000_000m, expiration: Today.AddDays(-1), effective: Today.AddDays(30));

        var outcome = ComplianceCheckService.ComputeOutcome(doc, Today);

        outcome.Status.Should().Be(ComplianceStatus.Expired, "Expired is top precedence and never demotes");
    }

    [Fact]
    public void An_in_force_all_pass_doc_stores_and_reads_Compliant()
    {
        // Control: with no future EffectiveDate, nothing changes — the doc is Compliant end to end.
        var doc = Coi(glLimit: 2_000_000m, expiration: Today.AddDays(300), effective: Today.AddDays(-5));

        var outcome = ComplianceCheckService.ComputeOutcome(doc, Today);

        outcome.Status.Should().Be(ComplianceStatus.Compliant);
        ComplianceStatusDeriver.Effective(outcome.Status, doc.ExpirationDate, doc.EffectiveDate, Today)
            .Should().Be(ComplianceStatus.Compliant, "an in-force compliant doc is not demoted");
    }
}
