using Npgsql;

namespace CompliDrop.Api;

/// <summary>
/// Logs a redacted, one-line summary of the data-bearing / outward-facing targets the process is
/// wired to (database host, blob account, email mode, Stripe mode) at boot, and — in Development —
/// a loud WARNING for any target that looks like a LIVE/production resource. A composition-root
/// helper (like <see cref="DatabaseMigrator"/> and <see cref="RateLimitingGate"/>), not
/// request-pipeline middleware — kept at the project root so the folder structure doesn't imply
/// otherwise.
/// </summary>
/// <remarks>
/// Exists because of #271: the dev environment was silently pointed at the PRODUCTION Neon database
/// and the prod Azure storage account. The hazard was invisible — nothing at boot named which DB /
/// storage / Stripe mode / email sender the process had resolved, so a local <c>dotnet run</c> could
/// auto-migrate prod, race prod's extraction worker, write test rows into prod, and mail real vendors
/// from prod's Resend before anyone noticed. The secrets are now rotated to an isolated Neon dev
/// branch + Azurite + an email-silent (no Resend key) dev profile, but the durable guard against a
/// recurrence is <em>visibility</em>: this banner NAMES the resolved targets every boot, so the
/// mistake can't hide again.
/// <para/>
/// Two log levels, by design:
/// <list type="bullet">
///   <item><b>The banner is INFO in every environment.</b> In prod it is a useful operational
///   sanity line (confirms which Neon branch / storage account / Stripe mode prod serves); in dev it
///   is the at-a-glance "am I pointed at the right place?" check.</item>
///   <item><b>The misconfig WARNINGs are Development-only.</b> A live Stripe key, a present Resend
///   key, and a real (non-Azurite) storage account are CORRECT in prod and a hazard in dev, so the
///   warning only fires under <c>IHostEnvironment.IsDevelopment()</c>. It is a loud
///   warning, not a boot abort: a deliberate "point local at prod for a one-off" is a legitimate
///   (founder-sanctioned) mode, and a hard fail would be hostile to it — mirrors the
///   force-on-but-don't-crash posture of <see cref="RateLimitingGate"/>.</item>
/// </list>
/// Security invariant (mirrors the ADR 0026 validator family): the banner NEVER echoes a secret —
/// not the DB password, the storage account key, the Resend API key, or the Stripe key. It prints
/// only hostnames, account names, and key <em>modes</em> (test/live) derived from prefixes. The
/// redaction is pinned by <c>StartupEnvironmentBannerTests</c>.
/// </remarks>
public static class StartupEnvironmentBanner
{
    /// <summary>
    /// A redacted, human-readable summary of the targets the process is wired to. Every field is
    /// safe to log: no field can contain a password, account key, or API key.
    /// </summary>
    public sealed record TargetSummary(string Database, string BlobStorage, string Email, string Stripe);

    /// <summary>
    /// Builds the redacted <see cref="TargetSummary"/> from configuration. Pure — reads config, never
    /// touches the network. Takes <see cref="IConfiguration"/> (not bound options) to match the sibling
    /// helpers (<see cref="DatabaseMigrator.ShouldAutoMigrate"/>, <see cref="RateLimitingGate.ShouldEnable"/>)
    /// and stay trivially unit-testable from a few in-memory entries.
    /// </summary>
    public static TargetSummary Describe(IConfiguration config) => new(
        Database: DescribeDatabase(config.GetConnectionString("Database")),
        BlobStorage: DescribeBlob(config["AzureStorage:ConnectionString"]),
        Email: DescribeEmail(config["Resend:ApiKey"]),
        Stripe: DescribeStripe(config["Stripe:SecretKey"]));

    /// <summary>
    /// One message per data-bearing / outward-facing target that looks like a LIVE/production
    /// resource — the payload of the Development loud-misconfig guard. Env-agnostic by design (the
    /// environment gate lives in <see cref="Log"/>) so the predicate set is testable in isolation.
    /// Empty when every target looks dev-safe.
    /// </summary>
    public static IReadOnlyList<string> LiveResourceWarnings(IConfiguration config)
    {
        var warnings = new List<string>();

        if (IsLiveStripeKey(config["Stripe:SecretKey"]))
            warnings.Add(
                "Stripe:SecretKey is a LIVE key — a local checkout/billing test writes real "
                + "subscription state. Use an sk_test_ key in Development.");

        // Email is "live" the moment a Resend API key is present — IEmailService.SendAsync delivers
        // for real (the dev DB is a clone of prod data with real vendor/user addresses, so the hourly
        // reminder worker would mail them). #271 deliberately REMOVED Resend:ApiKey from the dev
        // secrets to stay email-silent; this warns loudly if it ever reappears.
        if (!string.IsNullOrWhiteSpace(config["Resend:ApiKey"]))
            warnings.Add(
                "Resend:ApiKey is set — the local reminder/transactional senders will deliver REAL "
                + "email. Remove Resend:ApiKey in Development to stay email-silent.");

        if (RealBlobAccountName(config["AzureStorage:ConnectionString"]) is { } account)
            warnings.Add(
                $"AzureStorage points at a real Azure account ('{account}'), not Azurite — local "
                + "uploads write to it. Use UseDevelopmentStorage=true (Azurite) in Development.");

        return warnings;
    }

