using System.Net;
using System.Text.Json.Nodes;
using CompliDrop.Api.Services.Extraction;
using CompliDrop.Api.Tests.TestHelpers;
using FluentAssertions;

namespace CompliDrop.Api.Tests.ExtractionFixtures;

/// <summary>
/// Self-tests for the fixture harness and the one invariant that spans both providers. The comparator
/// pins exist so the fixture round-trip suite cannot pass vacuously — they prove the harness's field
/// matcher actually rejects wrong values. The discovery guard fails loudly if the fixtures can't be
/// located (rather than the theories silently running zero cases).
/// </summary>
public sealed class ExtractionFixtureHarnessTests
{
    [Fact]
    public void All_five_committed_fixtures_are_discovered()
    {
        ExtractionFixtureHarness.FixtureNames().Should().HaveCountGreaterThanOrEqualTo(5);
    }

    [Fact]
    public void Every_discovered_fixture_loads_with_a_document_type_and_fields()
    {
        foreach (var name in ExtractionFixtureHarness.FixtureNames())
        {
            var expected = ExtractionFixtureHarness.Load(name);
            expected.DocumentType.Should().NotBeNullOrWhiteSpace($"fixture '{name}' must declare a documentType");
            expected.Fields.Should().NotBeEmpty($"fixture '{name}' must declare at least one field");
        }
    }

    [Fact]
    public void Exact_tolerance_rejects_a_different_value()
    {
        var field = new ExpectedField { Expected = "GL-1234567", Tolerance = "exact" };
        ExtractionFixtureHarness.FieldValueMatches(field, "GL-1234567").Should().BeTrue();
        ExtractionFixtureHarness.FieldValueMatches(field, "GL-9999999").Should().BeFalse();
    }

    [Fact]
    public void Fuzzy_tolerance_accepts_a_superset_but_rejects_unrelated_text()
    {
        var field = new ExpectedField { Expected = "Travelers", Tolerance = "fuzzy" };
        ExtractionFixtureHarness.FieldValueMatches(field, "Travelers Indemnity Company").Should().BeTrue();
        ExtractionFixtureHarness.FieldValueMatches(field, "State Farm").Should().BeFalse();
    }

    [Fact]
    public void Fuzzy_tolerance_does_not_vacuously_match_empty_values()
    {
        // Guards the comparator against the "every string contains the empty string" trap.
        ExtractionFixtureHarness.FieldValueMatches(new ExpectedField { Expected = "", Tolerance = "fuzzy" }, "anything")
            .Should().BeFalse();
        ExtractionFixtureHarness.FieldValueMatches(new ExpectedField { Expected = "Travelers", Tolerance = "fuzzy" }, "")
            .Should().BeFalse();
        ExtractionFixtureHarness.FieldValueMatches(new ExpectedField { Expected = "", Tolerance = "fuzzy" }, "")
            .Should().BeTrue();
    }

    [Fact]
    public async Task Both_providers_send_the_identical_provider_agnostic_system_prompt()
    {
        var gHandler = new StubHttpMessageHandler(HttpStatusCode.OK,
            ExtractionFixtureHarness.GeminiResponse(ExtractionFixtureHarness.Minimal()).ToJsonString());
        var aHandler = new StubHttpMessageHandler(HttpStatusCode.OK,
            ExtractionFixtureHarness.AnthropicResponse(ExtractionFixtureHarness.Minimal()).ToJsonString());

        await ExtractionClientBuilder.Gemini(gHandler).ExtractAsync(ExtractionClientBuilder.Ocr(), null, "application/pdf", null, default);
        await ExtractionClientBuilder.Anthropic(aHandler).ExtractAsync(ExtractionClientBuilder.Ocr(), null, "application/pdf", null, default);

        var geminiPrompt = JsonNode.Parse(gHandler.LastRequestBody)!["systemInstruction"]!["parts"]![0]!["text"]!.GetValue<string>();
        var anthropicPrompt = JsonNode.Parse(aHandler.LastRequestBody)!["system"]![0]!["text"]!.GetValue<string>();

        geminiPrompt.Should().Be(ExtractionPrompts.SystemPrompt);
        anthropicPrompt.Should().Be(ExtractionPrompts.SystemPrompt);
        geminiPrompt.Should().Be(anthropicPrompt);
    }
}
