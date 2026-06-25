using FluentAssertions;
using Microsoft.Extensions.Configuration;

namespace CompliDrop.Api.Tests;

/// <summary>
/// Pure unit tests for <see cref="DatabaseMigrator.ShouldAutoMigrate"/> — the config decision that
/// gates whether a booting host applies migrations itself. No DB / host boot needed; the
/// schema-touching behaviour of <see cref="DatabaseMigrator.MigrateAndGuardAsync"/> is covered by
/// <see cref="DatabaseMigratorIntegrationTests"/>.
/// </summary>
public sealed class DatabaseMigratorTests
{
    [Fact]
    public void Defaults_to_true_when_key_absent()
    {
        // The whole point of #226: a missing config must NOT leave migrations unapplied. The safe
        // state (apply on boot) is the default.
        DatabaseMigrator.ShouldAutoMigrate(Config())
            .Should().BeTrue("an absent Database:AutoMigrate must default to applying migrations");
    }

    [Fact]
    public void Honors_explicit_true()
    {
        DatabaseMigrator.ShouldAutoMigrate(Config(("Database:AutoMigrate", "true")))
            .Should().BeTrue();
    }

    [Fact]
    public void Honors_explicit_false()
    {
        // An explicit false is honored in EVERY environment — the release-command deploy shape
        // (option 2) legitimately disables auto-migrate. The drift guard in MigrateAndGuardAsync is
        // the safety net, not a force-on here.
        DatabaseMigrator.ShouldAutoMigrate(Config(("Database:AutoMigrate", "false")))
            .Should().BeFalse();
    }

    [Theory]
    [InlineData("True")]
    [InlineData("TRUE")]
    [InlineData("False")]
    [InlineData("FALSE")]
    public void Parses_case_insensitively(string value)
    {
        var expected = value.Equals("true", StringComparison.OrdinalIgnoreCase);
        DatabaseMigrator.ShouldAutoMigrate(Config(("Database:AutoMigrate", value)))
            .Should().Be(expected);
    }

    [Theory]
    [InlineData("yes")]
    [InlineData("1")]
    [InlineData("on")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-a-bool")]
    public void Unparseable_string_fails_safe_to_enabled(string value)
    {
        // Don't crash startup on a typoed bool, and don't silently disable migrations — treat
        // unparseable as the safe default (apply on boot). Mirrors RateLimitingGate's fail-safe.
        DatabaseMigrator.ShouldAutoMigrate(Config(("Database:AutoMigrate", value)))
            .Should().BeTrue();
    }

    private static IConfiguration Config(params (string key, string value)[] entries) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(entries.Select(e => new KeyValuePair<string, string?>(e.key, e.value)))
            .Build();
}
