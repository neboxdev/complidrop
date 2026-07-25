using System.Net;
using System.Reflection;
using System.Text.Json.Nodes;
using CompliDrop.Api.Endpoints;
using CompliDrop.Api.Services;
using CompliDrop.Api.Tests.ExtractionFixtures;
using CompliDrop.Api.Tests.TestHelpers;
using FluentAssertions;

namespace CompliDrop.Api.Tests;

/// <summary>
/// Pins the canonical document-type vocabulary (#373) and the three places that must all speak it:
/// <see cref="CanonicalDocumentTypes"/> itself, the two providers' structured-output schemas (asserted on
/// the WIRE payload, not the C# source), and the PATCH endpoint's own allow-list. No web host, no
/// network — the extraction clients run against a stub HTTP handler.
/// <para/>
/// The vocabulary is compliance-critical rather than cosmetic: <c>Document.DocumentType</c> is compared
/// with ordinal equality by <c>ComplianceCheckService</c>'s applicable-rules filter and by
/// <see cref="DocumentSupersession"/>'s <c>(VendorId, DocumentType)</c> grouping, so a value outside the
/// set grades against zero rules and supersedes nothing.
/// </summary>
public sealed class CanonicalDocumentTypeTests
{
    // ---- The vocabulary itself ------------------------------------------------------------------

    [Theory]
    [InlineData("coi", "coi")]                             // already canonical — passes through unmangled
    [InlineData("license", "license")]
    [InlineData("COI", "coi")]                             // the ticket's headline shape (an API client)
    [InlineData("Coi", "coi")]                             // mixed case
    [InlineData("CERTIFICATION", "certification")]
    [InlineData("  coi  ", "coi")]                         // surrounding whitespace
    [InlineData("\tContract\n", "contract")]
    [InlineData("Certificate of Insurance", "other")]      // the alternate provider's plausible prose
    [InlineData("banana", "other")]                        // unknown vocabulary
    [InlineData("co i", "other")]                          // near-miss: inner whitespace is NOT stripped
    [InlineData("", "other")]                              // blank
    [InlineData("   ", "other")]                           // whitespace-only
    [InlineData(null, "other")]                            // absent
    public void Normalize_coerces_any_value_into_the_vocabulary(string? input, string expected) =>
        CanonicalDocumentTypes.Normalize(input).Should().Be(expected);

    [Fact]
    public void Normalize_maps_an_over_length_value_to_the_fallback_rather_than_truncating()
    {
        // The varchar(100) column is what turned a long documentType into a 22001 (the ticket's public-route
        // 500). Normalize doesn't clamp — it refuses: a 5,000-character string names no type we know, so it
        // resolves to "other" like any other unknown word. Truncating would have invented a value.
        var runaway = new string('x', 5_000);

        CanonicalDocumentTypes.Normalize(runaway).Should().Be(CanonicalDocumentTypes.Fallback);
    }

