using CompliDrop.Api.Endpoints;
using FluentAssertions;

namespace CompliDrop.Api.Tests;

/// <summary>
/// Unit tests for the "no signing secret configured" policy used by the Resend webhook
/// (<see cref="ReminderEndpoints.ResolveSecretPolicy"/>). Covers all three arms so an inverted
/// environment check — which would silently disable signature verification in production — fails
/// the build rather than slipping through (the integration harness only runs as Development).
/// </summary>
public sealed class ResendWebhookSecretPolicyTests
{
    [Theory]
    [InlineData("whsec_abc", true)]
    [InlineData("whsec_abc", false)]
    public void Configured_secret_always_verifies(string secret, bool isDevelopment) =>
        ReminderEndpoints.ResolveSecretPolicy(secret, isDevelopment)
            .Should().Be(ReminderEndpoints.SecretPolicy.Verify);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Unset_secret_in_Development_skips_verification(string? secret) =>
        ReminderEndpoints.ResolveSecretPolicy(secret, isDevelopment: true)
            .Should().Be(ReminderEndpoints.SecretPolicy.SkipInDevelopment);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Unset_secret_outside_Development_is_rejected(string? secret) =>
        ReminderEndpoints.ResolveSecretPolicy(secret, isDevelopment: false)
            .Should().Be(ReminderEndpoints.SecretPolicy.RejectUnconfigured);
}
