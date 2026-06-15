namespace CompliDrop.Api.Services.Extraction;

/// <summary>
/// Thrown by an <see cref="IExtractionClient"/> when an attempt failed for a DETERMINISTIC reason —
/// one that a byte-identical retry would hit again (e.g. the model truncated its output at the token
/// cap, or the content was blocked). The <see cref="BackgroundServices.ExtractionWorker"/> fails such
/// a document immediately rather than re-running OCR + LLM up to the retry budget for no benefit
/// (#259, problem 1). Transient failures (5xx, timeouts, malformed-but-non-deterministic responses)
/// stay ordinary exceptions and keep their retries.
/// </summary>
public sealed class NonRetryableExtractionException(string code, string message)
    : Exception(message)
{
    /// <summary>Stable machine code persisted as the <c>ProcessingError</c> prefix (e.g. <c>extraction.token_limit</c>).</summary>
    public string Code { get; } = code;
}
