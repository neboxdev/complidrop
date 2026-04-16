using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using CompliDrop.Api.Configuration;
using CompliDrop.Api.Entities;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace CompliDrop.Api.Auth;

public interface ITokenService
{
    string IssueSessionToken(User user, string plan);
    string IssueRefreshToken(User user);
    ClaimsPrincipal? ValidateToken(string token, bool isRefresh);
}

public class TokenService(IOptions<JwtSettings> settings) : ITokenService
{
    private readonly JwtSettings _cfg = settings.Value;
    private readonly JwtSecurityTokenHandler _handler = new();

    public string IssueSessionToken(User user, string plan)
    {
        var claims = new Claim[]
        {
            new(JwtRegisteredClaimNames.Sub, user.Id.ToString()),
            new(ClaimTypes.NameIdentifier, user.Id.ToString()),
            new(ClaimTypes.Email, user.Email),
            new("org_id", user.OrganizationId.ToString()),
            new("plan", plan),
            new("typ", "session")
        };
        return WriteToken(claims, TimeSpan.FromMinutes(_cfg.SessionExpiryMinutes));
    }

    public string IssueRefreshToken(User user)
    {
        var claims = new Claim[]
        {
            new(JwtRegisteredClaimNames.Sub, user.Id.ToString()),
            new("org_id", user.OrganizationId.ToString()),
            new("typ", "refresh")
        };
        return WriteToken(claims, TimeSpan.FromDays(_cfg.RefreshExpiryDays));
    }

    public ClaimsPrincipal? ValidateToken(string token, bool isRefresh)
    {
        try
        {
            var principal = _handler.ValidateToken(token, BuildValidationParameters(), out _);
            var typ = principal.FindFirstValue("typ");
            if (isRefresh && typ != "refresh") return null;
            if (!isRefresh && typ != "session") return null;
            return principal;
        }
        catch
        {
            return null;
        }
    }

    private string WriteToken(Claim[] claims, TimeSpan lifetime)
    {
        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_cfg.Secret));
        var creds = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);
        var now = DateTime.UtcNow;
        var token = new JwtSecurityToken(
            issuer: _cfg.Issuer,
            audience: _cfg.Audience,
            claims: claims,
            notBefore: now,
            expires: now.Add(lifetime),
            signingCredentials: creds);
        return _handler.WriteToken(token);
    }

    internal TokenValidationParameters BuildValidationParameters() => new()
    {
        ValidateIssuer = true,
        ValidateAudience = true,
        ValidateLifetime = true,
        ValidateIssuerSigningKey = true,
        ValidIssuer = _cfg.Issuer,
        ValidAudience = _cfg.Audience,
        IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_cfg.Secret)),
        ClockSkew = TimeSpan.FromSeconds(30)
    };
}
