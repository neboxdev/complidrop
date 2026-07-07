using CompliDrop.Api.RuleEngine;
using FluentAssertions;

namespace CompliDrop.Api.Tests.RuleEngine;

/// <summary>
/// Loader tests (SCHEMA §1): loads the SYNTHETIC valid fixtures from disk and asserts the fail-fast
/// validation rejects every class of schema violation the brief enumerates — dangling satisfiesFederal
/// ref, unknown fact, unknown op, bad date, out-of-vocabulary category, operator/type mismatch, and more.
/// All fixtures are fake (entity type "test-widget"); the tests assert MECHANICS, not compliance facts.
/// </summary>
public class RuleSetLoaderTests
{
    private static string ValidFixtureDir =>
        Path.Combine(AppContext.BaseDirectory, "RuleEngineFixtures", "valid");

    private static RuleSet LoadInline(string json, RuleLoadOptions? options = null) =>
        RuleSetLoader.LoadFromJson("inline-test.json", json, options);

    // A minimal, VALID single-rule file used as the base for mutation into malformed variants.
    private const string MinimalValidRule = """
    {
      "schemaVersion": 1,
      "rules": [{
        "id": "test-min",
        "obligationRef": "OBL-MIN",
        "jurisdiction": "us-tx",
        "entityTypes": ["test-widget"],
        "category": "license",
        "basis": "regulatory",
        "versions": [{
          "version": 1,
          "validFrom": "2026-01-01",
          "validTo": null,
          "confidence": "verified",
          "applicability": { "fact": "operatesInterstate", "op": "eq", "value": true },
          "obligation": { "name": "Minimal", "documentType": "license", "documentSubType": "test-min" },
          "cadence": { "kind": "one-time", "anchor": "documentExpiration", "gracePeriodDays": 0 },
          "citation": { "section": "Synthetic §1" },
          "rationale": "Synthetic minimal rule.",
          "userAction": "Ask the vendor."
        }]
      }]
    }
    """;

    // ---------------- happy path ----------------

    [Fact]
    public void Loads_and_merges_the_synthetic_fixture_directory()
    {
        var ruleSet = RuleSetLoader.LoadFromDirectory(ValidFixtureDir);

        ruleSet.SchemaVersion.Should().Be(1);
        ruleSet.Rules.Select(r => r.Id).Should().Contain(["test-fed-passenger-insurance", "test-tx-widget-license", "test-tx-operator-cert-implements-fed"]);
        // The fed + tx files merged into one set, so the cross-file satisfiesFederal reference resolves.
        ruleSet.Rules.Should().Contain(r => r.Id == "test-tx-operator-cert-implements-fed"
            && r.Versions[0].SatisfiesFederal.Contains("test-fed-operator-cert"));
    }

    [Fact]
    public void Parses_jsonc_comments_and_a_minimal_valid_rule()
    {
        var ruleSet = LoadInline(MinimalValidRule);
        ruleSet.Rules.Should().ContainSingle().Which.Id.Should().Be("test-min");
    }

    [Fact]
    public void VerifiedOnly_drops_probable_versions_and_keeps_verified()
    {
        var all = RuleSetLoader.LoadFromDirectory(ValidFixtureDir);
        var verifiedOnly = RuleSetLoader.LoadFromDirectory(ValidFixtureDir, new RuleLoadOptions(VerifiedOnly: true));

        all.Rules.Should().Contain(r => r.Id == "test-tx-probable-only");
        verifiedOnly.Rules.Should().NotContain(r => r.Id == "test-tx-probable-only",
            "a rule left with no verified versions is dropped in the production posture");
        verifiedOnly.Rules.Should().Contain(r => r.Id == "test-tx-widget-license");
    }

    // ---------------- rejections (fail-fast) ----------------

    [Fact]
    public void Rejects_a_dangling_satisfiesFederal_reference()
    {
        var json = MinimalValidRule.Replace(
            "\"userAction\": \"Ask the vendor.\"",
            "\"userAction\": \"Ask the vendor.\", \"satisfiesFederal\": [\"no-such-federal-rule\"]");

        var act = () => LoadInline(json);
        act.Should().Throw<RuleSchemaException>().WithMessage("*satisfiesFederal*no-such-federal-rule*");
    }

    [Fact]
    public void Rejects_a_leaf_referencing_an_unknown_fact()
    {
        var json = MinimalValidRule.Replace("\"fact\": \"operatesInterstate\"", "\"fact\": \"invented_fact\"");

        var act = () => LoadInline(json);
        act.Should().Throw<RuleSchemaException>().WithMessage("*unknown fact*invented_fact*");
    }

    [Fact]
    public void Rejects_an_unknown_operator()
    {
        var json = MinimalValidRule.Replace("\"op\": \"eq\"", "\"op\": \"between\"");

        var act = () => LoadInline(json);
        act.Should().Throw<RuleSchemaException>();
    }

    [Fact]
    public void Rejects_a_bad_date()
    {
        var json = MinimalValidRule.Replace("\"validFrom\": \"2026-01-01\"", "\"validFrom\": \"not-a-date\"");

        var act = () => LoadInline(json);
        act.Should().Throw<RuleSchemaException>();
    }

    [Fact]
    public void Rejects_an_inverted_version_window()
    {
        var json = MinimalValidRule.Replace("\"validTo\": null", "\"validTo\": \"2025-01-01\"");

        var act = () => LoadInline(json);
        act.Should().Throw<RuleSchemaException>().WithMessage("*validTo*before validFrom*");
    }