    [Fact]
    public void Every_value_Normalize_can_return_is_a_short_member_of_the_vocabulary()
    {
        // The length-safety guarantee is STRUCTURAL, not a clamp: because Normalize only ever hands back an
        // element of All (HashSet.TryGetValue returns the STORED literal, never the caller's string), no
        // input can produce a value that overflows the column. Pinning the longest literal well under 100
        // keeps that true if the vocabulary grows.
        CanonicalDocumentTypes.All.Should().OnlyContain(t => t.Length < 100 && t.Length > 0);
        CanonicalDocumentTypes.All.Should().Contain(CanonicalDocumentTypes.Fallback,
            "the fallback must itself be a member of the vocabulary it coerces into");
        CanonicalDocumentTypes.All.Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public void Every_member_of_the_vocabulary_round_trips_unchanged()
    {
        // Data-driven over the production set so a type ADDED later is automatically covered: the whole set
        // must survive normalization untouched, or a legitimate document type would silently become "other".
        foreach (var type in CanonicalDocumentTypes.All)
        {
            CanonicalDocumentTypes.Normalize(type).Should().Be(type);
            CanonicalDocumentTypes.IsAllowed(type).Should().BeTrue();
            CanonicalDocumentTypes.Normalize(type.ToUpperInvariant()).Should().Be(type);
        }
    }

    [Theory]
    [InlineData("coi", true)]
    [InlineData("COI", true)]
    [InlineData("  license  ", true)]
    [InlineData("banana", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsAllowed_answers_the_membership_question_the_request_paths_ask(string? input, bool expected)
    {
        // IsAllowed and Normalize are deliberately DIFFERENT operations on the same vocabulary: a request
        // path rejects unrecognized input with a 400 (there's a human to correct it), a background worker
        // parsing a model response has to coerce. They must never disagree about membership, though.
        CanonicalDocumentTypes.IsAllowed(input).Should().Be(expected);
        if (!expected)
            CanonicalDocumentTypes.Normalize(input).Should().Be(CanonicalDocumentTypes.Fallback,
                "a value the request paths would reject is exactly what the worker coerces to the fallback");
    }

    // ---- The extraction-pipeline form: a blank answer must not demote a good stored type ----------

    [Theory]
    // A non-blank answer always wins — that's the self-heal the ticket's owner comment relies on.
    [InlineData("license", "coi", "license")]
    [InlineData("LICENSE", "coi", "license")]
    [InlineData("banana", "coi", "other")]         // an unknown answer IS a positive "we don't know"
    // A blank answer carries no information (documentType is `required` in both providers' schemas), so
    // it falls back to what we already believe rather than demoting it to "other".
    [InlineData(null, "license", "license")]
    [InlineData("", "license", "license")]
    [InlineData("   ", "license", "license")]
    // …but the fallback is itself normalized, so a non-canonical STORED value is laundered, never kept.
    [InlineData(null, "COI", "coi")]
    [InlineData(null, "banana", "other")]
    [InlineData(null, null, "other")]
    public void NormalizeExtracted_prefers_the_model_but_never_lets_a_blank_demote_the_stored_type(
        string? extracted, string? current, string expected) =>
        CanonicalDocumentTypes.NormalizeExtracted(extracted, current).Should().Be(expected);

    // ---- The provider schemas: one definition, two contracts -------------------------------------

    [Fact]
    public void SchemaEnum_hands_out_a_fresh_array_each_call()
    {
        // A JsonNode may only be attached to one parent, so a cached/shared array would throw the moment
        // the second client's schema tried to adopt it — the failure mode that would push a future author
        // back to two literal lists.
        var first = CanonicalDocumentTypes.SchemaEnum();
        var second = CanonicalDocumentTypes.SchemaEnum();

        first.Should().NotBeSameAs(second);
        var parentBoth = () => new JsonObject { ["a"] = first, ["b"] = second };
        parentBoth.Should().NotThrow("each schema must be able to adopt its own array");
        Values(first).Should().Equal(CanonicalDocumentTypes.All);
    }

    [Fact]
    public async Task Both_provider_schemas_pin_documentType_to_the_same_vocabulary()
    {
        // #373 scope item 2, asserted on the WIRE payloads rather than on the C# source: the Anthropic tool
        // schema left documentType a free string, so that provider was free to answer "COI" / "Certificate
        // of Insurance" against a prompt asking for "coi". Comparing the two arrays to EACH OTHER and to
        // All is what makes the sets impossible to drift apart — re-introducing a literal list on either
        // side reddens this even if that list happens to be correct on the day it's written.
        var gemini = await GeminiDocumentTypeEnumAsync();
        var anthropic = await AnthropicDocumentTypeEnumAsync();

        anthropic.Should().Equal(gemini, "the two providers must offer the model the same allowed set");
        gemini.Should().Equal(CanonicalDocumentTypes.All, "and that set is the shared vocabulary, in order");
    }

    private static async Task<string[]> GeminiDocumentTypeEnumAsync()
    {
        var handler = new StubHttpMessageHandler(HttpStatusCode.OK,
            ExtractionFixtureHarness.GeminiResponse(ExtractionFixtureHarness.Minimal()).ToJsonString());
        await ExtractionClientBuilder.Gemini(handler)
            .ExtractAsync(ExtractionClientBuilder.Ocr(), null, "application/pdf", null, default);

        return Values(JsonNode.Parse(handler.LastRequestBody)!
            ["generationConfig"]!["responseSchema"]!["properties"]!["documentType"]!["enum"]!.AsArray());
    }

    private static async Task<string[]> AnthropicDocumentTypeEnumAsync()
    {
        var handler = new StubHttpMessageHandler(HttpStatusCode.OK,
            ExtractionFixtureHarness.AnthropicResponse(ExtractionFixtureHarness.Minimal()).ToJsonString());
        await ExtractionClientBuilder.Anthropic(handler)
            .ExtractAsync(ExtractionClientBuilder.Ocr(), null, "application/pdf", null, default);

        return Values(JsonNode.Parse(handler.LastRequestBody)!
            ["tools"]![0]!["input_schema"]!["properties"]!["documentType"]!["enum"]!.AsArray());
    }

    private static string[] Values(JsonArray array) => [.. array.Select(n => n!.GetValue<string>())];

    // ---- The PATCH endpoint's allow-list -----------------------------------------------------------

    [Fact]
    public void The_document_PATCH_allow_list_speaks_the_same_vocabulary()
    {
        // DocumentEndpoints validates a manual type edit against its own private set. Those endpoint files
        // are owned by #389 (the upload-path allow-list + the oversize 22001) and are deliberately NOT
        // edited here, so the drift guarantee is mechanical instead: the two sets are pinned EQUAL.
        //
        // If #389 collapses that literal into CanonicalDocumentTypes — the desired end state — this test
        // goes red on a missing field, which is the correct prompt to delete it rather than a silent gap.
        var field = typeof(DocumentEndpoints).GetField("AllowedDocumentTypes",
            BindingFlags.NonPublic | BindingFlags.Static);

        field.Should().NotBeNull(
            "DocumentEndpoints must still declare AllowedDocumentTypes — if it now reuses " +
            "CanonicalDocumentTypes instead, delete this test, it has been superseded by the reuse");
        var endpointVocabulary = (IEnumerable<string>)field!.GetValue(null)!;

        endpointVocabulary.Should().BeEquivalentTo(CanonicalDocumentTypes.All,
            "a type the PATCH endpoint accepts but extraction normalizes away (or vice versa) is exactly " +
            "the drift #373 closes");
    }
}
