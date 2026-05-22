using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace CompliDrop.Api.Webhooks;

/// <summary>
/// Verifies inbound webhook signatures using the Svix scheme (the signing mechanism Resend uses).
/// Reference: https://docs.svix.com/receiving/verifying-payloads/how
///
/// The signed content is <c>"{svix-id}.{svix-timestamp}.{payload}"</c>, HMAC-SHA256'd with the
/// base64-decoded signing secret (the part after the <c>whsec_</c> prefix). The
/// <c>svix-signature</c> header carries a space-delimited list of <c>"{version},{base64-signature}"</c>
/// entries (more than one during secret rotation); a request is valid when the timestamp is within
/// the tolerance window AND any <c>v1</c> entry's signature matches.
/// </summary>
public static class SvixWebhookVerifier
{
    /// <summary>Svix's recommended replay-protection window (the timestamp must be within ± this of now).</summary>
    public static readonly TimeSpan DefaultTolerance = TimeSpan.FromMinutes(5);

    public enum Result
    {
        Valid,
        MissingHeaders,
        TimestampOutOfTolerance,
        InvalidSecret,
        NoMatchingSignature
    }

    /// <param name="payload">The raw request body, exactly as received (no re-serialization).</param>
    /// <param name="now">Current time, injected for testability (use <c>TimeProvider.GetUtcNow()</c>).</param>
    /// <param name="tolerance">Replay window; defaults to <see cref="DefaultTolerance"/> when null.</param>
    public static Result Verify(
        string payload,
        string? svixId,
        string? svixTimestamp,
        string? svixSignature,
        string secret,
        DateTimeOffset now,
        TimeSpan? tolerance = null)
    {
        if (string.IsNullOrEmpty(svixId) ||
            string.IsNullOrEmpty(svixTimestamp) ||
            string.IsNullOrEmpty(svixSignature))
            return Result.MissingHeaders;

        // Replay protection: the timestamp must parse and fall within ± the tolerance window.
        if (!long.TryParse(svixTimestamp, NumberStyles.Integer, CultureInfo.InvariantCulture, out var unixSeconds))
            return Result.TimestampOutOfTolerance;

        var window = tolerance ?? DefaultTolerance;
        var timestamp = DateTimeOffset.FromUnixTimeSeconds(unixSeconds);
        if (timestamp < now - window || timestamp > now + window)
            return Result.TimestampOutOfTolerance;

        if (!TryDecodeSecret(secret, out var key))
            return Result.InvalidSecret;

        var signedContent = $"{svixId}.{svixTimestamp}.{payload}";
        byte[] expected;
        using (var hmac = new HMACSHA256(key))
            expected = hmac.ComputeHash(Encoding.UTF8.GetBytes(signedContent));

        foreach (var entry in svixSignature.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            // Each entry is "{version},{base64sig}". Only v1 (HMAC-SHA256) is supported.
            if (!entry.StartsWith("v1,", StringComparison.Ordinal)) continue;

            var candidate = entry["v1,".Length..];
            if (!TryFromBase64(candidate, out var provided)) continue;

            // FixedTimeEquals is constant-time and safely returns false on a length mismatch.
            if (CryptographicOperations.FixedTimeEquals(provided, expected))
                return Result.Valid;
        }

        return Result.NoMatchingSignature;
    }

    private static bool TryDecodeSecret(string secret, out byte[] key)
    {
        key = [];
        if (string.IsNullOrEmpty(secret)) return false;
        const string prefix = "whsec_";
        var raw = secret.StartsWith(prefix, StringComparison.Ordinal) ? secret[prefix.Length..] : secret;
        return TryFromBase64(raw, out key);
    }

    private static bool TryFromBase64(string value, out byte[] bytes)
    {
        bytes = [];
        if (string.IsNullOrEmpty(value)) return false;
        var buffer = new byte[(value.Length / 4 + 1) * 3];
        if (!Convert.TryFromBase64String(value, buffer, out var written)) return false;
        bytes = buffer[..written];
        return true;
    }
}
