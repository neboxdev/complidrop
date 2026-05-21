using CompliDrop.Api.Auth;
using FluentAssertions;

namespace CompliDrop.Api.Tests;

/// <summary>Pure unit tests for <see cref="BCryptPasswordHasher"/>.</summary>
public class PasswordHasherTests
{
    private readonly BCryptPasswordHasher _hasher = new();

    [Fact]
    public void Hash_then_verify_succeeds()
    {
        var hash = _hasher.Hash("Correct-Horse-Battery-1");

        _hasher.Verify("Correct-Horse-Battery-1", hash).Should().BeTrue();
    }

    [Fact]
    public void Verify_fails_for_wrong_password()
    {
        var hash = _hasher.Hash("Correct-Horse-Battery-1");

        _hasher.Verify("wrong-password-1", hash).Should().BeFalse();
    }

    [Fact]
    public void Verify_returns_false_for_a_malformed_hash()
    {
        // Verify must swallow the BCrypt parse exception and return false, not throw.
        _hasher.Verify("anything", "not-a-bcrypt-hash").Should().BeFalse();
    }

    [Fact]
    public void Hash_uses_work_factor_12()
    {
        // BCrypt hash format is $2<x>$<cost>$<salt+digest>; cost 12 is the project standard.
        _hasher.Hash("pw").Should().Contain("$12$");
    }

    [Fact]
    public void Hashing_same_password_twice_yields_different_hashes()
    {
        // Distinct random salts ⇒ different hashes for the same input.
        _hasher.Hash("same-password-xyz").Should().NotBe(_hasher.Hash("same-password-xyz"));
    }
}
