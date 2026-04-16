namespace CompliDrop.Api.Configuration;

public class StripeSettings
{
    public string SecretKey { get; set; } = string.Empty;
    public string PublishableKey { get; set; } = string.Empty;
    public string WebhookSecret { get; set; } = string.Empty;
    public string MonthlyPriceId { get; set; } = string.Empty;
    public string AnnualPriceId { get; set; } = string.Empty;
    public string FoundingPriceId { get; set; } = string.Empty;
}
