using CompliDrop.Api.Services.Extraction;
using CompliDrop.Api.Services.Ocr;

namespace CompliDrop.Api.Tests.TestHelpers;

/// <summary>
/// In-memory <see cref="IOcrService"/> for tests. The real <c>DocumentAiOcrService</c> calls Google
/// Document AI; this fake keeps the worker hermetic and lets tests assert OCR was (or wasn't)
/// invoked. Defaults to <c>IsEnabled = false</c> to mirror the unconfigured production default — the
/// worker then skips OCR and feeds an empty <see cref="OcrResult"/> into extraction. Registered as a
/// host singleton; <see cref="Reset"/> runs between tests via the fixture.
/// </summary>
public sealed class FakeOcrService : IOcrService
{
    private static OcrResult Empty => new(string.Empty, 0, 0, 0m);

    public bool IsEnabled { get; set; }

    /// <summary>Number of times <see cref="OcrAsync"/> was called since the last reset.</summary>
    public int OcrCallCount { get; private set; }

    /// <summary>Result returned when <see cref="IsEnabled"/> is true.</summary>
    public OcrResult Result { get; set; } = Empty;

    public Task<OcrResult> OcrAsync(Stream content, string contentType, CancellationToken ct)
    {
        OcrCallCount++;
        return Task.FromResult(Result);
    }

    public void Reset()
    {
        OcrCallCount = 0;
        IsEnabled = false;
        Result = Empty;
    }
}
