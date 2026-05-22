using CompliDrop.Api.Services.Extraction;

namespace CompliDrop.Api.Tests.TestHelpers;

/// <summary>
/// Canned <see cref="IGoogleAuthTokenProvider"/> for the Gemini Vertex path. The real provider loads a
/// Google service-account credential, which the test host has no access to, so the Vertex tests swap in
/// this fake to assert the client attaches the access token as a Bearer header (and to exercise the
/// "not configured" guard via <paramref name="isConfigured"/>).
/// </summary>
public sealed class FakeGoogleAuthTokenProvider(
    string? token = "fake-vertex-access-token",
    bool isConfigured = true) : IGoogleAuthTokenProvider
{
    public bool IsConfigured { get; } = isConfigured;

    public Task<string?> GetAccessTokenAsync(CancellationToken ct) => Task.FromResult(token);
}
