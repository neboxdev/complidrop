using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using CompliDrop.Api.Configuration;
using Microsoft.Extensions.Options;

namespace CompliDrop.Api.Services.Extraction;

public class GeminiExtractionClient(
    IHttpClientFactory httpFactory,
    IGoogleAuthTokenProvider auth,
    IOptions<GeminiSettings> geminiSettings,
    IOptions<DocumentAiSettings> docAiSettings,
    ILogger<GeminiExtractionClient> logger) : IExtractionClient
{
    public ExtractionProvider Provider => ExtractionProvider.Gemini;

    public async Task<ExtractionResult> ExtractAsync(
        OcrResult ocr,
        Stream? imageStream,
        string imageContentType,
        string? documentTypeHint,
        CancellationToken ct)
    {
        var cfg = geminiSettings.Value;
        var useVertex = cfg.Endpoint.Equals("vertex", StringComparison.OrdinalIgnoreCase);
        var projectId = docAiSettings.Value.ProjectId;

        string url;
        string? authorizationToken = null;
        if (useVertex)
        {
            if (string.IsNullOrWhiteSpace(projectId) || !auth.IsConfigured)
                throw new InvalidOperationException("Vertex AI not configured — set Google Cloud project + service account credentials.");
            authorizationToken = await auth.GetAccessTokenAsync(ct)
                ?? throw new InvalidOperationException("Google auth token not resolvable.");
            url = $"https://{cfg.Location}-aiplatform.googleapis.com/v1/projects/{projectId}/locations/{cfg.Location}/publishers/google/models/{cfg.Model}:generateContent";
        }
        else
        {
            if (string.IsNullOrWhiteSpace(cfg.ApiKey))
                throw new InvalidOperationException("Gemini API key not configured.");
            url = $"https://generativelanguage.googleapis.com/v1beta/models/{cfg.Model}:generateContent?key={cfg.ApiKey}";
        }

        var userParts = new JsonArray
        {
            new JsonObject
            {
                ["text"] = BuildPrompt(ocr.Text, documentTypeHint)
            }
        };

        if (imageStream is not null)
        {
            using var ms = new MemoryStream();
            await imageStream.CopyToAsync(ms, ct);
            var b64 = Convert.ToBase64String(ms.ToArray());
            userParts.Add(new JsonObject
            {
                ["inlineData"] = new JsonObject
                {
                    ["mimeType"] = imageContentType,
                    ["data"] = b64
                }
            });
        }

        var body = new JsonObject
        {
            ["systemInstruction"] = new JsonObject
            {
                ["parts"] = new JsonArray
                {
                    new JsonObject { ["text"] = ExtractionPrompts.SystemPrompt }
                }
            },
            ["contents"] = new JsonArray
            {
                new JsonObject { ["role"] = "user", ["parts"] = userParts }
            },
            ["generationConfig"] = new JsonObject
            {
                ["temperature"] = 0.0,
                ["maxOutputTokens"] = cfg.MaxTokens,
                ["responseMimeType"] = "application/json",
                ["responseSchema"] = BuildResponseSchema()
            }
        };

        var client = httpFactory.CreateClient("google");
        using var req = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = JsonContent.Create(body)
        };
        if (authorizationToken is not null)
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", authorizationToken);

        using var resp = await client.SendAsync(req, ct);
        var responseBody = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
        {
            logger.LogError("Vertex Gemini request failed {StatusCode} {Body}", (int)resp.StatusCode, responseBody);
            throw new HttpRequestException($"Vertex AI Gemini returned {(int)resp.StatusCode}.");
        }

        using var doc = JsonDocument.Parse(responseBody);
        var candidates = doc.RootElement.GetProperty("candidates");
        if (candidates.ValueKind != JsonValueKind.Array || candidates.GetArrayLength() == 0)
            throw new InvalidOperationException("Gemini response contained no candidates.");
        var firstCandidate = candidates[0];

        // A truncated response (finishReason=MAX_TOKENS) carries only PARTIAL JSON — JsonDocument.Parse
        // below would throw, and the worker would retry the byte-identical request until the cap, never
        // succeeding (#259, problem 1). Detect it here and fail fast as a deterministic failure so the
        // worker stops immediately instead of burning OCR + LLM cost on doomed retries.
        var finishReason = firstCandidate.TryGetProperty("finishReason", out var fr) ? fr.GetString() : null;
        if (string.Equals(finishReason, "MAX_TOKENS", StringComparison.OrdinalIgnoreCase))
            throw new NonRetryableExtractionException("extraction.token_limit",
                $"Gemini truncated the response at the {cfg.MaxTokens}-token output cap; the extracted JSON is incomplete. Raise Gemini:MaxTokens.");

        if (!firstCandidate.TryGetProperty("content", out var content)
            || !content.TryGetProperty("parts", out var parts)
            || parts.ValueKind != JsonValueKind.Array || parts.GetArrayLength() == 0)
        {
            // No text content. A non-STOP finishReason (SAFETY / RECITATION / BLOCKLIST / …) is a
            // content block — deterministic, so don't retry. An absent/STOP reason with no parts is an
            // odd-but-possibly-transient shape, so let it stay an ordinary (retryable) failure.
            if (!string.IsNullOrEmpty(finishReason) && !string.Equals(finishReason, "STOP", StringComparison.OrdinalIgnoreCase))
                throw new NonRetryableExtractionException("extraction.blocked",
                    $"Gemini returned no usable content (finishReason: {finishReason}).");
            throw new InvalidOperationException("Gemini response missing content parts.");
        }

        var jsonText = parts[0].GetProperty("text").GetString()
            ?? throw new InvalidOperationException("Gemini response missing text content.");

        var parsed = JsonDocument.Parse(jsonText);
        var result = MapResult(parsed.RootElement);

        var usage = doc.RootElement.TryGetProperty("usageMetadata", out var um)
            ? new ExtractionUsage(
                InputTokens: um.TryGetProperty("promptTokenCount", out var it) ? it.GetInt32() : 0,
                OutputTokens: um.TryGetProperty("candidatesTokenCount", out var ot) ? ot.GetInt32() : 0,
                EstimatedCostUsd: EstimateCost(
                    um.TryGetProperty("promptTokenCount", out var it2) ? it2.GetInt32() : 0,
                    um.TryGetProperty("candidatesTokenCount", out var ot2) ? ot2.GetInt32() : 0))
            : null;

        return result with { Usage = usage };
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

    private static JsonObject BuildResponseSchema() => new()
    {
        ["type"] = "object",
        ["required"] = new JsonArray { "documentType", "fields", "needsReprocessing" },
        ["properties"] = new JsonObject
        {
            ["documentType"] = new JsonObject
            {
                ["type"] = "string",
                ["enum"] = new JsonArray { "coi", "license", "permit", "certification", "contract", "other" }
            },
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
                        ["type"] = new JsonObject
                        {
                            ["type"] = "string",
                            ["enum"] = new JsonArray { "text", "date", "currency", "number" }
                        },
                        ["confidence"] = new JsonObject
                        {
                            ["type"] = "number"
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
        // Gemini 2.5 Flash via Vertex: roughly $0.075 / 1M input, $0.30 / 1M output (2026 estimate).
        return (decimal)inputTokens * 0.075m / 1_000_000m + (decimal)outputTokens * 0.30m / 1_000_000m;
    }
}
