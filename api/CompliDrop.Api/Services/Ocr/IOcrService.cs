using CompliDrop.Api.Services.Extraction;

namespace CompliDrop.Api.Services.Ocr;

public interface IOcrService
{
    bool IsEnabled { get; }
    Task<OcrResult> OcrAsync(Stream content, string contentType, CancellationToken ct);
}
