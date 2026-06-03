using System.Security.Cryptography;
using System.Text;

namespace CompliDrop.Api.Auth;

/// <summary>
/// Generates and hashes single-use, link-delivered secret tokens (email
/// verification #184, password reset #183). The RAW token travels in the
/// emailed URL and is shown to the user exactly once; only its SHA-256 hash is
/// persisted, so a database read (backup, log, leaked dump) never exposes a
/// usable token. Lookups hash the inbound raw token and compare against the
/// stored hash — the same one-way pattern the refresh-cookie rate-limit key and
/// the Resend webhook secret already use elsewhere in the codebase.
/// </summary>
public static class SecureToken
{
    /// <summary>
    /// 32 cryptographically-random bytes → 43-char URL-safe base64 (no padding).
    /// 256 bits of entropy makes the token space unguessable; the SHA-256 hash is
    /// a fixed 64 hex chars regardless of raw length.
    /// </summary>
    public static (string Raw, string Hash) Generate()
    {
        Span<byte> bytes = stackalloc byte[32];
        RandomNumberGenerator.Fill(bytes);
        var raw = Convert.ToBase64String(bytes)
            .Replace('+', '-')
            .Replace('/', '_')
            .TrimEnd('=');
        return (raw, Hash(raw));
    }

    /// <summary>SHA-256 hex (uppercase, 64 chars) of the raw token. Deterministic, so the
    /// same raw token always maps to the same stored hash for lookup.</summary>
    public static string Hash(string raw) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(raw)));
}
