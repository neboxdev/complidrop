using Microsoft.Extensions.Options;

namespace CompliDrop.Api.Configuration;

public class FrontendSettings
{
    public string BaseUrl { get; set; } = "http://localhost:3000";
}

/// <summary>
/// Fails the boot outside Development if <c>Frontend:BaseUrl</c> is unset or a localhost/loopback URL
/// (#250). Every email-borne and copy/paste link — vendor portal, email-verify, password-reset,
/// Stripe checkout/billing return — is minted from this origin, so an unset prod value silently sent
/// real vendors dead <c>http://localhost:3000/portal/…</c> links. Validating at startup
/// (ValidateOnStart) turns that into a loud, immediate boot failure instead of a per-link surprise,
/// matching the fail-fast posture of the other required settings.
/// </summary>
public sealed class FrontendSettingsValidator(IHostEnvironment env) : IValidateOptions<FrontendSettings>
{
    public ValidateOptionsResult Validate(string? name, FrontendSettings options)
    {
        // The localhost default is correct for local dev; only guard real environments.
        if (env.IsDevelopment()) return ValidateOptionsResult.Success;

        var raw = options.BaseUrl?.Trim();
        if (string.IsNullOrWhiteSpace(raw))
            return ValidateOptionsResult.Fail(
                "Frontend:BaseUrl must be set outside Development — it is the public origin minted into "
                + "portal/verify/reset/checkout links sent to real users. Set it to the public site origin "
                + "(e.g. https://www.complidrop.com).");

        if (!Uri.TryCreate(raw, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            return ValidateOptionsResult.Fail(
                $"Frontend:BaseUrl must be an absolute http(s) URL, but was '{raw}'.");

        // Uri.IsLoopback covers localhost, 127.0.0.1, and ::1; the explicit host check is
        // belt-and-suspenders for any resolver edge.
        if (uri.IsLoopback || string.Equals(uri.Host, "localhost", StringComparison.OrdinalIgnoreCase))
            return ValidateOptionsResult.Fail(
                $"Frontend:BaseUrl is a localhost/loopback URL ('{raw}') outside Development — every "
                + "portal/verify/reset link would be dead for real users. Set it to the public site origin.");

        return ValidateOptionsResult.Success;
    }
}