    /// <summary>
    /// Logs the redacted banner (INFO, every environment) and, in Development only, a WARNING for each
    /// target that looks live. Call once at boot, before migrations run, so the resolved DB host is
    /// named immediately above the "Applying N migrations" line.
    /// </summary>
    public static void Log(IConfiguration config, IHostEnvironment env, ILogger logger)
    {
        var summary = Describe(config);

        logger.LogInformation(
            "Startup environment [{Environment}] — Database: {Database} | Blob: {BlobStorage} | "
            + "Email: {Email} | Stripe: {Stripe}",
            env.EnvironmentName, summary.Database, summary.BlobStorage, summary.Email, summary.Stripe);

        if (!env.IsDevelopment()) return;

        foreach (var warning in LiveResourceWarnings(config))
            logger.LogWarning("Development is wired to a LIVE resource (#271): {Warning}", warning);
    }

    // ---- redacting describers ------------------------------------------------------------------

    private static string DescribeDatabase(string? connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString)) return "not configured";
        try
        {
            // NpgsqlConnectionStringBuilder lets us read ONLY the host + database — the password and
            // username never enter the rendered string. The default port (5432) is noise; show it only
            // when non-default so a redirected dev port stands out.
            var b = new NpgsqlConnectionStringBuilder(connectionString);
            var host = string.IsNullOrWhiteSpace(b.Host) ? "?" : b.Host;
            var db = string.IsNullOrWhiteSpace(b.Database) ? "?" : b.Database;
            return b.Port == 5432 ? $"{host} (db: {db})" : $"{host}:{b.Port} (db: {db})";
        }
        catch (Exception)
        {
            // This is a boot-path DIAGNOSTIC — it must never take startup down, whatever exception type
            // Npgsql raises for a malformed string (it varies by bad-keyword vs bad-value vs version).
            // Never echo the (possibly secret-bearing) value on a parse failure — name the shape only.
            return "unparseable connection string";
        }
    }

    private static string DescribeBlob(string? connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString)) return "not configured";
        if (IsAzurite(connectionString)) return "Azurite (local emulator)";

        var account = AccountName(connectionString);
        return string.IsNullOrWhiteSpace(account)
            ? "configured (account name not found)"
            : $"account '{account}'";
    }

    private static string DescribeEmail(string? resendApiKey) =>
        string.IsNullOrWhiteSpace(resendApiKey)
            ? "silent (no Resend API key — sends are skipped)"
            : "LIVE (Resend will send real email)";

    private static string DescribeStripe(string? secretKey)
    {
        if (string.IsNullOrWhiteSpace(secretKey)) return "not configured";
        if (IsLiveStripeKey(secretKey)) return "LIVE mode";
        if (secretKey.StartsWith("sk_test_", StringComparison.Ordinal)
            || secretKey.StartsWith("rk_test_", StringComparison.Ordinal))
            return "test mode";
        return "configured (unrecognized key prefix)";
    }

    // ---- predicates (shared by describers + warnings) ------------------------------------------

    private static bool IsLiveStripeKey(string? secretKey) =>
        !string.IsNullOrWhiteSpace(secretKey)
        && (secretKey.StartsWith("sk_live_", StringComparison.Ordinal)
            || secretKey.StartsWith("rk_live_", StringComparison.Ordinal));

    /// <summary>
    /// The blob account name when the connection string names a REAL Azure account, or null when it
    /// targets Azurite / is empty / has no account name. Used both to describe the target and to
    /// decide the Development warning, so the two can never disagree.
    /// </summary>
    private static string? RealBlobAccountName(string? connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString) || IsAzurite(connectionString)) return null;
        var account = AccountName(connectionString);
        return string.IsNullOrWhiteSpace(account) ? null : account;
    }

    private static bool IsAzurite(string connectionString)
    {
        var trimmed = connectionString.Trim();
        // The shorthand the dev secrets use…
        if (trimmed.Equals("UseDevelopmentStorage=true", StringComparison.OrdinalIgnoreCase))
            return true;
        // …and the expanded form (devstoreaccount1 is Azurite's well-known account name).
        return string.Equals(AccountName(connectionString), "devstoreaccount1", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Reads ONLY the <c>AccountName</c> segment from an Azure storage connection string. A
    /// hand-rolled segment scan (not a generic parser) so it can never return the <c>AccountKey</c> /
    /// SAS token — the security invariant. Returns null when absent.
    /// </summary>
    private static string? AccountName(string connectionString)
    {
        foreach (var part in connectionString.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var idx = part.IndexOf('=');
            if (idx <= 0) continue;
            if (part[..idx].Trim().Equals("AccountName", StringComparison.OrdinalIgnoreCase))
                return part[(idx + 1)..].Trim();
        }
        return null;
    }
}
