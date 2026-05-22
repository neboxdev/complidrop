using System.Text;
using System.Text.Json.Nodes;
using CompliDrop.Api.Services.Extraction;
using FluentAssertions;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace CompliDrop.Api.Tests.ExtractionFixtures;

/// <summary>
/// Loads the regression fixtures' <c>expected.yaml</c> files and synthesizes provider responses from
/// them, so the extraction-client tests can prove request shaping + response parsing without calling a
/// real model. We mock the HTTP boundary (model accuracy is a non-goal — ticket #9), so the canned
/// provider reply is built FROM each fixture's expected fields and the client must map it back to those
/// same fields. The <c>input.pdf</c> files belong to the future live-OCR suite and are not read here.
/// </summary>
public static class ExtractionFixtureHarness
{
    public static readonly string FixtureRoot = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "ExtractionFixtures"));

    /// <summary>Fixture directory names discovered on disk (each holds an <c>expected.yaml</c>).</summary>
    public static IReadOnlyList<string> FixtureNames() =>
        Directory.Exists(FixtureRoot)
            ? Directory.GetDirectories(FixtureRoot)
                .Where(d => File.Exists(Path.Combine(d, "expected.yaml")))
                .Select(Path.GetFileName)
                .Where(n => !string.IsNullOrEmpty(n))
                .Select(n => n!)
                .OrderBy(n => n, StringComparer.Ordinal)
                .ToList()
            : [];

    /// <summary>xUnit <c>[MemberData]</c> source: one row per fixture.</summary>
    public static IEnumerable<object[]> AllFixtures() => FixtureNames().Select(name => new object[] { name });

    public static ExpectedExtraction Load(string fixtureName)
    {
        var path = Path.Combine(FixtureRoot, fixtureName, "expected.yaml");
        var deserializer = new DeserializerBuilder()
            .WithNamingConvention(CamelCaseNamingConvention.Instance)
            .IgnoreUnmatchedProperties()
            .Build();
        var model = deserializer.Deserialize<ExpectedExtraction>(File.ReadAllText(path))
            ?? throw new InvalidOperationException($"Fixture '{fixtureName}' expected.yaml deserialized to null.");
        model.Name = fixtureName;
        return model;
    }

    /// <summary>A trivial-but-valid fixture for shaping/usage tests that don't need a real document.</summary>
    public static ExpectedExtraction Minimal() => new()
    {
        Name = "minimal",
        DocumentType = "coi",
        DocumentSubType = "general_liability",
        Fields = new() { ["policy_number"] = new ExpectedField { Expected = "GL-1", Tolerance = "exact" } },
    };

    /// <summary>
    /// A line-per-field OCR-like text so request-shaping assertions exercise realistic content. Stands
    /// in for what Document AI would return reading <c>input.pdf</c> (Document AI is out of scope here).
    /// </summary>
    public static string SyntheticOcrText(ExpectedExtraction expected)
    {
        var sb = new StringBuilder();
        foreach (var (name, field) in expected.Fields)
            sb.AppendLine($"{name}: {field.Expected}");
        return sb.ToString();
    }

    /// <summary>The structured payload both providers carry (Gemini as JSON text, Anthropic as tool input).</summary>
    public static JsonObject StructuredPayload(ExpectedExtraction expected, double confidence = 0.95)
    {
        var fields = new JsonArray();
        foreach (var (name, field) in expected.Fields)
        {
            fields.Add(new JsonObject
            {
                ["name"] = name,
                ["value"] = field.Expected,
                ["type"] = InferType(name, field.Expected),
                ["confidence"] = confidence,
            });
        }

        var payload = new JsonObject
        {
            ["documentType"] = expected.DocumentType,
            ["needsReprocessing"] = false,
            ["fields"] = fields,
        };
        if (expected.DocumentSubType is not null)
            payload["documentSubType"] = expected.DocumentSubType;
        return payload;
    }

    public static JsonObject GeminiResponse(ExpectedExtraction expected, int promptTokens = 1200, int candidatesTokens = 300)
        => GeminiResponseFromPayload(StructuredPayload(expected), promptTokens, candidatesTokens);

    /// <summary>Wraps an arbitrary structured payload in Gemini's <c>candidates[].content.parts[].text</c> envelope.</summary>
    public static JsonObject GeminiResponseFromPayload(JsonObject payload, int promptTokens = 1200, int candidatesTokens = 300) => new()
    {
        ["candidates"] = new JsonArray
        {
            new JsonObject
            {
                ["content"] = new JsonObject
                {
                    ["parts"] = new JsonArray { new JsonObject { ["text"] = payload.ToJsonString() } },
                },
            },
        },
        ["usageMetadata"] = new JsonObject
        {
            ["promptTokenCount"] = promptTokens,
            ["candidatesTokenCount"] = candidatesTokens,
        },
    };

    public static JsonObject AnthropicResponse(ExpectedExtraction expected, int inputTokens = 1200, int outputTokens = 300)
        => AnthropicResponseFromPayload(StructuredPayload(expected), inputTokens, outputTokens);

    /// <summary>Wraps an arbitrary structured payload in Anthropic's <c>tool_use</c> content block.</summary>
    public static JsonObject AnthropicResponseFromPayload(JsonObject payload, int inputTokens = 1200, int outputTokens = 300) => new()
    {
        ["content"] = new JsonArray
        {
            // A leading non-tool block proves the client scans for tool_use rather than taking content[0].
            new JsonObject { ["type"] = "text", ["text"] = "Here is the structured extraction." },
            new JsonObject
            {
                ["type"] = "tool_use",
                ["name"] = "record_extraction",
                ["input"] = payload.DeepClone(),
            },
        },
        ["usage"] = new JsonObject
        {
            ["input_tokens"] = inputTokens,
            ["output_tokens"] = outputTokens,
        },
    };

    /// <summary>
    /// Asserts the parsed result contains every expected field, honoring each field's tolerance:
    /// <c>exact</c> = ordinal equality after trim; <c>fuzzy</c> = case-insensitive containment either way.
    /// </summary>
    public static void AssertFieldsMatch(ExpectedExtraction expected, ExtractionResult actual)
    {
        foreach (var (name, field) in expected.Fields)
        {
            var match = actual.Fields.FirstOrDefault(f => f.Name == name);
            match.Should().NotBeNull($"expected field '{name}' should be present in the parsed result");
            FieldValueMatches(field, match!.Value).Should().BeTrue(
                $"field '{name}' value '{match.Value}' should satisfy expected '{field.Expected}' (tolerance: {field.Tolerance})");
        }
    }

    public static bool FieldValueMatches(ExpectedField expected, string actual)
    {
        var e = (expected.Expected ?? string.Empty).Trim();
        var a = (actual ?? string.Empty).Trim();
        return string.Equals(expected.Tolerance, "fuzzy", StringComparison.OrdinalIgnoreCase)
            ? a.Contains(e, StringComparison.OrdinalIgnoreCase) || e.Contains(a, StringComparison.OrdinalIgnoreCase)
            : string.Equals(e, a, StringComparison.Ordinal);
    }

    private static string InferType(string name, string value) =>
        name.Contains("date", StringComparison.OrdinalIgnoreCase) ? "date"
        : value.Length > 0 && value.All(char.IsDigit) ? "currency"
        : "text";
}

/// <summary>Typed view of an <c>expected.yaml</c> fixture (numeric scalars like currency limits load as strings).</summary>
public sealed class ExpectedExtraction
{
    [YamlIgnore] public string Name { get; set; } = string.Empty;
    public string DocumentType { get; set; } = string.Empty;
    public string? DocumentSubType { get; set; }
    public Dictionary<string, ExpectedField> Fields { get; set; } = new();
}

public sealed class ExpectedField
{
    public string Expected { get; set; } = string.Empty;
    public string Tolerance { get; set; } = "exact";
}
