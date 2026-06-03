using CompliDrop.Api.Auth;
using FluentAssertions;

namespace CompliDrop.Api.Tests;

/// <summary>Pure-logic unit tests for <see cref="SecureToken"/> (#184/#183) — no DB, no Docker.</summary>
public sealed class SecureTokenTests
{
    [Fact]
    public void Generate_returns_a_url_safe_raw_token_and_its_hash()
    {
        var (raw, hash) = SecureToken.Generate();

        raw.Should().NotBeNullOrEmpty();
        // URL-safe base64 (no +, /, or = padding) so it survives an email link unescaped.
        raw.Should().MatchRegex("^[A-Za-z0-9_-]+$");
        // SHA-256 hex is a fixed 64 uppercase chars.
        hash.Should().HaveLength(64).And.MatchRegex("^[0-9A-F]+$");
        hash.Should().Be(SecureToken.Hash(raw));
    }

    [Fact]
    public void Generate_produces_distinct_unguessable_tokens()
    {
        var tokens = Enumerable.Range(0, 100).Select(_ => SecureToken.Generate().Raw).ToList();
        tokens.Distinct().Should().HaveCount(100, "256 bits of entropy must not collide across 100 draws");
    }

    [Fact]
    public void Hash_is_deterministic_and_collision_distinct()
    {
        SecureToken.Hash("abc").Should().Be(SecureToken.Hash("abc"));
        SecureToken.Hash("abc").Should().NotBe(SecureToken.Hash("abd"));
    }
}
