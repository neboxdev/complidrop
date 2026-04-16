namespace CompliDrop.Api.Services.Extraction;

public interface IExtractionClient
{
    ExtractionProvider Provider { get; }
    Task<ExtractionResult> ExtractAsync(
        OcrResult ocr,
        Stream? imageStream,
        string imageContentType,
        string? documentTypeHint,
        CancellationToken ct);
}

public interface IExtractionClientFactory
{
    IExtractionClient Get();
}
