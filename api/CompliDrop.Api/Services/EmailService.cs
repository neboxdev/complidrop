using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using CompliDrop.Api.Configuration;
using Microsoft.Extensions.Options;

namespace CompliDrop.Api.Services;

public interface IEmailService
{
    bool IsEnabled { get; }
    Task<string?> SendAsync(string toEmail, string subject, string htmlBody, CancellationToken ct);
}

public class ResendEmailService(
    IHttpClientFactory httpFactory,
    IOptions<ResendSettings> settings,
    ILogger<ResendEmailService> logger) : IEmailService
{
    private readonly ResendSettings _cfg = settings.Value;

    public bool IsEnabled =>
        !string.IsNullOrWhiteSpace(_cfg.ApiKey) && !string.IsNullOrWhiteSpace(_cfg.FromEmail);

    public async Task<string?> SendAsync(string toEmail, string subject, string htmlBody, CancellationToken ct)
    {
        if (!IsEnabled)
        {
            logger.LogWarning("Resend not configured — skipping email to {To}", toEmail);
            return null;
        }

        var payload = new
        {
            from = $"{_cfg.FromName} <{_cfg.FromEmail}>",
            to = new[] { toEmail },
            subject,
            html = htmlBody
        };

        var client = httpFactory.CreateClient("resend");
        using var req = new HttpRequestMessage(HttpMethod.Post, "https://api.resend.com/emails")
        {
            Content = JsonContent.Create(payload)
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _cfg.ApiKey);

        using var resp = await client.SendAsync(req, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
        {
            logger.LogError("Resend send failed {Status} {Body}", (int)resp.StatusCode, body);
            return null;
        }
        try
        {
            using var doc = JsonDocument.Parse(body);
            return doc.RootElement.TryGetProperty("id", out var id) ? id.GetString() : null;
        }
        catch
        {
            return null;
        }
    }
}
