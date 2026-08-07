using System.Text.RegularExpressions;
using FluentAssertions;

namespace CompliDrop.Api.Tests;

/// <summary>
/// Census: no API string may tell a user the product deletes, erases, or does something
/// irreversible (#398 / ADR 0013 Amendment 1).
///
/// <para>
/// This is the backend half of <c>frontend/src/test/marketing-claims.test.ts</c>, and round 2
/// of the #398 review is why it exists. The frontend census walks <c>frontend/src/**</c> plus
/// the repo README — so it structurally cannot see a server message, and the two CONFIRMED
/// majors of that round were server messages: both abort arms of the very endpoint the ticket
/// renamed still said *"so your account was not deleted"*. Those reach the customer verbatim
/// (the frontend's <c>friendly</c> returns <c>err.message</c> and toasts it), under a button
/// labelled "Close my account".
/// </para>
///
/// <para>
/// Behavioural assertions on that endpoint (<see cref="AccountManagementTests"/>) pin the three
/// messages someone already thought about. This pins the ones nobody has written yet — the same
/// argument the frontend census is built on, and the same argument as
/// <c>ExportDisclaimerTests</c>' whole-file scan, whose locator and comment-stripping this
/// borrows. It is a BACKSTOP, not a proof: it reads source, so copy assembled at runtime from
/// fragments can evade it.
/// </para>
///
/// <para>
/// Nothing here bans the identifiers. <c>DeleteAccount</c>, <c>DeleteDocument</c>,
/// <c>user.account_deleted</c> and <c>DeletedAt</c> are all names of real things and stay; every
/// pattern below needs at least two words, so a symbol cannot trip one.
/// </para>
/// </summary>
public sealed class DeletionClaimCensusTests
{
    private sealed record ClaimRule(Regex Pattern, string Why, params string[] MustCatch);

    private const string Retention =
        "nothing in CompliDrop hard-deletes (ADR 0013): closing an account scrubs the holder's email + "
        + "name and soft-deletes the user + org, while the vendors' contact details, the documents, the "
        + "uploaded blobs, the reminder logs, the Subscription row and the audit trail are all RETAINED — "
        + "and clearing DeletedAt restores the account, which ADR 0013 names as a BENEFIT";

    private static readonly ClaimRule[] BannedClaims =
    [
        new(new Regex(@"permanent(ly)?\s+(delet|remov|eras|destroy|wip|purg)", RegexOptions.IgnoreCase),
            $"{Retention}, so \"permanently\" is false twice over",
            "Permanently deletes your account and organization data.",
            "This permanently removes the vendor and everything they sent."),

        new(new Regex(@"\b(irreversibl[ey]|can(no|')t be reversed|cannot be reversed|deleted forever|unrecoverable)\b", RegexOptions.IgnoreCase),
            $"{Retention}. Nothing is irreversible",
            "Closing your account is irreversible.",
            "This action cannot be reversed."),

        new(new Regex(@"can(no|')t be undone|cannot be undone", RegexOptions.IgnoreCase),
            $"{Retention}. Say what the CUSTOMER cannot do instead",
            "This can't be undone."),

        new(new Regex(@"\bwe\s+(then\s+|also\s+|automatically\s+|permanently\s+)?(delete|erase|destroy|wipe|purge)\s+(your|all|every|everything)\b", RegexOptions.IgnoreCase),
            $"{Retention}. No purge job exists anywhere in this codebase",
            "When you close your account we delete your data."),

        // The shape that shipped and that this census exists for: an operation reporting on
        // an account in the erasure vocabulary. Both #398 round-2 majors match here.
        new(new Regex(@"\byour\s+account\s+(was|is|has been|will be)\s+(not\s+)?(permanently\s+)?(deleted|erased|destroyed|wiped|purged)\b", RegexOptions.IgnoreCase),
            $"{Retention}. The operation CLOSES an account — say so on the success arm AND on every abort arm",
            "We can't reach billing right now, so your account was not deleted. Please try again later.",
            "Your account has been deleted."),
    ];

    /// <summary>
    /// Resolve <c>api/CompliDrop.Api/</c> from the test assembly location (the
    /// <c>Adr0009EnforcementTests</c> / <c>ExportDisclaimerTests</c> locator).
    /// </summary>
    private static string FindProductionRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 8 && dir != null; i++, dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "api", "CompliDrop.Api");
            if (Directory.Exists(candidate)) return candidate;
        }

        throw new DirectoryNotFoundException(
            $"Could not locate api/CompliDrop.Api/ from {AppContext.BaseDirectory}");
    }

    /// <summary>
    /// Drop comments before scanning. A comment EXPLAINING the ban (this ticket left several
    /// beside the copy they corrected) must not read as the claim shipping. The <c>//</c> strip
    /// requires the slashes not to be preceded by <c>:</c> so a URL survives intact and cannot
    /// hide text after it — the frontend census's rule, same reason.
    /// </summary>
    private static string StripComments(string source) =>
        Regex.Replace(
            Regex.Replace(source, @"/\*[\s\S]*?\*/", " "),
            @"(^|[^:])//[^\n]*", "$1", RegexOptions.Multiline);

    /// <summary>Whitespace folded so a message wrapped across source lines still reads as one phrase.</summary>
    private static string Normalize(string source) => Regex.Replace(source, @"\s+", " ");

    [Fact]
    public void Every_rule_matches_the_copy_it_exists_to_ban()
    {
        // Without this the whole census could stop matching silently and every file below
        // would pass for the wrong reason. Each rule carries the real retired sentence where
        // there is one, plus the near-synonyms it was written to cover.
        var dark = BannedClaims
            .SelectMany(rule => rule.MustCatch
                .Where(copy => !rule.Pattern.IsMatch(Normalize(copy)))
                .Select(copy => $"{rule.Pattern} misses: {copy}"))
            .ToList();

        dark.Should().BeEmpty();
        BannedClaims.Should().HaveCountGreaterThanOrEqualTo(5,
            "a rule deleted rather than deliberately retired must redden here");
    }

    [Fact]
    public void No_api_source_file_claims_the_product_deletes_or_is_irreversible()
    {
        var root = FindProductionRoot();
        var scanned = 0;
        var violations = new List<string>();

        foreach (var file in Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(root, file).Replace('\\', '/');
            if (relative.StartsWith("obj/", StringComparison.OrdinalIgnoreCase)
                || relative.StartsWith("bin/", StringComparison.OrdinalIgnoreCase))
                continue;

            scanned++;
            var text = Normalize(StripComments(File.ReadAllText(file)));
            violations.AddRange(
                BannedClaims
                    .Where(rule => rule.Pattern.IsMatch(text))
                    .Select(rule => $"  {relative}: {rule.Pattern} — {rule.Why}"));
        }

        // A walk that reached nothing would pass vacuously — the failure mode the frontend
        // census guards with its own "scans the whole shipped surface" assertion.
        scanned.Should().BeGreaterThan(50, "the walk must actually reach the API source tree");
        violations.Should().BeEmpty(
            "no API string may tell a user the product deletes, erases, or does something irreversible");
    }
}
