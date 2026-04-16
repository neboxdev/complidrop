using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using CompliDrop.Api.Configuration;
using CompliDrop.Api.Services.Extraction;
using Microsoft.Extensions.Options;

namespace CompliDrop.Api.Services.Ocr;

public class DocumentAiOcrService(
    IHttpClientFactory httpFactory,
    IGoogleAuthTokenProvider auth,
    IOptions<DocumentAiSettings> settings,
    ILogger<DocumentAiOcrService> logger) : IOcrService
{
    private readonly DocumentAiSettings _cfg = settings.Value;

    public bool IsEnabled =>
        _cfg.Enabled
        && !string.IsNullOrWhiteSpace(_cfg.ProjectId)
        && !string.IsNullOrWhiteSpace(_cfg.Location)
        && !string.IsNullOrWhiteSpace(_cfg.ProcessorId)
        && auth.IsConfigured;

    public async Task<OcrResult> OcrAsync(Stream content, string contentType, CancellationToken ct)
    {
        if (!IsEnabled)
        {
            return new OcrResult(Text: string.Empty, PageCount: 0, AvgConfidence: 0, EstimatedCostUsd: 0);
        }

        using var ms = new MemoryStream();
        await content.CopyToAsync(ms, ct);
        var bytes = ms.ToArray();

        var token = await auth.GetAccessTokenAsync(ct)
            ?? throw new InvalidOperationException("Document AI credentials not resolvable.");

        var url = $"https://{_cfg.Location}-documentai.googleapis.com/v1/projects/{_cfg.ProjectId}/locations/{_cfg.Location}/processors/{_cfg.ProcessorId}:process";

        var payload = new
        {
            rawDocument = new
            {
                content = Convert.ToBase64String(bytes),
                mimeType = contentType
            }
        };

        var client = httpFactory.CreateClient("google");
        using var req = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = JsonContent.Create(payload)
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        using var resp = await client.SendAsync(req, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
        {
            logger.LogError("Document AI request failed {StatusCode} {Body}", (int)resp.StatusCode, body);
            throw new HttpRequestException($"Document AI returned {(int)resp.StatusCode}.");
        }

        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        var documentEl = root.GetProperty("document");
        var text = documentEl.TryGetProperty("text", out var t) ? t.GetString() ?? string.Empty : string.Empty;
        var pageCount = documentEl.TryGetProperty("pages", out var pages) ? pages.GetArrayLength() : 1;

        double confidenceSum = 0;
        int confidenceCount = 0;
        if (documentEl.TryGetProperty("pages", out var pagesEl))
        {
            foreach (var page in pagesEl.EnumerateArray())
            {
                if (page.TryGetProperty("layout", out var layout)
                    && layout.TryGetProperty("confidence", out var confEl)
                    && confEl.TryGetDouble(out var conf))
                {
                    confidenceSum += conf;
                    confidenceCount++;
                }
            }
        }
        var avgConfidence = confidenceCount > 0 ? confidenceSum / confidenceCount : 0.9;

        // Document AI OCR: ~$1.50 per 1000 pages as of 2026
        var costUsd = pageCount * 0.0015m;

        return new OcrResult(text, pageCount, avgConfidence, costUsd);
    }
}
