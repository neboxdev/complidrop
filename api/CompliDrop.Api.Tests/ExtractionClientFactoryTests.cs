using CompliDrop.Api.Configuration;
using CompliDrop.Api.Services.Extraction;
using CompliDrop.Api.Tests.TestHelpers;
using FluentAssertions;
using Microsoft.Extensions.Options;

namespace CompliDrop.Api.Tests;

/// <summary>
/// Verifies <see cref="ExtractionClientFactory"/> selects the client matching <c>Extraction:Provider</c>
/// (case- and whitespace-insensitive), defaults to Gemini for unknown/blank/null values, and falls back
/// to the first registered client when the preferred provider isn't registered. Uses the real clients so
/// the test also pins each client's <see cref="IExtractionClient.Provider"/> value.
/// </summary>
public sealed class ExtractionClientFactoryTests
{
    private static IExtractionClient Gemini() => ExtractionClientBuilder.Gemini(new StubHttpMessageHandler());
    private static IExtractionClient Anthropic() => ExtractionClientBuilder.Anthropic(new StubHttpMessageHandler());

    private static ExtractionClientFactory Factory(string? provider, params IExtractionClient[] clients) =>
        new(Options.Create(new ExtractionSettings { Provider = provider! }), clients);

    [Fact]
    public void Selects_gemini_for_gemini_provider()
    {
        IExtractionClient gemini = Gemini(), anthropic = Anthropic();
        Factory("gemini", gemini, anthropic).Get().Should().BeSameAs(gemini);
    }

    [Fact]
    public void Selects_anthropic_for_anthropic_provider()
    {
        IExtractionClient gemini = Gemini(), anthropic = Anthropic();
        Factory("anthropic", gemini, anthropic).Get().Should().BeSameAs(anthropic);
    }

    [Theory]
    [InlineData("GEMINI")]
    [InlineData(" gemini ")]
    [InlineData("openai")]  // unknown provider → default
    [InlineData("")]
    [InlineData(null)]
    public void Defaults_to_gemini_for_unknown_blank_or_null(string? provider)
    {
        IExtractionClient gemini = Gemini(), anthropic = Anthropic();
        Factory(provider, gemini, anthropic).Get().Should().BeSameAs(gemini);
    }

    [Theory]
    [InlineData("ANTHROPIC")]
    [InlineData("  anthropic")]
    public void Anthropic_selection_is_case_and_whitespace_insensitive(string provider)
    {
        IExtractionClient gemini = Gemini(), anthropic = Anthropic();
        Factory(provider, gemini, anthropic).Get().Should().BeSameAs(anthropic);
    }

    [Fact]
    public void Falls_back_to_first_when_preferred_provider_not_registered()
    {
        var gemini = Gemini();
        // Provider=anthropic, but only Gemini is registered → clients.First().
        Factory("anthropic", gemini).Get().Should().BeSameAs(gemini);
    }

    [Fact]
    public void Provider_property_matches_the_concrete_client()
    {
        Gemini().Provider.Should().Be(ExtractionProvider.Gemini);
        Anthropic().Provider.Should().Be(ExtractionProvider.Anthropic);
    }
}
