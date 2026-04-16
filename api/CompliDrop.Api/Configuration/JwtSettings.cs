using System.ComponentModel.DataAnnotations;

namespace CompliDrop.Api.Configuration;

public class JwtSettings
{
    [Required, MinLength(32)]
    public string Secret { get; set; } = string.Empty;

    [Required]
    public string Issuer { get; set; } = "complidrop-api";

    [Required]
    public string Audience { get; set; } = "complidrop-frontend";

    [Range(1, 1440)]
    public int SessionExpiryMinutes { get; set; } = 15;

    [Range(1, 365)]
    public int RefreshExpiryDays { get; set; } = 30;
}
