using System.Net.Http.Headers;
using System.Text.Json;
using CompliDrop.Api.Configuration;
using Microsoft.Extensions.Options;

namespace CompliDrop.Api.Services;

public interface IEmailService
{
    bool IsEnabled { get; }

    /// <summary>
    /// Sends one email via Resend; returns the Resend message id, or null when the send was not
    /// accepted. <paramref name="idempotencyKey"/>, when set, is forwarded as Resend's
    /// <c>Idempotency-Key</c> header (24h server-side TTL): a re-attempt carrying the key of an
    /// already-accepted request is deduped at Resend instead of double-delivering. Callers whose
    /// sends can be retried (the reminder worker, ADR 0025) pass a deterministic key; one-shot
    /// transactional sends (verify / reset emails) may leave it null.
    /// </summary>
    Task<string?> SendAsync(string toEmail, string subject, string htmlBody, CancellationToken ct, string? idempotencyKey = null);
}

public class ResendEmailService(
    IHttpClientFactory httpFactory,
    IOptions<ResendSettings> settings,
    IHostEnvironment env,
    ILogger<ResendEmailService> logger) : IEmailService
{
    private readonly ResendSettings _cfg = settings.Value;

    // The single send gate, shared with #271's StartupEnvironmentBanner (ResendSettings.WouldSend) so
    // the boot-time email-mode label can never drift from the runtime behaviour.
    public bool IsEnabled => _cfg.WouldSend;

    public async Task<string?> SendAsync(string toEmail, string subject, string htmlBody, CancellationToken ct, string? idempotencyKey = null)
    {
        if (!IsEnabled)
        {
            logger.LogWarning("Resend not configured — skipping email to {To}", toEmail);
            // Dev-only QA aid (#359): in Development email is silent (no Resend key, #271) and
            // verify/reset tokens are stored hashed, so surfacing the would-be message here is the
            // only way to drive the email flows by hand. Strictly Development-gated — in
            // Staging/Production the send path above runs instead, so a body is never logged.
            if (env.IsDevelopment())
            {
                logger.LogInformation(
                    "DEV email suppressed — would send to {To}\n  Subject: {Subject}\n  Body:\n{HtmlBody}",
                    toEmail, subject, htmlBody);
            }
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
        if (!string.IsNullOrEmpty(idempotencyKey))
            req.Headers.Add("Idempotency-Key", idempotencyKey);

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
