using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using CompliDrop.Api.Configuration;
using CompliDrop.Api.Services.Extraction;
using CompliDrop.Api.Tests.ExtractionFixtures;
using CompliDrop.Api.Tests.TestHelpers;
using FluentAssertions;

namespace CompliDrop.Api.Tests;

/// <summary>
/// Exercises <see cref="AnthropicExtractionClient"/> against a stubbed HTTP boundary: request shaping
/// (tool-use definition, forced tool_choice, prompt-caching headers + ephemeral cache_control, the
/// provider-agnostic system prompt), response parsing (the fixture round-trip + edges), token-usage/cost
/// computation, and malformed/empty-response handling. No web host, no network.
/// </summary>
public sealed class AnthropicExtractionClientTests
{
    private static string Json(JsonObject o) => o.ToJsonString();

    // ---- Acceptance #1/#2: fixture round-trip (canned response → expected fields) ----

    [Theory]
    [MemberData(nameof(ExtractionFixtureHarness.AllFixtures), MemberType = typeof(ExtractionFixtureHarness))]
    public async Task Maps_canned_tool_use_response_to_each_fixtures_expected_fields(string fixtureName)
    {
        // Round-trip from expected.yaml — see the Gemini equivalent for why fuzzy tolerance is trivial here.
        var expected = ExtractionFixtureHarness.Load(fixtureName);
        var handler = new StubHttpMessageHandler(HttpStatusCode.OK, Json(ExtractionFixtureHarness.AnthropicResponse(expected)));
        var client = ExtractionClientBuilder.Anthropic(handler);

        var result = await client.ExtractAsync(
            ExtractionClientBuilder.Ocr(ExtractionFixtureHarness.SyntheticOcrText(expected)),
            imageStream: null, "application/pdf", expected.DocumentType, default);

        result.DocumentType.Should().Be(expected.DocumentType);
        result.DocumentSubType.Should().Be(expected.DocumentSubType);
        result.Fields.Should().HaveCount(expected.Fields.Count);
        ExtractionFixtureHarness.AssertFieldsMatch(expected, result);
    }

    // ---- Acceptance #2: request shaping ----

    [Fact]
    public async Task Request_uses_tool_use_with_prompt_caching_headers()
    {
        var handler = new StubHttpMessageHandler(HttpStatusCode.OK, Json(ExtractionFixtureHarness.AnthropicResponse(ExtractionFixtureHarness.Minimal())));
        var client = ExtractionClientBuilder.Anthropic(handler,
            new AnthropicSettings { ApiKey = "sk-ant-xyz", Model = "claude-haiku-4-5-20251001", MaxTokens = 1500 });

        await client.ExtractAsync(ExtractionClientBuilder.Ocr("INSURED ACME OCR"), null, "application/pdf", "coi", default);

        handler.LastRequest!.RequestUri!.ToString().Should().Be("https://api.anthropic.com/v1/messages");
        handler.LastRequest.Headers.GetValues("x-api-key").Should().ContainSingle().Which.Should().Be("sk-ant-xyz");
        handler.LastRequest.Headers.GetValues("anthropic-version").Should().ContainSingle().Which.Should().Be("2023-06-01");
        handler.LastRequest.Headers.GetValues("anthropic-beta").Should().ContainSingle().Which.Should().Be("prompt-caching-2024-07-31");

        var body = JsonNode.Parse(handler.LastRequestBody)!.AsObject();
        body["model"]!.GetValue<string>().Should().Be("claude-haiku-4-5-20251001");
        body["max_tokens"]!.GetValue<int>().Should().Be(1500);
        body["system"]![0]!["text"]!.GetValue<string>().Should().Be(ExtractionPrompts.SystemPrompt);
        body["system"]![0]!["cache_control"]!["type"]!.GetValue<string>().Should().Be("ephemeral");
        body["tool_choice"]!["type"]!.GetValue<string>().Should().Be("tool");
        body["tool_choice"]!["name"]!.GetValue<string>().Should().Be("record_extraction");

        var tool = body["tools"]![0]!.AsObject();
        tool["name"]!.GetValue<string>().Should().Be("record_extraction");
        tool["input_schema"]!["required"]!.AsArray().Select(n => n!.GetValue<string>())
            .Should().Contain(new[] { "documentType", "fields", "needsReprocessing" });

        body["messages"]![0]!["role"]!.GetValue<string>().Should().Be("user");
        body["messages"]![0]!["content"]![0]!["text"]!.GetValue<string>()
            .Should().Contain("Document type hint: coi").And.Contain("INSURED ACME OCR");
    }

