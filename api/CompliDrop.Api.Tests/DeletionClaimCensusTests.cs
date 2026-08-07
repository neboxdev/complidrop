using System.Text.Json;
using System.Text.RegularExpressions;
using CompliDrop.Api.Tests.TestHelpers;
using FluentAssertions;

namespace CompliDrop.Api.Tests;

/// <summary>
/// Census: no API string may tell a user the product deletes, erases, or does something
/// irreversible (#398 / ADR 0013 Amendment 1).
///
/// <para>
/// The RULE TABLE is not written here. It is <c>SharedFixtures/deletion-claim-rules.json</c>, the
/// single table this census and <c>frontend/src/test/marketing-claims.test.ts</c> both read — the
/// ContactEmail arrangement (ADR 0038), adopted for the same reason: round 2 of the #398 review
/// found the two tables already unequal (5 rules here, 7 there, each catching sentences the other
/// missed) while three records called them "the same census". What differs between the two
/// censuses is the WALK, and that difference is why both exist: this one reads
/// <c>api/CompliDrop.Api/**/*.cs</c>, where a SERVER message lives. The frontend walk covers
/// <c>frontend/src/**</c> + the repo README and structurally cannot see one — which is where both
/// of that round's CONFIRMED majors were: both abort arms of the very endpoint the ticket renamed
/// still said *"so your account was not deleted"*. Those reach the customer verbatim (the
/// frontend's <c>friendly</c> returns <c>err.message</c> and toasts it), under a button labelled
/// "Close my account". The frontend census additionally carries the #403 MARKETING rules; those
/// are not mirrored here and are not claimed to be.
/// </para>
///
/// <para>
/// Behavioural assertions on that endpoint (<see cref="AccountManagementTests"/>) pin the three
/// messages someone already thought about. This pins the ones nobody has written yet — the same
/// argument the frontend census is built on, and the same argument as
/// <c>ExportDisclaimerTests</c>' whole-file scan. It is a BACKSTOP, not a proof: it reads source,
/// so copy assembled at runtime from fragments can evade it, and a regex census over natural
/// language can never be complete. It catches a KNOWN list — the one in the fixture — and the
/// records say exactly that.
/// </para>
///
/// <para>
/// Nothing here bans the identifiers. <c>DeleteAccount</c>, <c>DeleteDocument</c>,
/// <c>user.account_deleted</c> and <c>DeletedAt</c> are all names of real things and stay; every
/// pattern in the fixture needs at least two words, so a symbol cannot trip one.
/// </para>
/// </summary>
public sealed class DeletionClaimCensusTests
{
    private sealed record ClaimRule(string Id, Regex Pattern, string Why, string[] MustCatch);

    private sealed record FixtureRule(string Id, string Pattern, string Why, string Sample, string[]? AlsoCatches);

    private sealed record Fixture(string Retention, FixtureRule[] Rules);

    /// <summary>
    /// The shared table, compiled with THIS side's engine. <see cref="RegexOptions.CultureInvariant"/>
    /// is deliberate: case folding under a Turkish culture would quietly change which sentences the
    /// gate catches, and the frontend's <c>i</c> flag has no such dependency.
    /// </summary>
    private static readonly ClaimRule[] BannedClaims = LoadRules();

    private static ClaimRule[] LoadRules()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "SharedFixtures", "deletion-claim-rules.json");
        if (!File.Exists(path))
            throw new FileNotFoundException(
                "The shared deletion-claim rule table is missing. It drives this census AND "
                + "frontend/src/test/marketing-claims.test.ts (#398) — do not inline the rules here instead.",
                path);

        var fixture = JsonSerializer.Deserialize<Fixture>(File.ReadAllText(path),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? throw new InvalidOperationException($"Could not parse {path}");

        return [.. fixture.Rules.Select(rule => new ClaimRule(
            rule.Id,
            new Regex(rule.Pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant),
            fixture.Retention + rule.Why,
            [rule.Sample, .. rule.AlsoCatches ?? []]))];
    }

    /// <summary>
    /// Fold the shapes one sentence takes across <c>.cs</c> source into one comparable string, mirroring
    /// the frontend census's <c>normalize</c>. Whitespace so a message wrapped across source lines still
    /// reads as one phrase; curly punctuation and HTML entities because the retired vendor dialog
    /// literally shipped <c>This can’t be undone.</c> with a U+2019, the rules spell the ASCII form, and
    /// the API composes HTML email bodies (<c>SendPasswordResetEmailAsync</c>,
    /// <c>ReminderBackgroundService.BuildVendorBody</c>) where an entity is how the apostrophe arrives.
    /// Folding on only one side is how the shared table stops being shared (#398 round 2).
    /// </summary>
    private static string Normalize(string source) =>
        Regex.Replace(
            Regex.Replace(
                Regex.Replace(source, "&rsquo;|&lsquo;|&apos;|&#39;|[‘’]", "'"),
                "&mdash;|&ndash;|&nbsp;", " ")
            .Replace("&quot;", "\"").Replace('“', '"').Replace('”', '"'),
            @"\s+", " ");

    [Fact]
    public void Every_rule_matches_the_copy_it_exists_to_ban()
    {
        // The shared table is compiled by two different regex engines and normalized by two different
        // functions, so "the two censuses agree" is not something the fixture can assert — it is proved
        // on each side by running every sample through THAT side's engine. Without this the whole census
        // could stop matching silently and every file below would pass for the wrong reason.
        var dark = BannedClaims
            .SelectMany(rule => rule.MustCatch
                .Where(copy => !rule.Pattern.IsMatch(Normalize(copy)))
                .Select(copy => $"{rule.Id} ({rule.Pattern}) misses: {copy}"))
            .ToList();

        dark.Should().BeEmpty();
        BannedClaims.Should().HaveCountGreaterThanOrEqualTo(7,
            "a rule deleted from the shared fixture rather than deliberately retired must redden here "
            + "— on BOTH sides, which is the point of the fixture");
        BannedClaims.Select(r => r.Id).Should().OnlyHaveUniqueItems(
            "the id is how a failure names the rule, and how the two censuses are compared by hand");
    }

    [Fact]
    public void No_api_source_file_claims_the_product_deletes_or_is_irreversible()
    {
        var root = SourceScan.ProductionRoot().FullName;
        var scanned = 0;
        var violations = new List<string>();

        foreach (var file in Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(root, file).Replace('\\', '/');
            if (relative.StartsWith("obj/", StringComparison.OrdinalIgnoreCase)
                || relative.StartsWith("bin/", StringComparison.OrdinalIgnoreCase))
                continue;

            scanned++;
            // Comments stripped so PROSE ABOUT the ban — this ticket left several beside the copy it
            // corrected — does not read as the claim shipping. SourceScan owns the stripper (#398
            // round 2 / S4): a private third copy in a gate class is what SourceScan exists to stop.
            var text = Normalize(SourceScan.StripComments(File.ReadAllText(file)));
            violations.AddRange(
                BannedClaims
                    .Where(rule => rule.Pattern.IsMatch(text))
                    .Select(rule => $"  {relative}: {rule.Id} ({rule.Pattern}) — {rule.Why}"));
        }

        // A walk that reached nothing would pass vacuously — the failure mode the frontend
        // census guards with its own "scans the whole shipped surface" assertion.
        scanned.Should().BeGreaterThan(50, "the walk must actually reach the API source tree");
        violations.Should().BeEmpty(
            "no API string may tell a user the product deletes, erases, or does something irreversible");
    }
}
