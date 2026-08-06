using CompliDrop.Api.Configuration;
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
    public sealed record TargetSummary(
        string Database, string BlobStorage, string Email, string Stripe, string Telemetry);

    /// <summary>
    /// Builds the redacted <see cref="TargetSummary"/> from configuration. Pure — reads config, never
    /// touches the network. Takes <see cref="IConfiguration"/> (not bound options) to match the sibling
    /// helpers (<see cref="DatabaseMigrator.ShouldAutoMigrate"/>, <see cref="RateLimitingGate.ShouldEnable"/>)
    /// and stay trivially unit-testable from a few in-memory entries.
    /// <para/>
    /// <see cref="IHostEnvironment"/> is a parameter only because <see cref="Telemetry"/> needs it: error
    /// reporting is the one target whose resolved state is not decided by configuration alone (ADR 0053's
    /// gate is a DSN <b>and</b> a non-Development environment).
    /// </summary>
    public static TargetSummary Describe(IConfiguration config, IHostEnvironment env) => new(
        Database: DescribeDatabase(config.GetConnectionString("Database")),
        BlobStorage: DescribeBlob(config["AzureStorage:ConnectionString"]),
        Email: DescribeEmail(BindResend(config)),
        Stripe: DescribeStripe(config["Stripe:SecretKey"]),
        Telemetry: DescribeTelemetry(config, env));

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

        // Email is "live" when Resend would actually send — both an API key AND a from-address present
        // (ResendSettings.WouldSend, the same gate IEmailService.IsEnabled uses, so the two can't
        // drift). The hourly reminder worker mails for real then, and the dev DB is a clone of prod
        // data with real vendor/user addresses. #271 deliberately REMOVED Resend:ApiKey from the dev
        // secrets to stay email-silent; this warns loudly if it ever reappears.
        if (BindResend(config).WouldSend)
            warnings.Add(
                "Resend is configured to send real email (Resend:ApiKey + Resend:FromEmail present) — "
                + "the local reminder/transactional senders will deliver REAL email. Remove Resend:ApiKey "
                + "in Development to stay email-silent.");

        if (RealBlobAccountName(config["AzureStorage:ConnectionString"]) is { } account)
            warnings.Add(
                $"AzureStorage points at a real Azure account ('{account}'), not Azurite — local "
                + "uploads write to it. Use UseDevelopmentStorage=true (Azurite) in Development.");

        // #386 / ADR 0053. Unlike the three above this target is INERT in Development — the gate
        // refuses to report there whatever the DSN says, which is the whole point of its second half.
        // It still warns, because #386's finding was a DSN sitting in local user-secrets, and that DSN
        // was the PRODUCTION project's: a live credential in a store it does not belong in, one gate
        // away from exporting exceptions raised against a clone of prod data. Never echoes the value.
        if (!string.IsNullOrWhiteSpace(config[BackendSentry.DsnKey]))
            warnings.Add(
                "Sentry:Dsn is set in Development. Nothing is uploaded (ADR 0053 gates on a "
                + "non-Development environment too), but a Sentry DSN is a production-only secret — "
                + "run `dotnet user-secrets remove \"Sentry:Dsn\"` to keep it out of dev entirely.");

        return warnings;
    }

    /// <summary>
    /// Logs the redacted banner (INFO, every environment) and, in Development only, a WARNING for each
    /// target that looks live. Call once at boot, before migrations run, so the resolved DB host is
    /// named immediately above the "Applying N migrations" line.
    /// </summary>
    public static void Log(IConfiguration config, IHostEnvironment env, ILogger logger)
    {
        var summary = Describe(config, env);

        logger.LogInformation(
            "Startup environment [{Environment}] — Database: {Database} | Blob: {BlobStorage} | "
            + "Email: {Email} | Stripe: {Stripe} | Errors: {Telemetry}",
            env.EnvironmentName, summary.Database, summary.BlobStorage, summary.Email, summary.Stripe,
            summary.Telemetry);

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

    private static string DescribeEmail(ResendSettings resend) =>
        resend.WouldSend
            ? "LIVE (Resend will send real email)"
            : "silent (Resend not configured to send — sends are skipped)";

    /// <summary>
    /// Binds the <c>Resend</c> section onto a fresh <see cref="ResendSettings"/> so the email mode is
    /// decided by the SAME <see cref="ResendSettings.WouldSend"/> gate the runtime
    /// <see cref="Services.ResendEmailService.IsEnabled"/> uses — the two can't drift. Binding (not a
    /// raw key read) applies <c>FromEmail</c>'s non-empty default, so the banner models the real send
    /// gate exactly, including in unit tests that only set the API key.
    /// </summary>
    private static ResendSettings BindResend(IConfiguration config)
    {
        var resend = new ResendSettings();
        config.GetSection("Resend").Bind(resend);
        return resend;
    }

    /// <summary>
    /// Whether backend errors leave this process, and if not, WHY not (#386 / ADR 0053).
    /// <para/>
    /// Sentry is the fourth config-gated outward-facing target in this host and #386's discovered scope
    /// was precisely the failure this banner exists to prevent: something outward-facing wired up
    /// invisibly, believed live for months, reporting nothing. Computed from
    /// <see cref="BackendSentry.IsEnabled"/> — the same predicate the SDK and the Serilog sink ask — so
    /// the banner and the behaviour cannot disagree; a second copy of the rule here is how they would.
    /// <para/>
    /// The DSN check comes first so a Development box with no DSN reads "no DSN" (true, and naming the
    /// environment would imply a DSN was present); a Development box WITH one reads "Development",
    /// which is the state that needs explaining. The DSN itself is NEVER echoed.
    /// </summary>
    private static string DescribeTelemetry(IConfiguration config, IHostEnvironment env)
    {
        if (BackendSentry.IsEnabled(config, env)) return "reporting to Sentry";
        return string.IsNullOrWhiteSpace(config[BackendSentry.DsnKey])
            ? "silent (no DSN)"
            : "silent (Development)";
    }

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
        // Match the shorthand as a SEGMENT, not by whole-string equality: a trailing ';' or an
        // appended "BlobEndpoint=…" (the documented form for retargeting the emulator host) is still
        // Azurite, e.g. "UseDevelopmentStorage=true;BlobEndpoint=http://127.0.0.1:10000/devstoreaccount1".
        if (string.Equals(SegmentValue(connectionString, "UseDevelopmentStorage"), "true", StringComparison.OrdinalIgnoreCase))
            return true;
        // …and the expanded form (devstoreaccount1 is Azurite's well-known account name).
        return string.Equals(AccountName(connectionString), "devstoreaccount1", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The <c>AccountName</c> segment, or null when absent. See <see cref="SegmentValue"/>.</summary>
    private static string? AccountName(string connectionString) => SegmentValue(connectionString, "AccountName");

    /// <summary>
    /// Reads ONLY the value of the named <c>key=value</c> segment from a <c>;</c>-delimited connection
    /// string. A hand-rolled segment scan (not a generic parser) keyed on an explicit key name, so it
    /// can never return the <c>AccountKey</c> / SAS token when asked for <c>AccountName</c> — the
    /// security invariant. Splits on the FIRST <c>=</c>, so a value containing <c>=</c> (e.g. a base64
    /// account key's <c>==</c> padding) stays bound to its own key and is never mis-attributed. Returns
    /// null when the key is absent.
    /// </summary>
    private static string? SegmentValue(string connectionString, string key)
    {
        foreach (var part in connectionString.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var idx = part.IndexOf('=');
            if (idx <= 0) continue;
            if (part[..idx].Trim().Equals(key, StringComparison.OrdinalIgnoreCase))
                return part[(idx + 1)..].Trim();
        }
        return null;
    }
}
