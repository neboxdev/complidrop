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

    // ---------------- #383: an unreadable canonical value certifies nothing ----------------

    /// <summary>
    /// Builds the document state a real writer produces for an unparseable canonical value: the raw
    /// text in the ExtractionFields JSON, and the typed column cleared by the shared
    /// <see cref="CanonicalDocumentFields.ApplyToTypedColumn"/> parse. Driving the column through the
    /// production writer (rather than hand-nulling it) is what makes these tests pin the real state —
    /// if the parse ever starts accepting one of these values, the test tells us instead of silently
    /// asserting against a shape that no longer occurs.
    /// </summary>
    private static Document DocWithUnreadable(string field, string rawValue)
    {
        var doc = DocWithField(field, rawValue);
        CanonicalDocumentFields.ApplyToTypedColumn(doc, field, rawValue)
            .Should().Be(TypedColumnResult.Unreadable, "the fixture must actually be the unreadable case");
        return doc;
    }

    [Theory]
    [InlineData("12/31/2026 (per endorsement)")]
    [InlineData("continuous until cancelled")]
    [InlineData("2020-01-01 (per endorsement)")]
    public void Required_FAILS_on_an_expiration_date_we_could_not_read(string rawValue)
    {
        // THE #383 REGRESSION. `required` used to pass here: the typed ExpirationDate column was null,
        // so LookupValue fell through to the non-empty raw string and reported the field present. The
        // document then rendered "Insurance has not expired" with a green check — on a certificate a
        // human reads as expired in 2020 — while the date windows and the reminder queries, both keyed
        // on that same null column, could never fire. A value we can't read certifies NOTHING.
        var (passed, actual, note) = ComplianceCheckService.EvaluateRule(
            DocWithUnreadable("expiration_date", rawValue), Rule("required", "expiration_date"));

        passed.Should().BeFalse("an expiration date we couldn't read must never satisfy the requirement");
        actual.Should().Be(rawValue, "the check row shows the raw text so the user can correct it");
        note.Should().Be(ComplianceCheckService.UnreadableValueNote);
    }

    [Fact]
    public void An_unreadable_value_reads_differently_from_an_absent_one()
    {
        // The two notes assert OPPOSITE facts about the certificate, so they must not collapse into
        // one message: "Field missing." says the document shows no expiration; the unreadable note
        // says it shows one we couldn't parse. Only the second is a cue to go correct a value.
        var absent = ComplianceCheckService.EvaluateRule(
            DocWithField("other", "x"), Rule("required", "expiration_date"));
        var unreadable = ComplianceCheckService.EvaluateRule(
            DocWithUnreadable("expiration_date", "continuous until cancelled"), Rule("required", "expiration_date"));

        absent.passed.Should().BeFalse();
        unreadable.passed.Should().BeFalse();
        absent.note.Should().Be("Field missing.");
        unreadable.note.Should().Be(ComplianceCheckService.UnreadableValueNote);
        unreadable.note.Should().NotBe(absent.note);
    }

    [Theory]
    [InlineData("required", null)]
    [InlineData("equals", "2026-12-31")]
    [InlineData("contains", "2026")]
    [InlineData("min_value", "1000000")]
    public void EVERY_operator_fails_closed_on_an_unreadable_canonical_value(string op, string? expected)
    {
        // The guard sits ahead of the operator switch on purpose. `contains "2026"` would otherwise
        // MATCH the raw text "12/31/2026 (per endorsement)" — a substring hit on a date we can't
        // actually evaluate — so a per-operator fix would have left that door open.
        var (passed, _, note) = ComplianceCheckService.EvaluateRule(
            DocWithUnreadable("expiration_date", "12/31/2026 (per endorsement)"),
            Rule(op, "expiration_date", expected));

        passed.Should().BeFalse();
        note.Should().Be(ComplianceCheckService.UnreadableValueNote);
    }

    [Fact]
    public void An_unreadable_effective_date_and_gl_limit_fail_closed_too()
    {
        // All three typed columns share the ambiguity, so all three share the guard — expiration_date
        // is just the one with the loudest failure mode.
        ComplianceCheckService.EvaluateRule(
            DocWithUnreadable("effective_date", "see attached endorsement"),
            Rule("required", "effective_date")).passed.Should().BeFalse();

        ComplianceCheckService.EvaluateRule(
            DocWithUnreadable("general_liability_limit", "1M per occurrence"),
            Rule("required", "general_liability_limit")).passed.Should().BeFalse();
    }

    [Fact]
    public void LookupValue_returns_null_for_an_unreadable_canonical_value()
    {
        // The fail-open path the ticket names. LookupValue is `internal` and reachable from outside
        // EvaluateRule, so it is closed at the source too rather than only behind the guard.
        ComplianceCheckService.LookupValue(
            DocWithUnreadable("expiration_date", "continuous until cancelled"), "expiration_date")
            .Should().BeNull();
    }

    [Fact]
    public void LookupValue_still_falls_back_to_a_READABLE_raw_value()
    {
        // The narrowing is surgical: only values that fail to parse lose the fallback. A row whose
        // typed column is null but whose JSON holds a readable date (a legacy row written before both
        // writers funneled through CanonicalDocumentFields) keeps resolving exactly as before — this
        // fix must not quietly un-grade historical documents.
        var legacy = DocWithField("expiration_date", "2027-01-15"); // JSON only; column left null

        legacy.ExpirationDate.Should().BeNull("this fixture is the legacy shape: JSON set, column unset");
        ComplianceCheckService.LookupValue(legacy, "expiration_date").Should().Be("2027-01-15");
        ComplianceCheckService.EvaluateRule(legacy, Rule("required", "expiration_date")).passed.Should().BeTrue();
    }

    [Fact]
    public void LookupValue_prefers_the_typed_column_when_it_is_set()
    {
        // Unchanged precedence: a parsed column wins over the raw JSON, so the value a rule compares
        // is the same value the date windows and reminders use.
        var doc = DocWithField("expiration_date", "December 31st, 2026");
        doc.ExpirationDate = new DateTime(2026, 12, 31, 0, 0, 0, DateTimeKind.Utc);

        ComplianceCheckService.LookupValue(doc, "expiration_date").Should().Be("2026-12-31");
        ComplianceCheckService.EvaluateRule(doc, Rule("required", "expiration_date")).passed.Should().BeTrue();
    }

    // ---------------- #383 secondary: currency-symbol amounts ----------------

    [Theory]
    [InlineData("$1,500,000", "1000000", true)]
    [InlineData("1500000", "$1,000,000", true)]   // the owner-typed minimum side
    [InlineData("$1,500,000", "$1,000,000", true)]
    [InlineData("$999,999", "$1,000,000", false)]
    public void MinValue_compares_currency_formatted_amounts(string actualValue, string min, bool shouldPass)
    {
        // Pre-#383 every row here reported "Unable to parse numeric comparison" and FAILED, because
        // NumberStyles.Any under InvariantCulture allows ¤ and not $ — so a $1.5M certificate read as
        // non-compliant against a $1M floor. Non-canonical money fields (auto/umbrella/liquor limits)
        // have no typed column and reach this comparison as raw text, which is why the fix belongs on
        // both sides of the comparison and not only in the typed-column parse.
        var (passed, _, _) = ComplianceCheckService.EvaluateRule(
            DocWithField("auto_liability_limit", actualValue), Rule("min_value", "auto_liability_limit", min));

        passed.Should().Be(shouldPass);
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
