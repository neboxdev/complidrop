using FluentAssertions;

namespace CompliDrop.Api.Tests;

/// <summary>
/// Mechanical CI gate for <see href="https://github.com/neboxdev/complidrop/blob/main/docs/adr/0052-extraction-trust-is-its-own-column.md">ADR 0052</see>
/// — <c>Document.ExtractionTrust</c> has exactly one READ surface (the vendor coverage rollup) and exactly
/// three WRITE surfaces (the worker's two, plus the request-side confirmation).
///
/// <para>
/// Why mechanically. ADR 0052 inherits ADR 0042's document-level CARVE-OUT: the dashboard counts, the
/// <c>?status=</c> list and its badges, the CSV/PDF export and the per-document compliance badge do NOT
/// move on the trust axis, because the documents list already renders a separate extraction badge beside
/// the compliance badge (ADR 0042 Amendment 1's test). Nothing BEHAVIOURAL can pin "no other surface
/// consults trust" — a new read site added to the dashboard would simply be a new, green feature — so the
/// carve-out is pinned the way this repo pins its other reviewer-memory rules:
/// <see cref="Adr0050EnforcementTests"/> and <c>ExportDisclaimerTests</c> both scan production source.
/// </para>
///
/// <para>
/// The write side matters for the same reason in reverse: trust is only durable across a re-arm because
/// the QUEUE writers leave it alone. A <c>.SetProperty(d =&gt; d.ExtractionTrust, …)</c> appearing in a
/// fourth place — most obviously in <c>Reextract</c>, where it looks like tidy bookkeeping — restores the
/// exact conflation this ADR removes.
/// </para>
///
/// <para>
/// Adding a legitimate surface is allowed and cheap: extend the list below, which forces the reader past
/// the ADR 0042 carve-out question rather than around it. Anti-no-op floors match the sibling gates — the
/// scan must find the source tree and a plausible number of files, or it fails closed.
/// </para>
/// </summary>
public class Adr0052EnforcementTests
{
    /// <summary>Every production file allowed to mention <c>ExtractionTrust</c>, with the role that earns
    /// it the mention. Paths are relative to <c>api/CompliDrop.Api/</c>, forward-slashed.</summary>
    private static readonly Dictionary<string, string> AllowedMentions = new()
    {
        ["Entities/Document.cs"] = "the enum and the property themselves",
        ["Data/ModelConfiguration.cs"] = "the column mapping + store default",
        ["BackgroundServices/ExtractionWorker.cs"] = "PersistSuccess + the two terminal-failure writers",
        ["Endpoints/DocumentEndpoints.cs"] = "ResolveManualReview — the human confirmation",
        ["Endpoints/VendorEndpoints.cs"] = "ComputeCoverage — the ONE read surface (ADR 0042 carve-out)",
    };

    /// <summary>Below this the walk found the wrong tree and every assertion would be vacuous.</summary>
    private const int MinScannedFiles = 50;

    private static DirectoryInfo ProductionRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 8 && dir != null; i++, dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "api", "CompliDrop.Api");
            if (Directory.Exists(Path.Combine(candidate, "Endpoints"))) return new DirectoryInfo(candidate);
        }
        throw new DirectoryNotFoundException(
            $"Could not locate api/CompliDrop.Api from {AppContext.BaseDirectory}");
    }

    /// <summary>Production sources, excluding build output and the EF <c>Migrations</c> tree — a migration
    /// and its designer/snapshot name every column by construction, so they carry no decision.</summary>
    private static List<(string Relative, string Text)> ProductionSources()
    {
        var root = ProductionRoot();
        return [.. root.EnumerateFiles("*.cs", SearchOption.AllDirectories)
            .Select(f => (Relative: Path.GetRelativePath(root.FullName, f.FullName).Replace('\\', '/'), File: f))
            .Where(x => !x.Relative.StartsWith("bin/", StringComparison.Ordinal)
                && !x.Relative.StartsWith("obj/", StringComparison.Ordinal)
                && !x.Relative.StartsWith("Migrations/", StringComparison.Ordinal))
            .Select(x => (x.Relative, Text: File.ReadAllText(x.File.FullName)))];
    }

    [Fact]
    public void ExtractionTrust_is_mentioned_only_where_ADR_0052_says_it_may_be()
    {
        var sources = ProductionSources();
        sources.Should().HaveCountGreaterThan(MinScannedFiles,
            "the scan must have found the real production tree, or every assertion below is vacuous");

        var mentions = sources
            .Where(s => s.Text.Contains("ExtractionTrust", StringComparison.Ordinal))
            .Select(s => s.Relative)
            .OrderBy(p => p, StringComparer.Ordinal)
            .ToList();

        mentions.Should().BeEquivalentTo(AllowedMentions.Keys,
            "a NEW surface reading Document.ExtractionTrust has to answer ADR 0042's carve-out question "
            + "first (does some other surface already disclose this state beside the compliance badge?), "
            + "and a new WRITER has to answer why the distrust should not survive a re-arm. Extend "
            + "AllowedMentions deliberately — do not delete this assertion");
    }

    [Fact]
    public void The_queue_re_arm_does_not_write_trust()
    {
        // The narrow, high-value half of the gate above, spelled out so the failure message names the bug
        // rather than a set difference. Reextract is allowed to MENTION ExtractionTrust — its comment
        // explains the deliberate absence, and naming the forbidden shape there is the whole point of the
        // comment — so whole-line comments are stripped first, exactly as Adr0050EnforcementTests does for
        // the same reason: prose about a forbidden call must not read as the call itself.
        var endpoints = StripLineComments(ProductionSources()
            .Single(s => s.Relative == "Endpoints/DocumentEndpoints.cs").Text);

        endpoints.Should().Contain("ResolveManualReview",
            "anti-no-op: the file we read must be the one that owns the confirmation writer");
        endpoints.Should().Contain("SetProperty(d => d.ExtractionStatus, ExtractionStatus.Pending)",
            "anti-no-op: the stripper must have left the re-arm's own SetProperty list intact");
        endpoints.Should().NotContain("SetProperty(d => d.ExtractionTrust",
            "Reextract's ExecuteUpdateAsync must leave trust alone — writing it there is precisely the "
            + "conflation #459 removes (the re-arm would destroy the ADR 0042 distrust signal again)");
    }

    /// <summary>Drops whole-line <c>//</c> comments (and the <c>///</c> doc-comment lines that are a
    /// special case of them). Deliberately not a real parser: the only false negative it can produce is a
    /// trailing comment on a code line, which cannot hide a statement.</summary>
    internal static string StripLineComments(string source) =>
        string.Join('\n', source.Split('\n')
            .Where(line => !line.TrimStart().StartsWith("//", StringComparison.Ordinal)));

    [Fact]
    public void The_comment_stripper_removes_prose_without_touching_code()
    {
        // The gate above is only as good as its stripper — a stripper that dropped everything would make
        // the NotContain assertion pass on any source at all. Hermetic fixtures, the Adr0050 discipline.
        StripLineComments("    // SetProperty(d => d.ExtractionTrust, x)\n    var a = 1;")
            .Should().NotContain("ExtractionTrust").And.Contain("var a = 1;");
        StripLineComments("    .SetProperty(d => d.ExtractionTrust, x) // why\n")
            .Should().Contain("SetProperty(d => d.ExtractionTrust",
                "a trailing comment must not smuggle the statement past the gate");
    }
}
