using System.Security.Cryptography;
using CompliDrop.Api.Configuration;

namespace CompliDrop.Api.Services;

/// <summary>
/// Shared minting of vendor portal-link tokens + URLs (#320 review S1). Both the request-path
/// <c>VendorEndpoints.GeneratePortalLink</c> and the reminder worker (which mints a link to embed in a
/// vendor reminder email, FP-092) need the SAME 24-byte base64url token scheme, the SAME
/// <c>/portal/{token}</c> URL shape, and the SAME default upload quota — so a future change to any of
/// them can't silently diverge between the two call sites. NOT the auth <c>SecureToken</c> scheme:
/// those are 32-byte hashed-at-rest tokens; a portal token is a plaintext capability URL.
/// </summary>
public static class PortalLink
{
    /// <summary>Default per-link upload quota a freshly-minted link gets.</summary>
    public const int DefaultMaxUploads = 20;

    /// <summary>A 24-byte (192-bit) CSPRNG token, base64url-encoded (URL-safe, no padding).</summary>
    public static string GenerateToken()
    {
        Span<byte> bytes = stackalloc byte[24];
        RandomNumberGenerator.Fill(bytes);
        return Convert.ToBase64String(bytes).Replace('+', '-').Replace('/', '_').TrimEnd('=');
    }

    /// <summary>The public upload URL for a token: <c>{BaseUrl}/portal/{token}</c>.</summary>
    public static string Url(FrontendSettings cfg, string token) =>
        $"{cfg.BaseUrl.TrimEnd('/')}/portal/{token}";
}
