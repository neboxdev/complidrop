using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using CompliDrop.Api.Auth;
using CompliDrop.Api.Configuration;
using CompliDrop.Api.Entities;
using FluentAssertions;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace CompliDrop.Api.Tests;

/// <summary>Pure unit tests for <see cref="TokenService"/> issuance and validation.</summary>
public class TokenServiceTests
{
    private const string Secret = "token-service-unit-test-secret-key-0123456789"; // >= 32 chars

    private static readonly JwtSettings Cfg = new()
    {
        Secret = Secret,
        Issuer = "complidrop-api-test",
        Audience = "complidrop-frontend-test",
        SessionExpiryMinutes = 15,
        RefreshExpiryDays = 30
    };

    private static readonly TokenService Sut = new(Options.Create(Cfg));

    private static User NewUser() => new()
    {
        Id = Guid.NewGuid(),
        OrganizationId = Guid.NewGuid(),
        Email = "user@example.com",
        PasswordHash = "x",
        SecurityStamp = Guid.NewGuid()
    };

    [Fact]
    public void Session_token_round_trips_with_expected_claims()
    {
        var user = NewUser();

        var principal = Sut.ValidateToken(Sut.IssueSessionToken(user, "pro"), isRefresh: false);

        principal.Should().NotBeNull();
        principal!.FindFirstValue("typ").Should().Be("session");
        principal.FindFirstValue(ClaimTypes.NameIdentifier).Should().Be(user.Id.ToString());
        principal.FindFirstValue("org_id").Should().Be(user.OrganizationId.ToString());
        principal.FindFirstValue("plan").Should().Be("pro");
        // #202: the security stamp rides the session token so it can be re-checked
        // per request and rotated on credential change.
        principal.FindFirstValue("stamp").Should().Be(user.SecurityStamp.ToString());
    }

    [Fact]
    public void Refresh_token_round_trips()
    {
        var user = NewUser();
        var principal = Sut.ValidateToken(Sut.IssueRefreshToken(user), isRefresh: true);

        principal.Should().NotBeNull();
        principal!.FindFirstValue("typ").Should().Be("refresh");
        // #202: the refresh token carries the stamp too — Refresh() re-checks it.
        principal.FindFirstValue("stamp").Should().Be(user.SecurityStamp.ToString());
        principal.FindFirstValue(ClaimTypes.NameIdentifier).Should().Be(user.Id.ToString());
    }

    [Fact]
    public void Session_token_is_rejected_when_validated_as_refresh()
    {
        var token = Sut.IssueSessionToken(NewUser(), "free");

        Sut.ValidateToken(token, isRefresh: true).Should().BeNull();
    }

    [Fact]
    public void Refresh_token_is_rejected_when_validated_as_session()
    {
        var token = Sut.IssueRefreshToken(NewUser());

        Sut.ValidateToken(token, isRefresh: false).Should().BeNull();
    }

    [Fact]
    public void Tampered_token_is_rejected()
    {
        var token = Sut.IssueSessionToken(NewUser(), "free");
        var tampered = token[..^4] + (token.EndsWith("AAAA") ? "BBBB" : "AAAA");

        Sut.ValidateToken(tampered, isRefresh: false).Should().BeNull();
    }

    [Fact]
    public void Token_signed_with_a_different_secret_is_rejected()
    {
        var foreign = new TokenService(Options.Create(new JwtSettings
        {
            Secret = "a-totally-different-signing-secret-9876543210",
            Issuer = Cfg.Issuer,
            Audience = Cfg.Audience,
            SessionExpiryMinutes = 15,
            RefreshExpiryDays = 30
        }));

        Sut.ValidateToken(foreign.IssueSessionToken(NewUser(), "free"), isRefresh: false).Should().BeNull();
    }

    [Fact]
    public void Expired_token_is_rejected()
    {
        // A validly-signed but already-expired session token (expired ~2h ago, beyond clock skew).
        var creds = new SigningCredentials(
            new SymmetricSecurityKey(Encoding.UTF8.GetBytes(Secret)), SecurityAlgorithms.HmacSha256);
        var past = DateTime.UtcNow.AddMinutes(-120);
        var jwt = new JwtSecurityToken(
            issuer: Cfg.Issuer,
            audience: Cfg.Audience,
            claims: [new Claim("typ", "session")],
            notBefore: past,
            expires: past.AddMinutes(1),
            signingCredentials: creds);
        var token = new JwtSecurityTokenHandler().WriteToken(jwt);

        Sut.ValidateToken(token, isRefresh: false).Should().BeNull();
    }
}
