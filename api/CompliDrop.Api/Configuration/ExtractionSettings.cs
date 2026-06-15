namespace CompliDrop.Api.Configuration;

public class ExtractionSettings
{
    public string Provider { get; set; } = "gemini";

    /// <summary>
    /// Hard wall-clock bound on a single extraction attempt (blob download → OCR → LLM → persist).
    /// Caps a wedged attempt so it can't hold its claim — and the row lock under it — open
    /// indefinitely (#259, problems 3 &amp; 4). MUST stay below the worker's 5-minute zombie-reclaim
    /// threshold so the attempt cancels and requeues before a second worker could reclaim the same
    /// document; <see cref="BackgroundServices.ExtractionWorker"/> clamps it into a safe range.
    /// </summary>
    public int AttemptTimeoutSeconds { get; set; } = 180;
}

public class GeminiSettings
{
    public string ApiKey { get; set; } = string.Empty;
    public string Model { get; set; } = "gemini-2.5-flash";
    // 8192, not 2000: a normal one-page COI's schema-constrained `fields` array exceeds 2000 output
    // tokens, so the response was truncated (finishReason=MAX_TOKENS), the partial JSON failed to
    // parse, and the worker burned its whole retry budget on a byte-identical deterministic failure
    // (#259, problem 1). Gemini Flash supports far more output; 8192 fits real documents with margin.
    public int MaxTokens { get; set; } = 8192;
    public string Location { get; set; } = "us-central1";
    public string Endpoint { get; set; } = "vertex"; // "vertex" | "aistudio"
}

public class AnthropicSettings
{
    public string ApiKey { get; set; } = string.Empty;
    public string Model { get; set; } = "claude-haiku-4-5-20251001";
    // Same truncation risk as Gemini (#259, problem 1): the forced-tool `record_extraction` input
    // for a real document exceeds 2000 output tokens. Raised so a normal extraction isn't cut off.
    public int MaxTokens { get; set; } = 8192;
}

public class DocumentAiSettings
{
    public bool Enabled { get; set; } = true;
    public string ProjectId { get; set; } = string.Empty;
    public string Location { get; set; } = "us";
    public string ProcessorId { get; set; } = string.Empty;
    public string? CredentialsPath { get; set; }
    public string? CredentialsJson { get; set; }
}
