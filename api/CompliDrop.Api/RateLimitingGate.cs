namespace CompliDrop.Api;

/// <summary>
/// Decides whether <c>app.UseRateLimiter()</c> should be wired in. A composition-root helper,
/// not request-pipeline middleware — kept at the project root so the folder structure doesn't
/// imply otherwise.
/// </summary>
/// <remarks>
/// The decision is configurable via <c>RateLimiting:Enabled</c> so integration tests (which have
/// no client IP to partition on inside <see cref="Microsoft.AspNetCore.TestHost.TestServer"/>) can
/// disable it. Defaults to <c>true</c> — and the default is fail-closed: any unparseable string
/// (e.g. <c>yes</c>, <c>1</c>) is treated as <c>true</c> rather than crashing host startup.
/// <para/>
/// In non-Development environments the disable is <em>not honored</em>: if the config tries to set
/// <c>RateLimiting:Enabled = false</c> in Staging/Production we log a warning and force the limiter
/// back on, because dropping the limiter silently disables <c>auth-strict</c> brute-force throttling
/// and the public-portal abuse limits — a silent failure mode that defeats the point of having
/// rate limiting at all.
/// </remarks>
public static class RateLimitingGate
{
    public static bool ShouldEnable(IHostEnvironment env, IConfiguration config, ILogger? logger = null)
    {
        // Read as a string and bool.TryParse — IConfiguration.GetValue<bool> throws FormatException
        // on values like "yes"/"1"/"on", which would crash startup. Treat unparseable as the safe
        // default (true) so a typo in prod config doesn't take the host down.
        var raw = config["RateLimiting:Enabled"];
        var configured = string.IsNullOrWhiteSpace(raw) || !bool.TryParse(raw, out var parsed) || parsed;
        if (configured) return true;

        if (env.IsDevelopment()) return false;

        logger?.LogWarning(
            "RateLimiting:Enabled=false ignored in {Environment} — rate limiting forced ON to "
            + "prevent silent drop of auth-strict and portal abuse limits.",
            env.EnvironmentName);
        return true;
    }
}
