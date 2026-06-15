using CompliDrop.Api.BackgroundServices;
using CompliDrop.Api.Configuration;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace CompliDrop.Api.Tests;

/// <summary>
/// Pins the extraction-robustness config defaults and the worker's per-attempt-timeout clamp (#259).
/// The whole of problem 1 was that the token-cap default was too low; problem 3's per-attempt timeout
/// MUST stay below the 5-minute zombie-reclaim threshold or two workers could process one document.
/// These are pure unit tests — no web host, no DB.
/// </summary>
public sealed class ExtractionSettingsDefaultsTests
{
    [Fact]
    public void Gemini_max_tokens_default_is_high_enough_for_a_real_document()
    {
        // 2000 truncated a normal one-page COI (#259, problem 1); the default must be well above it.
        new GeminiSettings().MaxTokens.Should().Be(8192);
    }

    [Fact]
    public void Anthropic_max_tokens_default_is_high_enough_for_a_real_document()
    {
        new AnthropicSettings().MaxTokens.Should().Be(8192);
    }

    [Fact]
    public void Attempt_timeout_default_is_three_minutes()
    {
        new ExtractionSettings().AttemptTimeoutSeconds.Should().Be(180);
    }

    [Theory]
    [InlineData(5, 60)]      // below the floor → clamped up
    [InlineData(180, 180)]   // in range → unchanged
    [InlineData(600, 240)]   // above the ceiling → clamped down (keeps it under the zombie threshold)
    public void Worker_clamps_attempt_timeout_into_a_safe_range(int configuredSeconds, int expectedSeconds)
    {
        var worker = BuildWorker(configuredSeconds);

        worker.AttemptTimeout.Should().Be(TimeSpan.FromSeconds(expectedSeconds));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(180)]
    [InlineData(100_000)]
    public void Attempt_timeout_always_stays_below_the_five_minute_zombie_threshold(int configuredSeconds)
    {
        // Load-bearing invariant: the attempt must cancel + requeue before another worker could
        // reclaim the same Processing row at the 5-minute zombie threshold (ExtractionWorker.ClaimSql).
        var worker = BuildWorker(configuredSeconds);

        worker.AttemptTimeout.Should().BeLessThan(TimeSpan.FromMinutes(5));
    }

    private static ExtractionWorker BuildWorker(int attemptTimeoutSeconds)
    {
        // A real (empty) provider supplies IServiceScopeFactory; the clamp runs at construction and
        // needs no database.
        using var provider = new ServiceCollection().BuildServiceProvider();
        return new ExtractionWorker(
            provider.GetRequiredService<IServiceScopeFactory>(),
            Options.Create(new ExtractionSettings { AttemptTimeoutSeconds = attemptTimeoutSeconds }),
            NullLogger<ExtractionWorker>.Instance);
    }
}