    [Fact]
    public async Task Throws_when_api_key_missing()
    {
        var client = ExtractionClientBuilder.Anthropic(new StubHttpMessageHandler(), new AnthropicSettings { ApiKey = "" });
        Func<Task> act = () => client.ExtractAsync(ExtractionClientBuilder.Ocr(), null, "application/pdf", null, default);
        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*API key not configured*");
    }

    [Fact]
    public async Task Image_is_attached_only_for_image_content_types()
    {
        var bytes = new byte[] { 9, 8, 7 };

        var imgHandler = new StubHttpMessageHandler(HttpStatusCode.OK, Json(ExtractionFixtureHarness.AnthropicResponse(ExtractionFixtureHarness.Minimal())));
        await ExtractionClientBuilder.Anthropic(imgHandler).ExtractAsync(ExtractionClientBuilder.Ocr(), new MemoryStream(bytes), "image/png", null, default);
        var imgContent = JsonNode.Parse(imgHandler.LastRequestBody)!["messages"]![0]!["content"]!.AsArray();
        imgContent.Should().HaveCount(2);
        imgContent[1]!["type"]!.GetValue<string>().Should().Be("image");
        imgContent[1]!["source"]!["media_type"]!.GetValue<string>().Should().Be("image/png");
        imgContent[1]!["source"]!["data"]!.GetValue<string>().Should().Be(Convert.ToBase64String(bytes));

        // A PDF stream must NOT be attached as an image block — text only.
        var pdfHandler = new StubHttpMessageHandler(HttpStatusCode.OK, Json(ExtractionFixtureHarness.AnthropicResponse(ExtractionFixtureHarness.Minimal())));
        await ExtractionClientBuilder.Anthropic(pdfHandler).ExtractAsync(ExtractionClientBuilder.Ocr(), new MemoryStream(bytes), "application/pdf", null, default);
        JsonNode.Parse(pdfHandler.LastRequestBody)!["messages"]![0]!["content"]!.AsArray().Should().ContainSingle();
    }

    // ---- Acceptance #5: usage + cost ----

    [Fact]
    public async Task Token_usage_and_cost_are_computed_from_usage()
    {
        var handler = new StubHttpMessageHandler(HttpStatusCode.OK,
            Json(ExtractionFixtureHarness.AnthropicResponse(ExtractionFixtureHarness.Minimal(), inputTokens: 2_000_000, outputTokens: 1_000_000)));

        var result = await ExtractionClientBuilder.Anthropic(handler).ExtractAsync(ExtractionClientBuilder.Ocr(), null, "application/pdf", null, default);

        result.Usage.Should().NotBeNull();
        result.Usage!.InputTokens.Should().Be(2_000_000);
        result.Usage.OutputTokens.Should().Be(1_000_000);
        // Distinct token counts so a transposed input/output rate would change the total:
        // $1/1M input * 2M + $5/1M output * 1M = 2 + 5.
        result.Usage.EstimatedCostUsd.Should().Be(7m);
    }

    [Fact]
    public async Task Usage_defaults_to_zero_when_response_omits_usage()
    {
        // Unlike Gemini (null Usage), the Anthropic client always returns a Usage record, defaulting to 0/0.
        var response = ExtractionFixtureHarness.AnthropicResponse(ExtractionFixtureHarness.Minimal());
        response.Remove("usage");
        var handler = new StubHttpMessageHandler(HttpStatusCode.OK, Json(response));

        var result = await ExtractionClientBuilder.Anthropic(handler).ExtractAsync(ExtractionClientBuilder.Ocr(), null, "application/pdf", null, default);

        result.Usage.Should().NotBeNull();
        result.Usage!.InputTokens.Should().Be(0);
        result.Usage.OutputTokens.Should().Be(0);
        result.Usage.EstimatedCostUsd.Should().Be(0m);
    }

    // ---- Response parsing edges ----

    [Fact]
    public async Task Confidence_is_clamped_and_defaulted()
    {
        var payload = new JsonObject
        {
            ["documentType"] = "coi",
            ["needsReprocessing"] = false,
            ["fields"] = new JsonArray
            {
                new JsonObject { ["name"] = "over", ["value"] = "a", ["type"] = "text", ["confidence"] = 1.5 },
                new JsonObject { ["name"] = "under", ["value"] = "b", ["type"] = "text", ["confidence"] = -0.2 },
                new JsonObject { ["name"] = "missing", ["value"] = "c", ["type"] = "text" },
            },
        };
        var handler = new StubHttpMessageHandler(HttpStatusCode.OK, Json(ExtractionFixtureHarness.AnthropicResponseFromPayload(payload)));

        var result = await ExtractionClientBuilder.Anthropic(handler).ExtractAsync(ExtractionClientBuilder.Ocr(), null, "application/pdf", null, default);

        result.Fields.Single(f => f.Name == "over").Confidence.Should().Be(1.0);
        result.Fields.Single(f => f.Name == "under").Confidence.Should().Be(0.0);
        result.Fields.Single(f => f.Name == "missing").Confidence.Should().Be(0.5);
    }

