namespace CompliDrop.Api.Services.Extraction;

public record ExtractionResult(
    // NULLABLE deliberately (#373): null means "the provider did not answer" — an ABSENT or JSON-null
    // documentType, which is off-spec since the property is `required` in both providers'
    // structured-output schemas. Both clients used to coerce that absence into the literal "other"
    // before anyone downstream could see it, which is a POSITIVE classification the model never made:
    // ExtractionWorker.PersistSuccess would then overwrite the uploader's deliberate "permit" with
    // "other", leaving zero applicable rules and a document stranded at Pending forever. Keeping the
    // absence distinguishable lets CanonicalDocumentTypes.NormalizeExtracted fall back to the stored
    // type; a model that POSITIVELY answers "other" still arrives as "other" and still overwrites.
    string? DocumentType,
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
