using System.Text.Json;
using CompliDrop.Api.Entities;
using CompliDrop.Api.Services;
using FluentAssertions;

namespace CompliDrop.Api.Tests;

/// <summary>
/// Pure unit tests (no database) for the compliance rule-evaluation logic:
/// <see cref="ComplianceCheckService.EvaluateRule"/> (the four operators) and
/// <see cref="ComplianceCheckService.LookupValue"/> (special fields + JSON extraction lookup).
/// </summary>
public class ComplianceRuleEvaluationTests
{
    private static Document DocWithField(string field, object value) =>
        new() { ExtractionFields = JsonSerializer.SerializeToDocument(new Dictionary<string, object> { [field] = value }) };

    private static ComplianceRule Rule(string op, string? field, string? expected = null) =>
        new() { Operator = op, FieldName = field, ExpectedValue = expected };

    // ---------------- required ----------------

    [Fact]
    public void Required_passes_when_field_present()
    {
        var (passed, actual, note) = ComplianceCheckService.EvaluateRule(
            DocWithField("license_number", "ABC123"), Rule("required", "license_number"));

        passed.Should().BeTrue();
        actual.Should().Be("ABC123");
        note.Should().BeNull();
    }

    [Fact]
    public void Required_fails_with_note_when_field_missing()
    {
        var (passed, actual, note) = ComplianceCheckService.EvaluateRule(
            DocWithField("other", "x"), Rule("required", "license_number"));

        passed.Should().BeFalse();
        actual.Should().BeNull();
        note.Should().Be("Field missing.");
    }

    [Fact]
    public void Required_fails_when_field_is_whitespace()
    {
        var (passed, _, _) = ComplianceCheckService.EvaluateRule(
            DocWithField("license_number", "   "), Rule("required", "license_number"));

        passed.Should().BeFalse();
    }

    // ---------------- equals ----------------

    [Theory]
    [InlineData("CDL", "cdl", true)]    // case-insensitive
    [InlineData(" CDL ", "CDL", true)]  // trims both sides
    [InlineData("CDL-A", "CDL", false)]
    public void Equals_is_case_insensitive_and_trims(string actualValue, string expected, bool shouldPass)
    {
        var (passed, _, _) = ComplianceCheckService.EvaluateRule(
            DocWithField("license_type", actualValue), Rule("equals", "license_type", expected));

        passed.Should().Be(shouldPass);
    }

    [Fact]
    public void Equals_fails_with_note_when_field_missing()
    {
        var (passed, _, note) = ComplianceCheckService.EvaluateRule(
            DocWithField("other", "x"), Rule("equals", "license_type", "CDL"));

        passed.Should().BeFalse();
        note.Should().Be("Field missing.");
    }

    // ---------------- contains ----------------

    [Theory]
    [InlineData("Acme Property Mgmt", "property", true)]  // case-insensitive substring
    [InlineData("Acme Roofing", "property", false)]
    public void Contains_matches_substring_case_insensitively(string actualValue, string expected, bool shouldPass)
    {
        var (passed, _, _) = ComplianceCheckService.EvaluateRule(
            DocWithField("additional_insured", actualValue), Rule("contains", "additional_insured", expected));

        passed.Should().Be(shouldPass);
    }

    [Fact]
    public void Contains_fails_with_note_when_field_missing()
    {
        var (passed, _, note) = ComplianceCheckService.EvaluateRule(
            DocWithField("other", "x"), Rule("contains", "additional_insured", "property"));

        passed.Should().BeFalse();
        note.Should().Be("Expected to contain 'property'.");
    }

    // ---------------- min_value ----------------

    [Theory]
    [InlineData("1500000", "1000000", true)]
    [InlineData("1000000", "1000000", true)]   // boundary: equal passes
    [InlineData("999999", "1000000", false)]
    public void MinValue_compares_numeric_values_at_the_boundary(string actualValue, string min, bool shouldPass)
    {
        var (passed, _, _) = ComplianceCheckService.EvaluateRule(
            DocWithField("gl", actualValue), Rule("min_value", "gl", min));

        passed.Should().Be(shouldPass);
    }

    [Fact]
    public void MinValue_fails_when_actual_is_not_numeric()
    {
        var (passed, _, note) = ComplianceCheckService.EvaluateRule(
            DocWithField("gl", "not-a-number"), Rule("min_value", "gl", "1000000"));

        passed.Should().BeFalse();
        note.Should().Be("Unable to parse numeric comparison.");
    }

    [Fact]
    public void MinValue_fails_when_expected_is_not_numeric()
    {
        var (passed, _, note) = ComplianceCheckService.EvaluateRule(
            DocWithField("gl", "1500000"), Rule("min_value", "gl", "lots"));

        passed.Should().BeFalse();
        note.Should().Be("Unable to parse numeric comparison.");
    }

    // ---------------- operator fallbacks ----------------

    [Fact]
    public void Unknown_operator_fails_with_note()
    {
        var (passed, _, note) = ComplianceCheckService.EvaluateRule(
            DocWithField("x", "y"), Rule("between", "x", "1"));

        passed.Should().BeFalse();
        note.Should().Be("Unknown operator 'between'.");
    }

    [Fact]
    public void Null_operator_defaults_to_required()
    {
        var rule = new ComplianceRule { Operator = null!, FieldName = "license_number" };

        ComplianceCheckService.EvaluateRule(DocWithField("license_number", "X"), rule).passed.Should().BeTrue();
        ComplianceCheckService.EvaluateRule(DocWithField("other", "X"), rule).passed.Should().BeFalse();
    }

    // ---------------- LookupValue ----------------

    [Fact]
    public void LookupValue_reads_special_expiration_date_as_iso()
    {
        var doc = new Document { ExpirationDate = new DateTime(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc) };

        ComplianceCheckService.LookupValue(doc, "expiration_date").Should().Be("2026-07-01");
    }

    [Fact]
    public void LookupValue_reads_special_general_liability_limit()
    {
        var doc = new Document { GeneralLiabilityLimit = 1000000m };

        ComplianceCheckService.LookupValue(doc, "general_liability_limit").Should().Be("1000000");
    }

    [Fact]
    public void LookupValue_reads_string_and_number_json_fields()
    {
        var doc = new Document
        {
            ExtractionFields = JsonSerializer.SerializeToDocument(new Dictionary<string, object>
            {
                ["carrier"] = "Acme Insurance",
                ["limit"] = 2000000
            })
        };

        ComplianceCheckService.LookupValue(doc, "carrier").Should().Be("Acme Insurance");
        ComplianceCheckService.LookupValue(doc, "limit").Should().Be("2000000");
    }

    [Fact]
    public void LookupValue_returns_null_for_missing_or_blank_field()
    {
        ComplianceCheckService.LookupValue(DocWithField("present", "v"), "absent").Should().BeNull();
        ComplianceCheckService.LookupValue(DocWithField("present", "v"), "  ").Should().BeNull();
        ComplianceCheckService.LookupValue(new Document(), "anything").Should().BeNull();
    }
}
