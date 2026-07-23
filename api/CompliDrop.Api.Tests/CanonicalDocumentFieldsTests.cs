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
        // field the user can now see — it nulls the column, and reports Unreadable so the caller
        // can route the document to a human (#383).
        var doc = new Document { GeneralLiabilityLimit = 500_000m };

        var result = CanonicalDocumentFields.ApplyToTypedColumn(doc, "general_liability_limit", "approximately $1M");

        doc.GeneralLiabilityLimit.Should().BeNull();
        result.Should().Be(TypedColumnResult.Unreadable);
    }

    [Fact]
    public void An_unparseable_date_clears_the_column_to_null()
    {
        var doc = new Document { ExpirationDate = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc) };

        var result = CanonicalDocumentFields.ApplyToTypedColumn(doc, "expiration_date", "next year");

        doc.ExpirationDate.Should().BeNull();
        result.Should().Be(TypedColumnResult.Unreadable);
    }

    // ---------------- #383: absent vs unreadable are different facts ----------------

    [Theory]
    [InlineData("expiration_date", "12/31/2026 (per endorsement)")]
    [InlineData("expiration_date", "continuous until cancelled")]
    [InlineData("expiration_date", "2020-01-01 (per endorsement)")]
    [InlineData("effective_date", "see attached")]
    [InlineData("general_liability_limit", "1M per occurrence")]
    public void A_non_blank_value_the_parser_cannot_read_reports_Unreadable(string field, string value)
    {
        // The #383 core distinction. A cleared column is the SAME null whether the certificate
        // carried no value or carried one we couldn't read — opposite compliance meanings. The
        // return value is the only thing that still knows which happened, so every writer can
        // route the second case to a human instead of silently certifying around it.
        var doc = new Document();

        CanonicalDocumentFields.ApplyToTypedColumn(doc, field, value).Should().Be(TypedColumnResult.Unreadable);
        CanonicalDocumentFields.IsUnreadable(field, value).Should().BeTrue();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void A_blank_value_is_Blank_not_Unreadable(string? value)
    {
        // Blank is an HONEST absence — the certificate really doesn't show one. It must not drag the
        // document into manual review, or every COI with no effective date would demand attention.
        var doc = new Document { ExpirationDate = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc) };

        CanonicalDocumentFields.ApplyToTypedColumn(doc, "expiration_date", value).Should().Be(TypedColumnResult.Blank);
        doc.ExpirationDate.Should().BeNull("a blank correction still clears the column");
        CanonicalDocumentFields.IsUnreadable("expiration_date", value).Should().BeFalse();
    }

    [Fact]
    public void A_parseable_value_is_Parsed()
    {
        var doc = new Document();

        CanonicalDocumentFields.ApplyToTypedColumn(doc, "expiration_date", "2027-01-15")
            .Should().Be(TypedColumnResult.Parsed);
        CanonicalDocumentFields.IsUnreadable("expiration_date", "2027-01-15").Should().BeFalse();
    }

    [Fact]
    public void A_non_canonical_field_is_never_Unreadable()
    {
        // Only the three typed columns have the absent-vs-unreadable ambiguity. A free-text field
        // like policy_number keeps whatever the document says, however odd it looks — flagging those
        // would put half the corpus into manual review.
        var doc = new Document();

        CanonicalDocumentFields.ApplyToTypedColumn(doc, "policy_number", "not on this certificate")
            .Should().Be(TypedColumnResult.NotCanonical);
        CanonicalDocumentFields.IsUnreadable("policy_number", "not on this certificate").Should().BeFalse();
    }

    // ---------------- #383 secondary: currency symbols ----------------

    [Theory]
    [InlineData("$1,000,000", 1_000_000)]
    [InlineData("$1000000", 1_000_000)]
    [InlineData("  $2,000,000  ", 2_000_000)]
    [InlineData("€1,500,000", 1_500_000)]
    [InlineData("1,000,000", 1_000_000)]
    public void An_amount_parses_with_its_currency_symbol_stripped(string value, decimal expected)
    {
        // NumberStyles.Any allows a currency symbol, but under InvariantCulture that symbol is ¤ —
        // NOT $. So "$1,000,000", the most natural way to write a coverage limit, used to null the
        // column and (pre-#383) hand the raw string to a min_value comparison that then reported it
        // couldn't parse: a false NonCompliant on a certificate that genuinely met the floor.
        var doc = new Document();

        CanonicalDocumentFields.ApplyToTypedColumn(doc, "general_liability_limit", value)
            .Should().Be(TypedColumnResult.Parsed);
        doc.GeneralLiabilityLimit.Should().Be(expected);
    }

    [Theory]
    [InlineData("$")]
    [InlineData("1,000,000 USD")]
    [InlineData("$1M")]
    public void A_currency_strip_does_not_rescue_genuinely_unreadable_text(string value)
    {
        // The strip is EDGE-only and deliberately narrow: anything it can't turn into a bare number
        // stays Unreadable and reaches a human, rather than being coerced into a number we invented.
        var doc = new Document();

        CanonicalDocumentFields.ApplyToTypedColumn(doc, "general_liability_limit", value)
            .Should().Be(TypedColumnResult.Unreadable);
        doc.GeneralLiabilityLimit.Should().BeNull();
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