    [Fact]
    public async Task Field_type_is_parsed_and_defaults_to_text()
    {
        var payload = new JsonObject
        {
            ["documentType"] = "license",
            ["needsReprocessing"] = false,
            ["fields"] = new JsonArray
            {
                new JsonObject { ["name"] = "issue_date", ["value"] = "2025-01-01", ["type"] = "date", ["confidence"] = 0.9 },
                new JsonObject { ["name"] = "limit", ["value"] = "1000000", ["type"] = "currency", ["confidence"] = 0.9 },
                new JsonObject { ["name"] = "note", ["value"] = "x", ["confidence"] = 0.9 }, // type omitted
            },
        };
        var handler = new StubHttpMessageHandler(HttpStatusCode.OK, Json(ExtractionFixtureHarness.AnthropicResponseFromPayload(payload)));

        var result = await ExtractionClientBuilder.Anthropic(handler).ExtractAsync(ExtractionClientBuilder.Ocr(), null, "application/pdf", null, default);

        result.Fields.Single(f => f.Name == "issue_date").Type.Should().Be("date");
        result.Fields.Single(f => f.Name == "limit").Type.Should().Be("currency");
        result.Fields.Single(f => f.Name == "note").Type.Should().Be("text"); // defaulted
    }

    // ---- Acceptance #4: malformed / empty handling ----

    [Fact]
    public async Task Non_json_body_throws_clean_json_exception()
    {
        var client = ExtractionClientBuilder.Anthropic(new StubHttpMessageHandler(HttpStatusCode.OK, "definitely not json"));
        Func<Task> act = () => client.ExtractAsync(ExtractionClientBuilder.Ocr(), null, "application/pdf", null, default);
        await act.Should().ThrowAsync<JsonException>();
    }

    [Fact]
    public async Task Missing_content_throws_a_catchable_exception()
    {
        // Worker catches any Exception → marks Failed; assert a clean throw, never a NullReferenceException.
        var client = ExtractionClientBuilder.Anthropic(new StubHttpMessageHandler(HttpStatusCode.OK, "{}"));
        Func<Task> act = () => client.ExtractAsync(ExtractionClientBuilder.Ocr(), null, "application/pdf", null, default);
        (await act.Should().ThrowAsync<Exception>()).Which.Should().NotBeOfType<NullReferenceException>();
    }

    [Theory]
    [InlineData("{\"content\":[]}")]                                          // no blocks at all
    [InlineData("{\"content\":[{\"type\":\"text\",\"text\":\"hi\"}]}")]      // text only, no tool_use
    public async Task Response_without_tool_use_block_throws_with_clear_message(string body)
    {
        var client = ExtractionClientBuilder.Anthropic(new StubHttpMessageHandler(HttpStatusCode.OK, body));
        Func<Task> act = () => client.ExtractAsync(ExtractionClientBuilder.Ocr(), null, "application/pdf", null, default);
        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*missing tool_use block*");
    }

    [Fact]
    public async Task Non_success_status_throws_http_request_exception()
    {
        var client = ExtractionClientBuilder.Anthropic(new StubHttpMessageHandler(HttpStatusCode.TooManyRequests, "{\"error\":\"rate limited\"}"));
        Func<Task> act = () => client.ExtractAsync(ExtractionClientBuilder.Ocr(), null, "application/pdf", null, default);
        await act.Should().ThrowAsync<HttpRequestException>();
    }

    [Fact]
    public async Task Valid_but_empty_tool_input_degrades_to_empty_other_extraction()
    {
        var handler = new StubHttpMessageHandler(HttpStatusCode.OK, Json(ExtractionFixtureHarness.AnthropicResponseFromPayload(new JsonObject())));

        var result = await ExtractionClientBuilder.Anthropic(handler).ExtractAsync(ExtractionClientBuilder.Ocr(), null, "application/pdf", null, default);

        result.DocumentType.Should().Be("other");
        result.DocumentSubType.Should().BeNull();
        result.Fields.Should().BeEmpty();
        result.NeedsReprocessing.Should().BeFalse();
    }
}
