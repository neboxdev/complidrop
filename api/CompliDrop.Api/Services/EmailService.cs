using System.Net.Http.Headers;
using System.Text.Json;
using CompliDrop.Api.Configuration;
using Microsoft.Extensions.Options;

namespace CompliDrop.Api.Services;

/// <summary>
/// Who turns a rejected send into an <c>Error</c>-level log line — which, since #386 / ADR 0053, means
/// a metered Sentry event.
/// </summary>
/// <remarks>
/// The distinction exists because one revoked <c>Resend:ApiKey</c> makes EVERY send fail, and the
/// reminder worker sends inside nested <c>org → reminder → doc → recipient</c> loops, re-attempted on
/// every qualifying tick of the org-local day (ADR 0025's catch-up window, up to 16). At the design
/// scale in <c>.claude/reviewers.md</c> that is tens of thousands of events a day from ONE failure —
/// enough to exhaust a Sentry plan's quota, after which Sentry 429s, the SDK drops, and genuine 500s
/// stop being reported. Backend monitoring would then be dark again, through a different door than the
/// one #386 closed.
/// </remarks>
public enum SendFailureReporting
{
    /// <summary>
    /// The default, and the right answer for a ONE-OFF send: a verification mail, a password reset, a
    /// vendor portal link. There is no caller-side aggregate to fold it into and each failure is its own
    /// incident, so the service logs it at <c>Error</c> itself.
    /// </summary>
    PerSend,

    /// <summary>
    /// A BULK send whose caller counts outcomes and raises ONE <c>Error</c> for the batch. The service
    /// logs the per-send detail at <c>Warning</c> instead — the console/JSON sink still records every
    /// line in full; only the metered copy is folded up.
    /// </summary>
    CallerAggregates,
}

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
    /// <para/>
    /// <paramref name="failureReporting"/> decides at which level a REJECTED send is logged — see
    /// <see cref="SendFailureReporting"/>. It changes logging only; the return value, the retry
    /// contract and Resend's own behaviour are identical either way.
    /// </summary>
    Task<string?> SendAsync(
        string toEmail,
        string subject,
        string htmlBody,
        CancellationToken ct,
        string? idempotencyKey = null,
        SendFailureReporting failureReporting = SendFailureReporting.PerSend);
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

    public async Task<string?> SendAsync(
        string toEmail,
        string subject,
        string htmlBody,
        CancellationToken ct,
        string? idempotencyKey = null,
        SendFailureReporting failureReporting = SendFailureReporting.PerSend)
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
            // ONE template, one call site — only the LEVEL moves, so the console/JSON sink records the
            // same line either way and a bulk caller cannot accidentally lose the detail.
            logger.Log(
                failureReporting == SendFailureReporting.PerSend ? LogLevel.Error : LogLevel.Warning,
                "Resend send failed {Status} {Body}", (int)resp.StatusCode, body);
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
