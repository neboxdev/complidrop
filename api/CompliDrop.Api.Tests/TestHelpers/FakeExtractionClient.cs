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

    /// <summary>
    /// Message carried by the <see cref="ThrowOnExtract"/> exception. Overridden when the message's own
    /// LENGTH is the contract under test — <c>ProcessingError</c> is <c>varchar(2000)</c> and stores
    /// <c>"{code}: {message}"</c>, so a long provider/EF message used to 22001 the failure-bookkeeping
    /// write itself (#373).
    /// </summary>
    public string ThrowMessage { get; set; } = DefaultThrowMessage;

    private const string DefaultThrowMessage = "Simulated extraction failure.";

    /// <summary>
    /// When true, every call throws a <see cref="NonRetryableExtractionException"/> — simulates a
    /// deterministic failure (e.g. token-cap truncation) the worker must fail fast on, not retry.
    /// </summary>
    public bool ThrowNonRetryable { get; set; }

    /// <summary>
    /// When &gt; zero, the call awaits this delay (honoring the cancellation token) before returning —
    /// lets a test drive the worker's per-attempt timeout by hanging the extraction boundary.
    /// </summary>
    public TimeSpan ExtractDelay { get; set; } = TimeSpan.Zero;

    /// <summary>Result returned on a successful (non-throwing) call.</summary>
    public ExtractionResult Result { get; set; } = DefaultResult;

    /// <summary>
    /// The <c>documentTypeHint</c> of the most recent call, VERBATIM. The worker hands the stored
    /// <c>Document.DocumentType</c> straight through — suppressing the hint for <c>other</c> / unknown
    /// values is <c>ExtractionPrompts.BuildUserPrompt</c>'s job and only its job (#384, ADR 0051), so a
    /// re-introduced pre-filter at the call site is visible here.
    /// </summary>
    public string? LastDocumentTypeHint { get; private set; }

    public async Task<ExtractionResult> ExtractAsync(
        OcrResult ocr,
        Stream? imageStream,
        string imageContentType,
        string? documentTypeHint,
        CancellationToken ct)
    {
        ExtractCallCount++;
        LastDocumentTypeHint = documentTypeHint;
        if (ThrowNonRetryable)
            throw new NonRetryableExtractionException("extraction.token_limit", "Simulated deterministic failure.");
        if (ExtractDelay > TimeSpan.Zero)
            await Task.Delay(ExtractDelay, ct); // throws OperationCanceledException when the attempt times out
        if (ThrowOnExtract)
            throw new InvalidOperationException(ThrowMessage);
        return Result;
    }

    public void Reset()
    {
        ExtractCallCount = 0;
        ThrowOnExtract = false;
        ThrowMessage = DefaultThrowMessage;
        ThrowNonRetryable = false;
        ExtractDelay = TimeSpan.Zero;
        Result = DefaultResult;
        LastDocumentTypeHint = null;
    }
}
