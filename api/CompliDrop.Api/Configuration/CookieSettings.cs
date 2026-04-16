namespace CompliDrop.Api.Configuration;

public class CookieSettings
{
    public string? Domain { get; set; }
    public bool Secure { get; set; } = true;
    public string SameSite { get; set; } = "Lax";
}
