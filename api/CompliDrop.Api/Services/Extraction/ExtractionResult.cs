namespace CompliDrop.Api.Services.Extraction;

public record ExtractionResult(
    string DocumentType,
    string? DocumentSubType,
    IReadOnlyList<ExtractedField> Fields,
    bool NeedsReprocessing,
    ExtractionUsage? Usage);

public record ExtractedField(
    string Name,
    string Value,
    string Type,
    double Confidence);

public record ExtractionUsage(
    int InputTokens,
    int OutputTokens,
    decimal EstimatedCostUsd);

public record OcrResult(
    string Text,
    int PageCount,
    double AvgConfidence,
    decimal EstimatedCostUsd);

public enum ExtractionProvider
{
    Gemini,
    Anthropic
}
