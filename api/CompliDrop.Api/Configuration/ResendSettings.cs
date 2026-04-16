namespace CompliDrop.Api.Configuration;

public class ResendSettings
{
    public string ApiKey { get; set; } = string.Empty;
    public string FromEmail { get; set; } = "notifications@complidrop.com";
    public string FromName { get; set; } = "CompliDrop";
    public string? WebhookSecret { get; set; }
}
