using CompliDrop.Api.Services.Extraction;

namespace CompliDrop.Api.Tests.TestHelpers;

/// <summary>
/// Controllable <see cref="IExtractionClient"/> for tests. The real Gemini/Anthropic clients make
/// HTTP calls, so the worker tests swap in this fake to make extraction deterministically succeed
/// or throw, and to assert whether it was invoked at all (short-circuit / zombie-guard paths must
/// never reach extraction). Registered as a host singleton so its knobs and call count survive the
/// worker's per-document DI scopes; <see cref="Reset"/> runs between tests via the fixture.
/// </summary>
public sealed class FakeExtractionClient : IExtractionClient
{
    private static ExtractionResult DefaultResult => new(
        DocumentType: "coi",
        DocumentSubType: null,
        Fields:
        [
            new ExtractedField("policy_number", "POL-12345", "string", 0.95),
            new ExtractedField("expiration_date", "2026-12-31", "date", 0.92),
        ],
        NeedsReprocessing: false,
        Usage: new ExtractionUsage(InputTokens: 1200, OutputTokens: 300, EstimatedCostUsd: 0.02m));

    // Gemini is the factory's default provider, so ExtractionClientFactory.Get() selects this fake
    // by preferred-match (not merely the clients.First() fallback) when it's the only registered client.
    public ExtractionProvider Provider => ExtractionProvider.Gemini;

    /// <summary>Number of times <see cref="ExtractAsync"/> was called since the last reset.</summary>
    public int ExtractCallCount { get; private set; }

    /// <summary>When true, every call throws — simulates an extraction boundary that always fails.</summary>
    public bool ThrowOnExtract { get; set; }

    /// <summary>Result returned on a successful (non-throwing) call.</summary>
    public ExtractionResult Result { get; set; } = DefaultResult;

    public Task<ExtractionResult> ExtractAsync(
        OcrResult ocr,
        Stream? imageStream,
        string imageContentType,
        string? documentTypeHint,
        CancellationToken ct)
    {
        ExtractCallCount++;
        if (ThrowOnExtract)
            throw new InvalidOperationException("Simulated extraction failure.");
        return Task.FromResult(Result);
    }

    public void Reset()
    {
        ExtractCallCount = 0;
        ThrowOnExtract = false;
        Result = DefaultResult;
    }
}
