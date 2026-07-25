using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CompliDrop.Api.Tests.TestHelpers;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;

namespace CompliDrop.Api.Tests;

/// <summary>
/// HTTP-level tests for POST /api/compliance/templates/{tid}/rules (UpsertRule), focused on the
/// #374 write-time guard: a value-operator rule (equals / contains / min_value) may not be
/// persisted with a null/blank ExpectedValue. Such a rule is meaningless to evaluate, and for
/// `equals` it was actively fail-OPEN (a document MISSING the field read Compliant). The `required`
/// operator is exempt — it legitimately carries no expected value. Covers both the CREATE and the
/// UPDATE path (the same endpoint), and asserts the rejected write never lands.
/// </summary>
public sealed class ComplianceRuleUpsertTests(IntegrationTestFixture fixture) : IntegrationTestBase(fixture)
{
    private static async Task<JsonElement> Data(HttpResponseMessage resp) =>
        (await resp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("data");

    private static async Task<string?> ErrorCode(HttpResponseMessage resp) =>
        (await resp.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("error").GetProperty("code").GetString();

    private static async Task<Guid> CreateTemplateAsync(HttpClient client)
    {
        var resp = await client.PostAsJsonAsync("/api/compliance/templates",
            new { name = "Caterer", description = (string?)null });
        resp.EnsureSuccessStatusCode();
        return (await Data(resp)).GetProperty("id").GetGuid();
    }

    private static Task<HttpResponseMessage> PostRuleAsync(
        HttpClient client, Guid templateId, string fieldName, string @operator, string? expectedValue,
        Guid? id = null, string? documentType = "coi") =>
        client.PostAsJsonAsync($"/api/compliance/templates/{templateId}/rules", new
        {
            id,
            documentType,
            fieldName,
            @operator,
            expectedValue,
            errorMessage = (string?)null,
            sortOrder = 0,
        });

    [Theory]
    [InlineData("equals", "license_type", null)]
    [InlineData("equals", "license_type", "")]
    [InlineData("equals", "license_type", "   ")]
    [InlineData("contains", "additional_insured", null)]
    [InlineData("contains", "additional_insured", "")]
    [InlineData("contains", "additional_insured", "   ")]
    [InlineData("min_value", "general_liability_limit", null)]
    [InlineData("min_value", "general_liability_limit", "")]
    [InlineData("min_value", "general_liability_limit", "   ")]
    public async Task Creating_a_value_operator_rule_without_an_expected_value_is_rejected_400(
        string op, string field, string? expected)
    {
        // #374: a value operator with nothing to compare against is rejected at the write boundary,
        // so the fail-open `equals` state (and its meaningless siblings) can never be persisted.
        var auth = await RegisterAndLoginAsync();
        var templateId = await CreateTemplateAsync(auth.Client);

        var resp = await PostRuleAsync(auth.Client, templateId, field, op, expected);

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ErrorCode(resp)).Should().Be("validation.expected_value_required");

        // The reject blocks the write — no rule row lands.
        await using var db = CreateSystemDb();
        (await db.ComplianceRules.CountAsync(r => r.ComplianceTemplateId == templateId)).Should().Be(0);
    }

    [Theory]
    [InlineData("abc")]
    [InlineData("$1M")]
    [InlineData("1,00,0x")]
    public async Task Creating_a_min_value_rule_with_a_non_numeric_expected_value_is_rejected_400(string expected)
    {
        // #374 re-review: a min_value threshold that ISN'T a number passes the blank guard above but can
        // never be satisfied — EvaluateRule fails EVERY document closed with "Unable to parse numeric
        // comparison." Reject it at the write boundary too, with a DISTINCT code from the blank case, so
        // the misconfiguration is caught up front instead of silently failing every certificate. The
        // guard mirrors EvaluateRule's decimal.TryParse(NumberStyles.Any, CultureInfo.InvariantCulture),
        // so a threshold the eval can parse is never rejected here.
        var auth = await RegisterAndLoginAsync();
        var templateId = await CreateTemplateAsync(auth.Client);

        var resp = await PostRuleAsync(auth.Client, templateId, "general_liability_limit", "min_value", expected);

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ErrorCode(resp)).Should().Be("validation.expected_value_not_numeric");

        // The reject blocks the write — no rule row lands.
        await using var db = CreateSystemDb();
        (await db.ComplianceRules.CountAsync(r => r.ComplianceTemplateId == templateId)).Should().Be(0);
    }

    [Fact]
    public async Task Creating_a_required_rule_without_an_expected_value_is_accepted()
    {
        // `required` only asserts the field is present — it needs no value to compare against, so it
        // stays exempt from the #374 guard.
        var auth = await RegisterAndLoginAsync();
        var templateId = await CreateTemplateAsync(auth.Client);

        var resp = await PostRuleAsync(auth.Client, templateId, "license_number", "required", null);

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        await using var db = CreateSystemDb();
        (await db.ComplianceRules.CountAsync(r => r.ComplianceTemplateId == templateId)).Should().Be(1);
    }