    [Fact]
    public void Rejects_an_out_of_vocabulary_category()
    {
        var json = MinimalValidRule.Replace("\"category\": \"license\"", "\"category\": \"tax-credit\"");

        var act = () => LoadInline(json);
        act.Should().Throw<RuleSchemaException>().WithMessage("*invalid category*tax-credit*");
    }

    [Fact]
    public void Rejects_an_operator_type_mismatch_gte_on_a_bool_fact()
    {
        var json = MinimalValidRule.Replace(
            "\"applicability\": { \"fact\": \"operatesInterstate\", \"op\": \"eq\", \"value\": true }",
            "\"applicability\": { \"fact\": \"operatesInterstate\", \"op\": \"gte\", \"value\": 5 }");

        var act = () => LoadInline(json);
        act.Should().Throw<RuleSchemaException>().WithMessage("*requires a numeric fact*");
    }

    [Fact]
    public void Rejects_a_value_whose_type_disagrees_with_the_fact_kind()
    {
        // operatesInterstate is a Bool fact; a string value is a schema error.
        var json = MinimalValidRule.Replace(
            "\"value\": true",
            "\"value\": \"yes\"");

        var act = () => LoadInline(json);
        act.Should().Throw<RuleSchemaException>().WithMessage("*not a valid Bool*");
    }

    [Fact]
    public void Rejects_a_bad_document_type()
    {
        var json = MinimalValidRule.Replace("\"documentType\": \"license\"", "\"documentType\": \"spreadsheet\"");

        var act = () => LoadInline(json);
        act.Should().Throw<RuleSchemaException>().WithMessage("*documentType*spreadsheet*");
    }

    [Fact]
    public void Rejects_an_invalid_jurisdiction()
    {
        var json = MinimalValidRule.Replace("\"jurisdiction\": \"us-tx\"", "\"jurisdiction\": \"texas\"");

        var act = () => LoadInline(json);
        act.Should().Throw<RuleSchemaException>().WithMessage("*invalid jurisdiction*texas*");
    }

    [Fact]
    public void Rejects_a_non_regulatory_basis()
    {
        var json = MinimalValidRule.Replace("\"basis\": \"regulatory\"", "\"basis\": \"contractual\"");

        var act = () => LoadInline(json);
        act.Should().Throw<RuleSchemaException>().WithMessage("*basis must be 'regulatory'*");
    }

    [Fact]
    public void Rejects_a_duplicate_rule_id()
    {
        var json = """
        {
          "schemaVersion": 1,
          "rules": [
            { "id": "dup", "obligationRef": "A", "jurisdiction": "us-tx", "entityTypes": ["test-widget"], "category": "license", "basis": "regulatory",
              "versions": [{ "version": 1, "validFrom": "2026-01-01", "validTo": null, "confidence": "verified",
                "applicability": { "all": [] }, "obligation": { "name": "A", "documentType": "license" },
                "rationale": "r", "userAction": "u" }] },
            { "id": "dup", "obligationRef": "B", "jurisdiction": "us-tx", "entityTypes": ["test-widget"], "category": "license", "basis": "regulatory",
              "versions": [{ "version": 1, "validFrom": "2026-01-01", "validTo": null, "confidence": "verified",
                "applicability": { "all": [] }, "obligation": { "name": "B", "documentType": "license" },
                "rationale": "r", "userAction": "u" }] }
          ]
        }
        """;

        var act = () => LoadInline(json);
        act.Should().Throw<RuleSchemaException>().WithMessage("*Duplicate rule id*dup*");
    }

    [Fact]
    public void Rejects_an_unsupported_schema_version()
    {
        var json = MinimalValidRule.Replace("\"schemaVersion\": 1", "\"schemaVersion\": 2");

        var act = () => LoadInline(json);
        act.Should().Throw<RuleSchemaException>().WithMessage("*unsupported schemaVersion*2*");
    }

    [Fact]
    public void Rejects_a_node_with_two_combinators()
    {
        var json = MinimalValidRule.Replace(
            "\"applicability\": { \"fact\": \"operatesInterstate\", \"op\": \"eq\", \"value\": true }",
            "\"applicability\": { \"all\": [], \"any\": [] }");

        var act = () => LoadInline(json);
        act.Should().Throw<RuleSchemaException>().WithMessage("*EXACTLY one*");
    }

    [Fact]
    public void Rejects_insurance_minimums_on_a_non_insurance_category()
    {
        var json = MinimalValidRule.Replace(
            "\"citation\": { \"section\": \"Synthetic §1\" }",
            "\"insuranceMinimums\": { \"perOccurrence\": 1000000, \"aggregate\": 1000000, \"currency\": \"USD\" }, \"citation\": { \"section\": \"Synthetic §1\" }");

        var act = () => LoadInline(json);
        act.Should().Throw<RuleSchemaException>().WithMessage("*insuranceMinimums is only valid on a category='insurance' rule*");
    }

    [Fact]
    public void Rejects_a_fixed_annual_cadence_missing_its_fixed_date()
    {
        var json = MinimalValidRule.Replace(
            "\"cadence\": { \"kind\": \"one-time\", \"anchor\": \"documentExpiration\", \"gracePeriodDays\": 0 }",
            "\"cadence\": { \"kind\": \"fixed-annual\", \"anchor\": \"fixedDate\", \"gracePeriodDays\": 0 }");

        var act = () => LoadInline(json);
        act.Should().Throw<RuleSchemaException>().WithMessage("*needs a fixedDate*");
    }

    [Fact]
    public void Rejects_a_missing_user_facing_rationale()
    {
        var json = MinimalValidRule.Replace("\"rationale\": \"Synthetic minimal rule.\"", "\"rationale\": \"\"");

        var act = () => LoadInline(json);
        act.Should().Throw<RuleSchemaException>().WithMessage("*rationale*required*");
    }
}
