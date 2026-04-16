using CompliDrop.Api.Configuration;
using Google.Apis.Auth.OAuth2;
using Microsoft.Extensions.Options;

namespace CompliDrop.Api.Services.Extraction;

public interface IGoogleAuthTokenProvider
{
    Task<string?> GetAccessTokenAsync(CancellationToken ct);
    bool IsConfigured { get; }
}

public class GoogleAuthTokenProvider : IGoogleAuthTokenProvider
{
    private readonly DocumentAiSettings _cfg;
    private GoogleCredential? _credential;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private static readonly string[] Scopes =
    {
        "https://www.googleapis.com/auth/cloud-platform"
    };

    public GoogleAuthTokenProvider(IOptions<DocumentAiSettings> settings)
    {
        _cfg = settings.Value;
    }

    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(_cfg.CredentialsPath) || !string.IsNullOrWhiteSpace(_cfg.CredentialsJson);

    public async Task<string?> GetAccessTokenAsync(CancellationToken ct)
    {
        if (!IsConfigured) return null;

        await _gate.WaitAsync(ct);
        try
        {
            _credential ??= await LoadCredentialAsync(ct);
            if (_credential is null) return null;
            var scoped = _credential.CreateScoped(Scopes);
            return await scoped.UnderlyingCredential.GetAccessTokenForRequestAsync(cancellationToken: ct);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<GoogleCredential?> LoadCredentialAsync(CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(_cfg.CredentialsJson))
        {
            return GoogleCredential.FromJson(_cfg.CredentialsJson);
        }
        if (!string.IsNullOrWhiteSpace(_cfg.CredentialsPath) && File.Exists(_cfg.CredentialsPath))
        {
            await using var stream = File.OpenRead(_cfg.CredentialsPath);
            return await GoogleCredential.FromStreamAsync(stream, ct);
        }
        return null;
    }
}
