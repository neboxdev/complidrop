namespace CompliDrop.Api.Configuration;

public class ResendSettings
{
    public string ApiKey { get; set; } = string.Empty;
    public string FromEmail { get; set; } = "notifications@complidrop.com";
    public string FromName { get; set; } = "CompliDrop";
    public string? WebhookSecret { get; set; }

    /// <summary>
    /// Whether a real email send would be attempted: both an API key and a from-address are present.
    /// The single source of truth for "Resend is live" — consulted by both
    /// <see cref="Services.ResendEmailService.IsEnabled"/> (the runtime send gate) and #271's
    /// <see cref="StartupEnvironmentBanner"/> (the boot-time email-mode label + Development warning),
    /// so the banner can never claim a mode the send path disagrees with.
    /// </summary>
    public bool WouldSend =>
        !string.IsNullOrWhiteSpace(ApiKey) && !string.IsNullOrWhiteSpace(FromEmail);
}
