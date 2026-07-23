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

    [Fact]
    public void Equals_wellformed_still_passes_a_matching_value_with_no_note()
    {
        // Guards the #374 fail-closed guard against over-reaching: a real ExpectedValue must behave
        // EXACTLY as before — a matching value passes, with no note.
        var (passed, actual, note) = ComplianceCheckService.EvaluateRule(
            DocWithField("license_type", "CDL"), Rule("equals", "license_type", "CDL"));

        passed.Should().BeTrue();
        actual.Should().Be("CDL");
        note.Should().BeNull();
    }

    [Fact]
    public void Equals_fails_closed_when_expected_is_null_and_field_missing()
    {
        // #374 core regression. A null ExpectedValue made `string.Equals(null, null)` TRUE, so a
        // document MISSING the field read as PASSING — a wrong-direction (fail-open) verdict, unique
        // among the operators. It must now FAIL, and NOT with the self-contradicting "Field missing."
        // note (the field being absent is not why this failed — the rule itself is misconfigured).
        var (passed, _, note) = ComplianceCheckService.EvaluateRule(
            DocWithField("other", "x"), Rule("equals", "license_type", expected: null));

        passed.Should().BeFalse();
        note.Should().NotBe("Field missing.");
        note.Should().Be("Rule is misconfigured: no expected value.");
    }

    [Fact]
    public void Equals_fails_closed_when_expected_is_null_and_field_present()
    {
        // The inverse of the fail-open pair: a doc that actually shows the field must not be judged
        // against a null expected either — the rule is misconfigured, so it fails closed regardless.
        var (passed, actual, note) = ComplianceCheckService.EvaluateRule(
            DocWithField("license_type", "CDL"), Rule("equals", "license_type", expected: null));

        passed.Should().BeFalse();
        actual.Should().Be("CDL");
        note.Should().Be("Rule is misconfigured: no expected value.");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Equals_fails_closed_when_expected_is_blank(string expected)
    {
        // A whitespace-only expected is as meaningless as null — same fail-closed treatment, mirroring
        // how `min_value` and `contains` also fail closed on a blank expected value.
        var (passed, _, note) = ComplianceCheckService.EvaluateRule(
            DocWithField("license_type", "CDL"), Rule("equals", "license_type", expected));

        passed.Should().BeFalse();
        note.Should().Be("Rule is misconfigured: no expected value.");
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

    // ---------------- contains: additional_insured checkbox fallback (#272) ----------------

    private static Document CoiWith(object? additionalInsured, string? certificateHolder = null, string? operations = null)
    {
        var fields = new Dictionary<string, object>();
        if (additionalInsured is not null) fields["additional_insured"] = additionalInsured;
        if (certificateHolder is not null) fields["certificate_holder"] = certificateHolder;
        if (operations is not null) fields["description_of_operations"] = operations;
        return new Document { ExtractionFields = JsonSerializer.SerializeToDocument(fields) };
    }

    [Theory]
    [InlineData("Y")]
    [InlineData(" y ")]
    [InlineData("X")]
    [InlineData("true")]
    [InlineData("YES")]
    [InlineData("✓")]
    [InlineData("checked")]
    public void Additional_insured_affirmative_flag_falls_back_to_certificate_holder(string flag)
    {
        // The ACORD ADDL INSD column reading: the certificate says additional-insured
        // exists but names nobody — the name lives in the certificate-holder box.
        var doc = CoiWith(flag, certificateHolder: "Riverside Event Hall\n123 Main St");

        var (passed, actual, note) = ComplianceCheckService.EvaluateRule(
            doc, Rule("contains", "additional_insured", "Riverside Event Hall"));

        passed.Should().BeTrue();
        actual.Should().Be(flag, "the check row shows the field's true value; the note explains the fallback");
        note.Should().Contain("certificate holder");
    }

    [Fact]
    public void Additional_insured_fallback_matches_certificate_holder_case_insensitively()
    {
        // A user who typed lowercase into "Name to look for" must still match the
        // certificate's casing — a regression to Ordinal here re-creates the exact
        // honest-certificate-flagged class #272 fixes.
        var doc = CoiWith("Y", certificateHolder: "RIVERSIDE EVENT HALL");

        var (passed, _, _) = ComplianceCheckService.EvaluateRule(
            doc, Rule("contains", "additional_insured", "riverside event hall"));

        passed.Should().BeTrue();
    }

    [Fact]
    public void Additional_insured_affirmative_flag_falls_back_to_description_of_operations()
    {
        var doc = CoiWith("Y", certificateHolder: "Somebody Else LLC",
            operations: "Riverside Event Hall is named as additional insured per attached endorsement.");

        var (passed, _, _) = ComplianceCheckService.EvaluateRule(
            doc, Rule("contains", "additional_insured", "Riverside Event Hall"));

        passed.Should().BeTrue();
    }

    [Fact]
    public void Additional_insured_fallback_matches_description_of_operations_case_insensitively()
    {
        var doc = CoiWith("Y", certificateHolder: "Somebody Else LLC",
            operations: "riverside event hall is named as additional insured.");

        var (passed, _, _) = ComplianceCheckService.EvaluateRule(
            doc, Rule("contains", "additional_insured", "Riverside Event Hall"));

        passed.Should().BeTrue();
    }

    [Fact]
    public void Additional_insured_affirmative_flag_fails_when_name_is_nowhere()
    {
        var doc = CoiWith("Y", certificateHolder: "Somebody Else LLC", operations: "General liability per project.");

        var (passed, _, note) = ComplianceCheckService.EvaluateRule(
            doc, Rule("contains", "additional_insured", "Riverside Event Hall"));

        passed.Should().BeFalse();
        note.Should().Be("The additional-insured box is checked, but 'Riverside Event Hall' was not found in the certificate holder or description of operations.");
    }

    [Fact]
    public void Additional_insured_json_boolean_true_takes_the_fallback_path()
    {
        // LookupValue serializes a JSON boolean via GetRawText() → "true" — must be treated
        // as the checkbox reading, not matched literally.
        var doc = CoiWith(true, certificateHolder: "Riverside Event Hall");

        var (passed, _, _) = ComplianceCheckService.EvaluateRule(
            doc, Rule("contains", "additional_insured", "Riverside Event Hall"));

        passed.Should().BeTrue();
    }

    [Theory]
    [InlineData("N")]
    [InlineData("false")]
    [InlineData("no")]
    public void Additional_insured_negative_flag_fails_without_fallback(string flag)
    {
        // A negative flag means the certificate says NOT additional insured — looking in
        // the holder box anyway would pass a certificate that disclaims the provision.
        var doc = CoiWith(flag, certificateHolder: "Riverside Event Hall");

        var (passed, _, note) = ComplianceCheckService.EvaluateRule(
            doc, Rule("contains", "additional_insured", "Riverside Event Hall"));

        passed.Should().BeFalse();
        note.Should().Be("Expected to contain 'Riverside Event Hall'.");
    }

    [Fact]
    public void Additional_insured_missing_field_fails_without_fallback()
    {
        // Absence must FAIL, not fall back: the certificate-holder box almost always names
        // the venue, so falling back on a missing field would pass certificates with no
        // additional-insured provision at all (#257's vacuous-Compliant class).
        var doc = CoiWith(null, certificateHolder: "Riverside Event Hall");

        var (passed, _, _) = ComplianceCheckService.EvaluateRule(
            doc, Rule("contains", "additional_insured", "Riverside Event Hall"));

        passed.Should().BeFalse();
    }

    [Fact]
    public void Additional_insured_party_name_text_takes_the_normal_contains_path()
    {
        // The v2 prompt emits party names as text — plain substring match, no fallback,
        // no explanatory note.
        var doc = CoiWith("Riverside Event Hall; Acme Property Mgmt", certificateHolder: "Unrelated Co");

        var (passed, _, note) = ComplianceCheckService.EvaluateRule(
            doc, Rule("contains", "additional_insured", "riverside event hall"));

        passed.Should().BeTrue();
        note.Should().BeNull();
    }

    [Fact]
    public void Affirmative_flag_on_another_field_does_not_trigger_the_fallback()
    {
        // The fallback is scoped to additional_insured — a "Y" in any other field keeps
        // the plain contains semantics.
        var doc = new Document
        {
            ExtractionFields = JsonSerializer.SerializeToDocument(new Dictionary<string, object>
            {
                ["some_flag"] = "Y",
                ["certificate_holder"] = "Riverside Event Hall"
            })
        };

        var (passed, _, note) = ComplianceCheckService.EvaluateRule(
            doc, Rule("contains", "some_flag", "Riverside Event Hall"));

        passed.Should().BeFalse();
        note.Should().Be("Expected to contain 'Riverside Event Hall'.");
    }

    // ---------------- contains: misconfigured (blank/empty expected) fails closed (#374 re-review) ----------------

    [Fact]
    public void Contains_fails_closed_when_expected_is_empty_and_field_present()
    {
        // #374 re-review CORE regression. `"Acme".Contains("")` is TRUE in .NET, so a persisted
        // `contains` rule with an EMPTY expected value graded any document that HAS the field as
        // PASSING — a vacuous false-Compliant. Empty is non-null, so it slipped past the plain-path
        // `rule.ExpectedValue is not null` guard. It must now FAIL closed with the misconfig note —
        // NOT pass, and NOT a fallback-hit note.
        var (passed, actual, note) = ComplianceCheckService.EvaluateRule(
            DocWithField("additional_insured", "Acme"), Rule("contains", "additional_insured", ""));

        passed.Should().BeFalse();
        actual.Should().Be("Acme");
        note.Should().Be("Rule is misconfigured: no expected value.");
    }

    [Theory]
    [InlineData("Acme", "   ")]   // plain substring path, whitespace expected
    [InlineData("Acme", null)]    // plain substring path, null expected
    [InlineData("Y", "")]         // affirmative-flag fallback path, empty expected (holder.Contains("") is otherwise TRUE)
    [InlineData("Y", "   ")]      // affirmative-flag fallback path, whitespace expected
    [InlineData("Y", null)]       // affirmative-flag fallback path, null expected
    public void Contains_fails_closed_on_a_blank_expected_across_both_paths(string additionalInsured, string? expected)
    {
        // The guard sits at the TOP of the contains arm, so a null/blank/empty expected fails closed on
        // BOTH the plain substring path (actual = "Acme") and the additional_insured affirmative-flag
        // fallback (actual = "Y", where `holder.Contains("")` would otherwise vacuously pass). Same
        // misconfig note as `equals` — never a pass, never a fallback-hit note.
        var doc = CoiWith(additionalInsured, certificateHolder: "Riverside Event Hall");

        var (passed, _, note) = ComplianceCheckService.EvaluateRule(
            doc, Rule("contains", "additional_insured", expected));

        passed.Should().BeFalse();
        note.Should().Be("Rule is misconfigured: no expected value.");
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
    public void MinValue_fails_with_field_missing_note_when_field_absent()
    {
        // The pre-#272 behavior surfaced a missing coverage line as the jargon note
        // "Unable to parse numeric comparison" — a missing field now reads like the
        // other operators' missing case.
        var (passed, actual, note) = ComplianceCheckService.EvaluateRule(
            DocWithField("other", "x"), Rule("min_value", "professional_liability_limit", "1000000"));

        passed.Should().BeFalse();
        actual.Should().BeNull();
        note.Should().Be("Field missing.");
    }

    [Fact]
    public void MinValue_fails_when_expected_is_not_numeric()
    {
        var (passed, _, note) = ComplianceCheckService.EvaluateRule(
            DocWithField("gl", "1500000"), Rule("min_value", "gl", "lots"));

        passed.Should().BeFalse();
        note.Should().Be("Unable to parse numeric comparison.");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void MinValue_fails_closed_when_expected_is_blank(string? min)
    {
        // Pin (#374 re-review): a min_value rule with a null/blank/empty expected fails closed —
        // `decimal.TryParse` rejects each, so the runtime net catches a misconfigured min_value row
        // persisted before the UpsertRule write guard. Pinned so a future refactor of the parse path
        // can't silently reopen the door. The actual is a valid number, so the failure is due to the
        // EXPECTED, not the actual.
        var (passed, _, note) = ComplianceCheckService.EvaluateRule(
            DocWithField("gl", "1500000"), Rule("min_value", "gl", min));

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

    // ---------------- check-column clamping (#272 review) ----------------

    [Fact]
    public void ClampToColumn_truncates_oversize_values_to_the_column_length()
    {
        // ComplianceCheck.ActualValue / .Notes are varchar(500) and Npgsql does not
        // truncate — an oversize actual (long description_of_operations) or note
        // (embedding a near-500-char ExpectedValue) threw 22001 at evaluation time.
        var oversize = new string('a', 600);

        ComplianceCheckService.ClampToColumn(oversize).Should().HaveLength(500);
        ComplianceCheckService.ClampToColumn(new string('b', 500)).Should().HaveLength(500);
        ComplianceCheckService.ClampToColumn("short").Should().Be("short");
        ComplianceCheckService.ClampToColumn(null).Should().BeNull();
    }

    [Fact]
    public void ClampToColumn_never_splits_a_surrogate_pair_at_the_cut()
    {
        // An emoji straddling index 499/500 must not leave a lone high surrogate — that is
        // an invalid string Npgsql's strict UTF-8 encoder rejects at SaveChangesAsync.
        var straddling = new string('a', 499) + "\U0001F600" + new string('b', 100);

        var clamped = ComplianceCheckService.ClampToColumn(straddling)!;

        clamped.Should().HaveLength(499);
        clamped.Should().EndWith("a");
        var act = () => new System.Text.UTF8Encoding(false, throwOnInvalidBytes: true).GetBytes(clamped);
        act.Should().NotThrow("the clamped value must stay valid Unicode");
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