    [Fact]
    public async Task Creating_value_operator_rules_with_a_valid_expected_value_is_accepted()
    {
        // The guard only rejects the null/blank case — a well-formed value-operator rule is unaffected.
        // Three distinct (documentType, fieldName, operator) tuples, so the dedupe guard doesn't fire.
        var auth = await RegisterAndLoginAsync();
        var templateId = await CreateTemplateAsync(auth.Client);

        (await PostRuleAsync(auth.Client, templateId, "license_type", "equals", "CDL"))
            .StatusCode.Should().Be(HttpStatusCode.OK);
        (await PostRuleAsync(auth.Client, templateId, "additional_insured", "contains", "Riverside Event Hall"))
            .StatusCode.Should().Be(HttpStatusCode.OK);
        (await PostRuleAsync(auth.Client, templateId, "general_liability_limit", "min_value", "1000000"))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        await using var db = CreateSystemDb();
        (await db.ComplianceRules.CountAsync(r => r.ComplianceTemplateId == templateId)).Should().Be(3);
    }

    [Fact]
    public async Task Updating_an_existing_rule_to_a_blank_expected_value_is_rejected_400()
    {
        // The guard covers the UPDATE path too (same endpoint): a well-formed `equals` rule cannot be
        // edited into the fail-open null-expected state.
        var auth = await RegisterAndLoginAsync();
        var templateId = await CreateTemplateAsync(auth.Client);

        var create = await PostRuleAsync(auth.Client, templateId, "license_type", "equals", "CDL");
        create.StatusCode.Should().Be(HttpStatusCode.OK);
        var ruleId = (await Data(create)).GetProperty("id").GetGuid();

        var update = await PostRuleAsync(auth.Client, templateId, "license_type", "equals", null, id: ruleId);

        update.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ErrorCode(update)).Should().Be("validation.expected_value_required");

        // The stored rule keeps its valid expected value — the rejected edit did not land.
        await using var db = CreateSystemDb();
        (await db.ComplianceRules.SingleAsync(r => r.Id == ruleId)).ExpectedValue.Should().Be("CDL");
    }

    // ---- #373 / ADR 0045: the rule side of the ordinal documentType comparison --------------------

    [Theory]
    [InlineData("coi", "coi")]           // already canonical — passes through unmangled
    [InlineData("license", "license")]
    [InlineData("COI", "coi")]           // the headline shape: folded, not rejected
    [InlineData("  Permit  ", "permit")] // mixed case + padding
    public async Task A_rules_document_type_is_stored_canonically(string sent, string stored)
    {
        // ComplianceCheckService's applicable-rules filter is `r.DocumentType == doc.DocumentType`, an
        // ORDINAL comparison, and ExtractionWorker.PersistSuccess now guarantees the DOCUMENT side is
        // always canonical (#373) — so a rule stored verbatim as "COI" governs literally nothing and can
        // no longer even accidentally match a document the upload path once stored as "COI". /api/compliance
        // is reachable without the rules page, so the API is the authoritative guard.
        var auth = await RegisterAndLoginAsync();
        var templateId = await CreateTemplateAsync(auth.Client);

        var resp = await PostRuleAsync(
            auth.Client, templateId, "general_liability_limit", "min_value", "1000000", documentType: sent);

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        await using var db = CreateSystemDb();
        (await db.ComplianceRules.SingleAsync(r => r.ComplianceTemplateId == templateId))
            .DocumentType.Should().Be(stored);
    }

    [Theory]
    [InlineData("banana")]
    [InlineData("Certificate of Insurance")] // plausible prose that is still not the vocabulary
    [InlineData("")]
    [InlineData(null)]
    public async Task A_rule_with_an_unrecognized_document_type_is_rejected_400(string? sent)
    {
        // REJECTED, not folded to "other" — the deliberate asymmetry with the extraction worker. A worker
        // parsing a model response has no one to ask; a human editing a checklist does, and silently
        // retyping a compliance RULE would change WHAT IT GOVERNS (a requirement that quietly stops
        // applying is exactly the silent-never-graded class #373 exists to close).
        var auth = await RegisterAndLoginAsync();
        var templateId = await CreateTemplateAsync(auth.Client);

        var resp = await PostRuleAsync(
            auth.Client, templateId, "general_liability_limit", "min_value", "1000000", documentType: sent);

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ErrorCode(resp)).Should().Be("validation.document_type");

        // The reject blocks the write — no rule row lands.
        await using var db = CreateSystemDb();
        (await db.ComplianceRules.CountAsync(r => r.ComplianceTemplateId == templateId)).Should().Be(0);
    }

    [Fact]
    public async Task Editing_a_rule_to_an_unrecognized_document_type_is_rejected_400()
    {
        // The guard covers the UPDATE path too (same endpoint): a well-formed "coi" rule cannot be edited
        // into a type that governs nothing.
        var auth = await RegisterAndLoginAsync();
        var templateId = await CreateTemplateAsync(auth.Client);

        var create = await PostRuleAsync(auth.Client, templateId, "license_type", "equals", "CDL");
        create.StatusCode.Should().Be(HttpStatusCode.OK);
        var ruleId = (await Data(create)).GetProperty("id").GetGuid();

        var update = await PostRuleAsync(
            auth.Client, templateId, "license_type", "equals", "CDL", id: ruleId, documentType: "banana");

        update.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ErrorCode(update)).Should().Be("validation.document_type");
        await using var db = CreateSystemDb();
        (await db.ComplianceRules.SingleAsync(r => r.Id == ruleId)).DocumentType.Should().Be("coi");
    }
}
