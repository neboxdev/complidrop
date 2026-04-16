using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using CompliDrop.Api.Configuration;
using Microsoft.Extensions.Options;

namespace CompliDrop.Api.Services.Extraction;

public class AnthropicExtractionClient(
    IHttpClientFactory httpFactory,
    IOptions<AnthropicSettings> settings,
    ILogger<AnthropicExtractionClient> logger) : IExtractionClient
{
    public ExtractionProvider Provider => ExtractionProvider.Anthropic;

    public async Task<ExtractionResult> ExtractAsync(
        OcrResult ocr,
        Stream? imageStream,
        string imageContentType,
        string? documentTypeHint,
        CancellationToken ct)
    {
        var cfg = settings.Value;
        if (string.IsNullOrWhiteSpace(cfg.ApiKey))
            throw new InvalidOperationException("Anthropic API key not configured.");

        var userContent = new JsonArray
        {
            new JsonObject
            {
                ["type"] = "text",
                ["text"] = BuildPrompt(ocr.Text, documentTypeHint)
            }
        };

        if (imageStream is not null && imageContentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
        {
            using var ms = new MemoryStream();
            await imageStream.CopyToAsync(ms, ct);
            userContent.Add(new JsonObject
            {
                ["type"] = "image",
                ["source"] = new JsonObject
                {
                    ["type"] = "base64",
                    ["media_type"] = imageContentType,
                    ["data"] = Convert.ToBase64String(ms.ToArray())
                }
            });
        }

        var body = new JsonObject
        {
            ["model"] = cfg.Model,
            ["max_tokens"] = cfg.MaxTokens,
            ["system"] = new JsonArray
            {
                new JsonObject
                {
                    ["type"] = "text",
                    ["text"] = ExtractionPrompts.SystemPrompt,
                    ["cache_control"] = new JsonObject { ["type"] = "ephemeral" }
                }
            },
            ["messages"] = new JsonArray
            {
                new JsonObject { ["role"] = "user", ["content"] = userContent }
            },
            ["tools"] = new JsonArray { BuildTool() },
            ["tool_choice"] = new JsonObject { ["type"] = "tool", ["name"] = "record_extraction" }
        };

        var client = httpFactory.CreateClient("anthropic");
        using var req = new HttpRequestMessage(HttpMethod.Post, "https://api.anthropic.com/v1/messages")
        {
            Content = JsonContent.Create(body)
        };
        req.Headers.Add("x-api-key", cfg.ApiKey);
        req.Headers.Add("anthropic-version", "2023-06-01");
        req.Headers.Add("anthropic-beta", "prompt-caching-2024-07-31");

        using var resp = await client.SendAsync(req, ct);
        var responseBody = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
        {
            logger.LogError("Anthropic request failed {StatusCode} {Body}", (int)resp.StatusCode, responseBody);
            throw new HttpRequestException($"Anthropic returned {(int)resp.StatusCode}.");
        }

        using var doc = JsonDocument.Parse(responseBody);
        var contentArr = doc.RootElement.GetProperty("content");
        JsonElement toolUse = default;
        foreach (var item in contentArr.EnumerateArray())
        {
            if (item.TryGetProperty("type", out var type) && type.GetString() == "tool_use")
            {
                toolUse = item;
                break;
            }
        }
        if (toolUse.ValueKind is JsonValueKind.Undefined)
            throw new InvalidOperationException("Anthropic response missing tool_use block.");

        var input = toolUse.GetProperty("input");
        var result = MapResult(input);

        int inputTokens = 0, outputTokens = 0;
        if (doc.RootElement.TryGetProperty("usage", out var usage))
        {
            inputTokens = usage.TryGetProperty("input_tokens", out var it) ? it.GetInt32() : 0;
            outputTokens = usage.TryGetProperty("output_tokens", out var ot) ? ot.GetInt32() : 0;
        }
        return result with { Usage = new ExtractionUsage(inputTokens, outputTokens, EstimateCost(inputTokens, outputTokens)) };
    }

    private static string BuildPrompt(string ocrText, string? documentTypeHint)
    {
        var hint = string.IsNullOrWhiteSpace(documentTypeHint) || documentTypeHint == "other"
            ? ""
            : $"Document type hint: {documentTypeHint}\n\n";
        var safeText = string.IsNullOrWhiteSpace(ocrText)
            ? "(No OCR text was extracted — inspect the attached image if available.)"
            : ocrText.Length > 20000 ? ocrText[..20000] : ocrText;
        return $"{hint}OCR text:\n---\n{safeText}\n---";
    }

    private static JsonObject BuildTool() => new()
    {
        ["name"] = "record_extraction",
        ["description"] = "Record the structured extraction result.",
        ["input_schema"] = new JsonObject
        {
            ["type"] = "object",
            ["required"] = new JsonArray { "documentType", "fields", "needsReprocessing" },
            ["properties"] = new JsonObject
            {
                ["documentType"] = new JsonObject { ["type"] = "string" },
                ["documentSubType"] = new JsonObject { ["type"] = "string" },
                ["needsReprocessing"] = new JsonObject { ["type"] = "boolean" },
                ["fields"] = new JsonObject
                {
                    ["type"] = "array",
                    ["items"] = new JsonObject
                    {
                        ["type"] = "object",
                        ["required"] = new JsonArray { "name", "value", "type", "confidence" },
                        ["properties"] = new JsonObject
                        {
                            ["name"] = new JsonObject { ["type"] = "string" },
                            ["value"] = new JsonObject { ["type"] = "string" },
                            ["type"] = new JsonObject { ["type"] = "string" },
                            ["confidence"] = new JsonObject { ["type"] = "number" }
                        }
                    }
                }
            }
        }
    };

    private static ExtractionResult MapResult(JsonElement root)
    {
        var documentType = root.TryGetProperty("documentType", out var dt) ? dt.GetString() ?? "other" : "other";
        var documentSubType = root.TryGetProperty("documentSubType", out var dst) ? dst.GetString() : null;
        var needsReprocessing = root.TryGetProperty("needsReprocessing", out var nr) && nr.GetBoolean();

        var fields = new List<ExtractedField>();
        if (root.TryGetProperty("fields", out var fieldsArr) && fieldsArr.ValueKind == JsonValueKind.Array)
        {
            foreach (var f in fieldsArr.EnumerateArray())
            {
                var name = f.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                var value = f.TryGetProperty("value", out var v) ? v.GetString() ?? "" : "";
                var type = f.TryGetProperty("type", out var t) ? t.GetString() ?? "text" : "text";
                var confidence = f.TryGetProperty("confidence", out var c) && c.TryGetDouble(out var cv) ? cv : 0.5;
                if (string.IsNullOrWhiteSpace(name)) continue;
                fields.Add(new ExtractedField(name, value, type, Math.Clamp(confidence, 0, 1)));
            }
        }

        return new ExtractionResult(documentType, documentSubType, fields, needsReprocessing, Usage: null);
    }

    private static decimal EstimateCost(int inputTokens, int outputTokens)
    {
        // Haiku 4.5: ~$1 per 1M input, ~$5 per 1M output (2026 estimate).
        return (decimal)inputTokens * 1m / 1_000_000m + (decimal)outputTokens * 5m / 1_000_000m;
    }
}
