using CompliDrop.Api.Entities;
using CompliDrop.Api.Services;
using FluentAssertions;

namespace CompliDrop.Api.Tests;

/// <summary>
/// Pure unit tests for <see cref="CanonicalDocumentFields.ApplyToTypedColumn"/> — the shared parse
/// that maps a field name + string value onto the three typed columns ComplianceCheckService reads.
/// Used by both the extraction worker and the manual-edit endpoint (#216 / ADR 0017).
/// </summary>
public class CanonicalDocumentFieldsTests
{
    [Fact]
    public void Applies_general_liability_limit_as_decimal()
    {
        var doc = new Document();

        CanonicalDocumentFields.ApplyToTypedColumn(doc, "general_liability_limit", "1500000");

        doc.GeneralLiabilityLimit.Should().Be(1_500_000m);
    }

    [Fact]
    public void Parses_grouped_thousands_in_an_amount()
    {
        var doc = new Document();

        CanonicalDocumentFields.ApplyToTypedColumn(doc, "general_liability_limit", "1,500,000");

        doc.GeneralLiabilityLimit.Should().Be(1_500_000m);
    }

    [Fact]
    public void Applies_dates_as_utc()
    {
        var doc = new Document();

        CanonicalDocumentFields.ApplyToTypedColumn(doc, "effective_date", "2026-01-15");
        CanonicalDocumentFields.ApplyToTypedColumn(doc, "expiration_date", "2027-01-15");

        doc.EffectiveDate.Should().Be(new DateTime(2026, 1, 15, 0, 0, 0, DateTimeKind.Utc));
        doc.EffectiveDate!.Value.Kind.Should().Be(DateTimeKind.Utc); // required for the timestamptz column
        doc.ExpirationDate.Should().Be(new DateTime(2027, 1, 15, 0, 0, 0, DateTimeKind.Utc));
        doc.ExpirationDate!.Value.Kind.Should().Be(DateTimeKind.Utc);
    }

    [Fact]
    public void Slash_format_dates_parse_month_first_under_invariant_culture()
    {
        // The extraction prompt mandates ISO yyyy-MM-dd, but a model that disobeys — or a US manual
        // edit typed as a slash date — can still arrive here. ParseUtcDate pins
        // CultureInfo.InvariantCulture, which is month-first (MM/dd/yyyy), correct for the US
        // beachhead market. This test exists so a refactor to CurrentCulture (which is DD/MM on a
        // non-US host) can't silently flip every slash date's month and day and shift compliance
        // verdicts with no failing test — the only date tests above use culture-stable ISO, so they
        // would NOT catch that regression. (#244 time/TZ audit, extracted-date culture class.)
        var doc = new Document();

        CanonicalDocumentFields.ApplyToTypedColumn(doc, "expiration_date", "06/10/2026");

        doc.ExpirationDate.Should().Be(new DateTime(2026, 6, 10, 0, 0, 0, DateTimeKind.Utc),
            "InvariantCulture reads MM/dd/yyyy, so 06/10 is June 10 — not October 6");
    }

    [Fact]
    public void Parses_an_offset_bearing_date_to_utc()
    {
        // AssumeUniversal|AdjustToUniversal shifts an offset-bearing value to the equivalent UTC
        // instant (vs. the old SpecifyKind, which would have mislabeled it). The extraction prompt
        // emits bare yyyy-MM-dd, but a manual edit could carry an offset — pin the conversion.
        var doc = new Document();

        CanonicalDocumentFields.ApplyToTypedColumn(doc, "expiration_date", "2026-01-15T00:00:00+05:00");

        doc.ExpirationDate.Should().Be(new DateTime(2026, 1, 14, 19, 0, 0, DateTimeKind.Utc));
        doc.ExpirationDate!.Value.Kind.Should().Be(DateTimeKind.Utc);
    }

    [Fact]
    public void Matches_the_field_name_case_insensitively()
    {
        var doc = new Document();

        CanonicalDocumentFields.ApplyToTypedColumn(doc, "General_Liability_Limit", "2000000");

        doc.GeneralLiabilityLimit.Should().Be(2_000_000m);
    }

    [Fact]
    public void An_unparseable_amount_clears_the_column_to_null()
    {
        // A correction the parser can't read must NOT leave a stale value that contradicts the
        // field the user can now see — it nulls the column so LookupValue falls back to the JSON.
        var doc = new Document { GeneralLiabilityLimit = 500_000m };

        CanonicalDocumentFields.ApplyToTypedColumn(doc, "general_liability_limit", "approximately $1M");

        doc.GeneralLiabilityLimit.Should().BeNull();
    }

    [Fact]
    public void An_unparseable_date_clears_the_column_to_null()
    {
        var doc = new Document { ExpirationDate = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc) };

        CanonicalDocumentFields.ApplyToTypedColumn(doc, "expiration_date", "next year");

        doc.ExpirationDate.Should().BeNull();
    }

    [Fact]
    public void A_non_canonical_field_name_is_a_no_op()
    {
        var doc = new Document
        {
            GeneralLiabilityLimit = 1_000_000m,
            EffectiveDate = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            ExpirationDate = new DateTime(2027, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        };

        CanonicalDocumentFields.ApplyToTypedColumn(doc, "policy_number", "POL-123");

        // None of the typed columns move for a field that isn't one of the canonical three.
        doc.GeneralLiabilityLimit.Should().Be(1_000_000m);
        doc.EffectiveDate.Should().Be(new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        doc.ExpirationDate.Should().Be(new DateTime(2027, 1, 1, 0, 0, 0, DateTimeKind.Utc));
    }
}
