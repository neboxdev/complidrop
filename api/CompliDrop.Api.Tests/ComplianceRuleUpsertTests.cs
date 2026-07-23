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
        HttpClient client, Guid templateId, string fieldName, string @operator, string? expectedValue, Guid? id = null) =>
        client.PostAsJsonAsync($"/api/compliance/templates/{templateId}/rules", new
        {
            id,
            documentType = "coi",
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
}
