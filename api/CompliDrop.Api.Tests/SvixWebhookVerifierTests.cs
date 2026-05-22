using System.Security.Cryptography;
using System.Text;
using CompliDrop.Api.Webhooks;
using FluentAssertions;

namespace CompliDrop.Api.Tests;

/// <summary>
/// Pure unit tests for the Svix signature verifier used by the Resend inbound webhook:
/// the happy path, every rejection branch, the replay-tolerance window, secret decoding,
/// and multi-signature / version handling.
/// </summary>
public sealed class SvixWebhookVerifierTests
{
    private static readonly byte[] KeyBytes =
        Encoding.UTF8.GetBytes("svix-verifier-unit-test-secret-key-0123456789");

    private static readonly string Secret = "whsec_" + Convert.ToBase64String(KeyBytes);

    // A fixed "now" so timestamp math is deterministic.
    private static readonly DateTimeOffset Now = DateTimeOffset.FromUnixTimeSeconds(1_700_000_000);

    private const string Id = "msg_2KWPBgLlAfxdpx2AI54pPJ85f";
    private const string Payload = """{"type":"email.delivered","data":{"email_id":"abc-123"}}""";

    private static string Ts(DateTimeOffset when) => when.ToUnixTimeSeconds().ToString();

    /// <summary>Produces a "v1,&lt;base64&gt;" signature, optionally with a different key.</summary>
    private static string Sign(string id, string timestamp, string payload, byte[]? key = null)
    {
        using var hmac = new HMACSHA256(key ?? KeyBytes);
        var sig = Convert.ToBase64String(hmac.ComputeHash(Encoding.UTF8.GetBytes($"{id}.{timestamp}.{payload}")));
        return $"v1,{sig}";
    }

    [Fact]
    public void Valid_signature_within_tolerance_passes()
    {
        var ts = Ts(Now);
        SvixWebhookVerifier.Verify(Payload, Id, ts, Sign(Id, ts, Payload), Secret, Now)
            .Should().Be(SvixWebhookVerifier.Result.Valid);
    }

    [Fact]
    public void Secret_without_whsec_prefix_is_treated_as_raw_base64()
    {
        var ts = Ts(Now);
        var rawSecret = Convert.ToBase64String(KeyBytes); // no "whsec_" prefix
        SvixWebhookVerifier.Verify(Payload, Id, ts, Sign(Id, ts, Payload), rawSecret, Now)
            .Should().Be(SvixWebhookVerifier.Result.Valid);
    }

    [Theory]
    [InlineData(null, "1700000000", "v1,sig")]
    [InlineData("msg_1", null, "v1,sig")]
    [InlineData("msg_1", "1700000000", null)]
    [InlineData("", "1700000000", "v1,sig")]
    [InlineData("msg_1", "", "v1,sig")]
    [InlineData("msg_1", "1700000000", "")]
    public void Missing_or_empty_header_returns_MissingHeaders(string? id, string? ts, string? sig)
    {
        SvixWebhookVerifier.Verify(Payload, id, ts, sig, Secret, Now)
            .Should().Be(SvixWebhookVerifier.Result.MissingHeaders);
    }

    [Fact]
    public void Tampered_payload_returns_NoMatchingSignature()
    {
        var ts = Ts(Now);
        var sig = Sign(Id, ts, Payload);
        SvixWebhookVerifier.Verify(Payload + "tampered", Id, ts, sig, Secret, Now)
            .Should().Be(SvixWebhookVerifier.Result.NoMatchingSignature);
    }

    [Fact]
    public void Signature_made_with_a_different_key_returns_NoMatchingSignature()
    {
        var ts = Ts(Now);
        var otherKey = Encoding.UTF8.GetBytes("a-totally-different-signing-key-value-here");
        SvixWebhookVerifier.Verify(Payload, Id, ts, Sign(Id, ts, Payload, otherKey), Secret, Now)
            .Should().Be(SvixWebhookVerifier.Result.NoMatchingSignature);
    }

