using CompliDrop.Api.Configuration;
using CompliDrop.Api.Services.Extraction;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace CompliDrop.Api.Tests.TestHelpers;

/// <summary>
/// Constructs the real extraction clients wired to a stub HTTP handler — no web host, no network.
/// Sensible defaults let the round-trip/parse tests stay terse; the shaping/guard tests override the
/// settings (endpoint, keys, token provider) they care about.
/// </summary>
public static class ExtractionClientBuilder
{
    public static GeminiExtractionClient Gemini(
        HttpMessageHandler handler,
        GeminiSettings? gemini = null,
        DocumentAiSettings? docAi = null,
        IGoogleAuthTokenProvider? auth = null) =>
        new(
            new StubHttpClientFactory(handler),
            auth ?? new FakeGoogleAuthTokenProvider(),
            Options.Create(gemini ?? new GeminiSettings { Endpoint = "aistudio", ApiKey = "test-gemini-key" }),
            Options.Create(docAi ?? new DocumentAiSettings { ProjectId = "test-project" }),
            NullLogger<GeminiExtractionClient>.Instance);

    public static AnthropicExtractionClient Anthropic(
        HttpMessageHandler handler,
        AnthropicSettings? settings = null) =>
        new(
            new StubHttpClientFactory(handler),
            Options.Create(settings ?? new AnthropicSettings { ApiKey = "test-anthropic-key" }),
            NullLogger<AnthropicExtractionClient>.Instance);

    /// <summary>A minimal valid OCR input for tests that don't care about the OCR content.</summary>
    public static OcrResult Ocr(string text = "OCR TEXT SAMPLE") => new(text, PageCount: 1, AvgConfidence: 0.9, EstimatedCostUsd: 0m);
}
