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
/// Exercises <see cref="GeminiExtractionClient"/> against a stubbed HTTP boundary: request shaping
/// (AI Studio vs Vertex URL/auth, JSON-schema structured output, provider-agnostic system prompt),
/// response parsing (the fixture round-trip + confidence/field edges), token-usage/cost computation,
/// and malformed/empty-response handling. No web host, no network.
/// </summary>
public sealed class GeminiExtractionClientTests
{
    private static string Json(JsonObject o) => o.ToJsonString();

    // ---- Acceptance #1/#2: fixture round-trip (canned response → expected fields) ----

    [Theory]
    [MemberData(nameof(ExtractionFixtureHarness.AllFixtures), MemberType = typeof(ExtractionFixtureHarness))]
    public async Task Maps_canned_response_to_each_fixtures_expected_fields(string fixtureName)
    {
        var expected = ExtractionFixtureHarness.Load(fixtureName);
        var handler = new StubHttpMessageHandler(HttpStatusCode.OK, Json(ExtractionFixtureHarness.GeminiResponse(expected)));
        var client = ExtractionClientBuilder.Gemini(handler);

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
    public async Task AiStudio_request_targets_generativelanguage_with_api_key_and_json_schema()
    {
        var handler = new StubHttpMessageHandler(HttpStatusCode.OK, Json(ExtractionFixtureHarness.GeminiResponse(ExtractionFixtureHarness.Minimal())));
        var client = ExtractionClientBuilder.Gemini(handler,
            new GeminiSettings { Endpoint = "aistudio", ApiKey = "k-123", Model = "gemini-2.5-flash", MaxTokens = 1234 });

        await client.ExtractAsync(ExtractionClientBuilder.Ocr("POLICY GL-1 OCR TEXT"), null, "application/pdf", "coi", default);

        handler.LastRequest!.RequestUri!.ToString().Should()
            .StartWith("https://generativelanguage.googleapis.com/v1beta/models/gemini-2.5-flash:generateContent")
            .And.Contain("key=k-123");

        var body = JsonNode.Parse(handler.LastRequestBody)!.AsObject();
        body["systemInstruction"]!["parts"]![0]!["text"]!.GetValue<string>().Should().Be(ExtractionPrompts.SystemPrompt);
        body["generationConfig"]!["temperature"]!.GetValue<double>().Should().Be(0.0);
        body["generationConfig"]!["maxOutputTokens"]!.GetValue<int>().Should().Be(1234);
        body["generationConfig"]!["responseMimeType"]!.GetValue<string>().Should().Be("application/json");

        var schema = body["generationConfig"]!["responseSchema"]!.AsObject();
        schema["required"]!.AsArray().Select(n => n!.GetValue<string>())
            .Should().Contain(new[] { "documentType", "fields", "needsReprocessing" });
        schema["properties"]!["documentType"]!["enum"]!.AsArray().Select(n => n!.GetValue<string>())
            .Should().Contain("coi");

        body["contents"]![0]!["role"]!.GetValue<string>().Should().Be("user");
        body["contents"]![0]!["parts"]![0]!["text"]!.GetValue<string>()
            .Should().Contain("Document type hint: coi").And.Contain("POLICY GL-1 OCR TEXT");
    }

    [Fact]
    public async Task Vertex_request_targets_aiplatform_with_bearer_token()
    {
        var handler = new StubHttpMessageHandler(HttpStatusCode.OK, Json(ExtractionFixtureHarness.GeminiResponse(ExtractionFixtureHarness.Minimal())));
        var client = ExtractionClientBuilder.Gemini(handler,
            new GeminiSettings { Endpoint = "vertex", Model = "gemini-2.5-flash", Location = "us-central1" },
            new DocumentAiSettings { ProjectId = "proj-42" },
            new FakeGoogleAuthTokenProvider(token: "vertex-token-abc", isConfigured: true));

        await client.ExtractAsync(ExtractionClientBuilder.Ocr(), null, "application/pdf", null, default);

        handler.LastRequest!.RequestUri!.ToString().Should().Be(
            "https://us-central1-aiplatform.googleapis.com/v1/projects/proj-42/locations/us-central1/publishers/google/models/gemini-2.5-flash:generateContent");
        handler.LastRequest.Headers.Authorization!.Scheme.Should().Be("Bearer");
        handler.LastRequest.Headers.Authorization.Parameter.Should().Be("vertex-token-abc");
    }

    [Theory]
    [InlineData("", true)]    // projectId missing
    [InlineData("proj", false)] // auth not configured
    public async Task Vertex_throws_when_not_configured(string projectId, bool authConfigured)
    {
        var client = ExtractionClientBuilder.Gemini(new StubHttpMessageHandler(),
            new GeminiSettings { Endpoint = "vertex" },
            new DocumentAiSettings { ProjectId = projectId },
            new FakeGoogleAuthTokenProvider(isConfigured: authConfigured));

        Func<Task> act = () => client.ExtractAsync(ExtractionClientBuilder.Ocr(), null, "application/pdf", null, default);
        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*Vertex AI not configured*");
    }

    [Fact]
    public async Task AiStudio_throws_when_api_key_missing()
    {
        var client = ExtractionClientBuilder.Gemini(new StubHttpMessageHandler(),
            new GeminiSettings { Endpoint = "aistudio", ApiKey = "" });

        Func<Task> act = () => client.ExtractAsync(ExtractionClientBuilder.Ocr(), null, "application/pdf", null, default);
        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*API key not configured*");
    }

    [Fact]
    public async Task Image_is_attached_as_inline_data()
    {
        var handler = new StubHttpMessageHandler(HttpStatusCode.OK, Json(ExtractionFixtureHarness.GeminiResponse(ExtractionFixtureHarness.Minimal())));
        var client = ExtractionClientBuilder.Gemini(handler);
        var bytes = new byte[] { 1, 2, 3, 4, 5 };

        await client.ExtractAsync(ExtractionClientBuilder.Ocr(), new MemoryStream(bytes), "image/png", null, default);

        var parts = JsonNode.Parse(handler.LastRequestBody)!["contents"]![0]!["parts"]!.AsArray();
        parts.Should().HaveCount(2);
        parts[1]!["inlineData"]!["mimeType"]!.GetValue<string>().Should().Be("image/png");
        parts[1]!["inlineData"]!["data"]!.GetValue<string>().Should().Be(Convert.ToBase64String(bytes));
    }

    // ---- Prompt building (shared with Anthropic; pinned here) ----

    [Theory]
    [InlineData("other")]
    [InlineData(null)]
    public async Task No_document_type_hint_for_other_or_null(string? hint)
    {
        var handler = new StubHttpMessageHandler(HttpStatusCode.OK, Json(ExtractionFixtureHarness.GeminiResponse(ExtractionFixtureHarness.Minimal())));
        await ExtractionClientBuilder.Gemini(handler).ExtractAsync(ExtractionClientBuilder.Ocr("body text"), null, "application/pdf", hint, default);

        JsonNode.Parse(handler.LastRequestBody)!["contents"]![0]!["parts"]![0]!["text"]!.GetValue<string>()
            .Should().NotContain("Document type hint").And.Contain("body text");
    }

    [Fact]
    public async Task Empty_ocr_text_uses_placeholder()
    {
        var handler = new StubHttpMessageHandler(HttpStatusCode.OK, Json(ExtractionFixtureHarness.GeminiResponse(ExtractionFixtureHarness.Minimal())));
        await ExtractionClientBuilder.Gemini(handler).ExtractAsync(ExtractionClientBuilder.Ocr(""), null, "application/pdf", null, default);

        JsonNode.Parse(handler.LastRequestBody)!["contents"]![0]!["parts"]![0]!["text"]!.GetValue<string>()
            .Should().Contain("No OCR text was extracted");
    }

    [Fact]
    public async Task Long_ocr_text_is_truncated_to_20000_chars()
    {
        var handler = new StubHttpMessageHandler(HttpStatusCode.OK, Json(ExtractionFixtureHarness.GeminiResponse(ExtractionFixtureHarness.Minimal())));
        var huge = new string('A', 20000) + "OVERFLOW_SENTINEL";
        await ExtractionClientBuilder.Gemini(handler).ExtractAsync(ExtractionClientBuilder.Ocr(huge), null, "application/pdf", null, default);

        var userText = JsonNode.Parse(handler.LastRequestBody)!["contents"]![0]!["parts"]![0]!["text"]!.GetValue<string>();
        userText.Should().Contain(new string('A', 1000)).And.NotContain("OVERFLOW_SENTINEL");
    }

    // ---- Acceptance #5: usage + cost ----

    [Fact]
    public async Task Token_usage_and_cost_are_computed_from_usage_metadata()
    {
        var handler = new StubHttpMessageHandler(HttpStatusCode.OK,
            Json(ExtractionFixtureHarness.GeminiResponse(ExtractionFixtureHarness.Minimal(), promptTokens: 1_000_000, candidatesTokens: 1_000_000)));

        var result = await ExtractionClientBuilder.Gemini(handler).ExtractAsync(ExtractionClientBuilder.Ocr(), null, "application/pdf", null, default);

        result.Usage.Should().NotBeNull();
        result.Usage!.InputTokens.Should().Be(1_000_000);
        result.Usage.OutputTokens.Should().Be(1_000_000);
        // $0.075/1M input + $0.30/1M output.
        result.Usage.EstimatedCostUsd.Should().Be(0.375m);
    }

    [Fact]
    public async Task Usage_is_null_when_response_omits_usage_metadata()
    {
        var response = ExtractionFixtureHarness.GeminiResponse(ExtractionFixtureHarness.Minimal());
        response.Remove("usageMetadata");
        var handler = new StubHttpMessageHandler(HttpStatusCode.OK, Json(response));

        var result = await ExtractionClientBuilder.Gemini(handler).ExtractAsync(ExtractionClientBuilder.Ocr(), null, "application/pdf", null, default);

        result.Usage.Should().BeNull();
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
        var handler = new StubHttpMessageHandler(HttpStatusCode.OK, Json(ExtractionFixtureHarness.GeminiResponseFromPayload(payload)));

        var result = await ExtractionClientBuilder.Gemini(handler).ExtractAsync(ExtractionClientBuilder.Ocr(), null, "application/pdf", null, default);

        result.Fields.Single(f => f.Name == "over").Confidence.Should().Be(1.0);
        result.Fields.Single(f => f.Name == "under").Confidence.Should().Be(0.0);
        result.Fields.Single(f => f.Name == "missing").Confidence.Should().Be(0.5);
    }

    [Fact]
    public async Task Fields_with_blank_names_are_dropped_and_flags_parsed()
    {
        var payload = new JsonObject
        {
            ["documentType"] = "permit",
            ["documentSubType"] = "commercial",
            ["needsReprocessing"] = true,
            ["fields"] = new JsonArray
            {
                new JsonObject { ["name"] = "", ["value"] = "ignored", ["type"] = "text", ["confidence"] = 0.9 },
                new JsonObject { ["name"] = "permit_number", ["value"] = "BP-1", ["type"] = "text", ["confidence"] = 0.9 },
            },
        };
        var handler = new StubHttpMessageHandler(HttpStatusCode.OK, Json(ExtractionFixtureHarness.GeminiResponseFromPayload(payload)));

        var result = await ExtractionClientBuilder.Gemini(handler).ExtractAsync(ExtractionClientBuilder.Ocr(), null, "application/pdf", null, default);

        result.DocumentType.Should().Be("permit");
        result.DocumentSubType.Should().Be("commercial");
        result.NeedsReprocessing.Should().BeTrue();
        result.Fields.Should().ContainSingle().Which.Name.Should().Be("permit_number");
    }

    // ---- Acceptance #4: malformed / empty handling ----

    [Fact]
    public async Task Non_json_body_throws_clean_json_exception()
    {
        var client = ExtractionClientBuilder.Gemini(new StubHttpMessageHandler(HttpStatusCode.OK, "this is not json"));
        Func<Task> act = () => client.ExtractAsync(ExtractionClientBuilder.Ocr(), null, "application/pdf", null, default);
        await act.Should().ThrowAsync<JsonException>();
    }

    [Fact]
    public async Task Inner_text_that_is_not_json_throws_clean_json_exception()
    {
        var response = new JsonObject
        {
            ["candidates"] = new JsonArray
            {
                new JsonObject { ["content"] = new JsonObject { ["parts"] = new JsonArray { new JsonObject { ["text"] = "<<<not json>>>" } } } },
            },
        };
        var client = ExtractionClientBuilder.Gemini(new StubHttpMessageHandler(HttpStatusCode.OK, Json(response)));
        Func<Task> act = () => client.ExtractAsync(ExtractionClientBuilder.Ocr(), null, "application/pdf", null, default);
        await act.Should().ThrowAsync<JsonException>();
    }

    [Theory]
    [InlineData("{}")]                       // no candidates
    [InlineData("{\"candidates\":[]}")]      // empty candidates
    public async Task Structurally_missing_fields_throw_a_catchable_exception(string body)
    {
        // The worker catches any Exception and marks the document Failed (ExtractionWorker.cs:196-211),
        // so "surfaced as a failed extraction" means a clean throw — never a NullReferenceException.
        var client = ExtractionClientBuilder.Gemini(new StubHttpMessageHandler(HttpStatusCode.OK, body));
        Func<Task> act = () => client.ExtractAsync(ExtractionClientBuilder.Ocr(), null, "application/pdf", null, default);
        (await act.Should().ThrowAsync<Exception>()).Which.Should().NotBeOfType<NullReferenceException>();
    }

    [Fact]
    public async Task Non_success_status_throws_http_request_exception()
    {
        var client = ExtractionClientBuilder.Gemini(new StubHttpMessageHandler(HttpStatusCode.InternalServerError, "{\"error\":\"boom\"}"));
        Func<Task> act = () => client.ExtractAsync(ExtractionClientBuilder.Ocr(), null, "application/pdf", null, default);
        await act.Should().ThrowAsync<HttpRequestException>();
    }

    [Fact]
    public async Task Valid_but_empty_payload_degrades_to_empty_other_extraction()
    {
        // Valid JSON, no usable content: the client must not crash — it returns an empty "other" result.
        var handler = new StubHttpMessageHandler(HttpStatusCode.OK, Json(ExtractionFixtureHarness.GeminiResponseFromPayload(new JsonObject())));

        var result = await ExtractionClientBuilder.Gemini(handler).ExtractAsync(ExtractionClientBuilder.Ocr(), null, "application/pdf", null, default);

        result.DocumentType.Should().Be("other");
        result.Fields.Should().BeEmpty();
        result.NeedsReprocessing.Should().BeFalse();
    }
}