    [Fact]
    public void Signature_bound_to_a_different_svix_id_does_not_match()
    {
        // Sender signed over "other_id" but the request presents "Id" — proves the id is part of
        // the signed content (defends against header/payload swapping).
        var ts = Ts(Now);
        var sig = Sign("msg_other_id", ts, Payload);
        SvixWebhookVerifier.Verify(Payload, Id, ts, sig, Secret, Now)
            .Should().Be(SvixWebhookVerifier.Result.NoMatchingSignature);
    }

    [Fact]
    public void Stale_timestamp_beyond_tolerance_is_rejected()
    {
        var when = Now.AddMinutes(-6); // default tolerance is 5 minutes
        var ts = Ts(when);
        SvixWebhookVerifier.Verify(Payload, Id, ts, Sign(Id, ts, Payload), Secret, Now)
            .Should().Be(SvixWebhookVerifier.Result.TimestampOutOfTolerance);
    }

    [Fact]
    public void Future_timestamp_beyond_tolerance_is_rejected()
    {
        var when = Now.AddMinutes(6);
        var ts = Ts(when);
        SvixWebhookVerifier.Verify(Payload, Id, ts, Sign(Id, ts, Payload), Secret, Now)
            .Should().Be(SvixWebhookVerifier.Result.TimestampOutOfTolerance);
    }

    [Fact]
    public void Timestamp_exactly_at_tolerance_boundary_passes()
    {
        var when = Now.AddMinutes(-5); // inclusive boundary
        var ts = Ts(when);
        SvixWebhookVerifier.Verify(Payload, Id, ts, Sign(Id, ts, Payload), Secret, Now)
            .Should().Be(SvixWebhookVerifier.Result.Valid);
    }

    [Fact]
    public void Non_numeric_timestamp_is_rejected()
    {
        SvixWebhookVerifier.Verify(Payload, Id, "not-a-number", Sign(Id, "not-a-number", Payload), Secret, Now)
            .Should().Be(SvixWebhookVerifier.Result.TimestampOutOfTolerance);
    }

    [Fact]
    public void Custom_tolerance_is_honored()
    {
        var when = Now.AddSeconds(-90);
        var ts = Ts(when);
        var sig = Sign(Id, ts, Payload);

        SvixWebhookVerifier.Verify(Payload, Id, ts, sig, Secret, Now, TimeSpan.FromMinutes(2))
            .Should().Be(SvixWebhookVerifier.Result.Valid);
        SvixWebhookVerifier.Verify(Payload, Id, ts, sig, Secret, Now, TimeSpan.FromSeconds(30))
            .Should().Be(SvixWebhookVerifier.Result.TimestampOutOfTolerance);
    }

    [Fact]
    public void Multiple_signatures_with_one_valid_entry_passes()
    {
        var ts = Ts(Now);
        var wrong = Sign(Id, ts, "a payload that was never sent"); // valid v1 format, wrong content
        var right = Sign(Id, ts, Payload);
        SvixWebhookVerifier.Verify(Payload, Id, ts, $"{wrong} {right}", Secret, Now)
            .Should().Be(SvixWebhookVerifier.Result.Valid);
    }

    [Fact]
    public void Non_v1_scheme_entries_are_ignored()
    {
        var ts = Ts(Now);
        var v2 = Sign(Id, ts, Payload).Replace("v1,", "v2,"); // correct bytes, unsupported version label
        SvixWebhookVerifier.Verify(Payload, Id, ts, v2, Secret, Now)
            .Should().Be(SvixWebhookVerifier.Result.NoMatchingSignature);
    }

    [Fact]
    public void Malformed_base64_signature_entry_is_skipped()
    {
        var ts = Ts(Now);
        SvixWebhookVerifier.Verify(Payload, Id, ts, "v1,!!!not-base64!!!", Secret, Now)
            .Should().Be(SvixWebhookVerifier.Result.NoMatchingSignature);
    }

    [Fact]
    public void Secret_that_is_not_valid_base64_returns_InvalidSecret()
    {
        var ts = Ts(Now);
        SvixWebhookVerifier.Verify(Payload, Id, ts, "v1,whatever", "whsec_!!!not-base64!!!", Now)
            .Should().Be(SvixWebhookVerifier.Result.InvalidSecret);
    }
}
