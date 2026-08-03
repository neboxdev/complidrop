using System.Text.RegularExpressions;
using CompliDrop.Api.BackgroundServices;
using CompliDrop.Api.Tests.TestHelpers;
using FluentAssertions;

namespace CompliDrop.Api.Tests;

/// <summary>
/// Mechanical CI gate for <see href="https://github.com/neboxdev/complidrop/blob/main/docs/adr/0052-extraction-trust-is-its-own-column.md">ADR 0052</see>
/// — <c>Document.ExtractionTrust</c> has exactly one READ surface (the vendor coverage rollup) and exactly
/// FOUR WRITE surfaces across two files: the worker's three (<c>PersistSuccess</c> plus the two
/// terminal-failure writers, all funnelled through <c>SetTrust</c>) and the request-side confirmation.
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
/// fifth place — most obviously in <c>Reextract</c> or <c>RequeueInterruptedAsync</c>, where it looks like
/// tidy bookkeeping — restores the exact conflation this ADR removes. Both are pinned by name below, and
/// the worker's own writers are counted rather than merely allow-listed by file: a whole-file allow-list
/// would have let a fifth worker writer in silently.
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
        ["DTOs/Vendors/VendorDtos.cs"] = "PROSE ONLY — VendorCoverage's contract comment names the "
            + "operand its Status is computed from; it reads and writes nothing",
        ["Services/DocumentGradingBasis.cs"] = "PROSE ONLY — the basis BOTH of PersistSuccess's "
            + "conclusions are now computed from (#467 / ADR 0052 Amendment 1); it reads and writes "
            + "nothing, and the trust write stays in the worker",
    };

    /// <summary>
    /// The files above whose entitlement is PROSE. A comment naming the column is cheap and useful; a
    /// LINE of code in one of these is a fifth writer or a second read surface arriving in a file the
    /// allow-list waves through whole. <see cref="Services_that_merely_TALK_about_trust_never_touch_it"/>
    /// makes that distinction mechanical rather than a promise in the value strings above.
    /// </summary>
    private static readonly string[] ProseOnlyMentions =
        ["DTOs/Vendors/VendorDtos.cs", "Services/DocumentGradingBasis.cs"];

    /// <summary>Below this the walk found the wrong tree and every assertion would be vacuous.</summary>
    private const int MinScannedFiles = 50;

    /// <summary>Production sources, excluding build output and the EF <c>Migrations</c> tree — a migration
    /// and its designer/snapshot name every column by construction, so they carry no decision.</summary>
    private static List<(string Relative, string Text)> ProductionSources()
    {
        var root = SourceScan.ProductionRoot();
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
    public void Services_that_merely_TALK_about_trust_never_touch_it()
    {
        // The allow-list above whitelists a file WHOLE, so "PROSE ONLY" in a value string buys nothing on
        // its own — the same hole the endpoints gate's occurrence count closes for DocumentEndpoints.
        // `Services/DocumentGradingBasis.cs` is the one that makes this worth enforcing rather than
        // documenting: it MATERIALIZES a Document (PropertyValues.ToObject), so a future edit assigning or
        // reading trust on that instance is one line away and would look like tidy prediction — while
        // being either a fifth writer (ADR 0052 §2 says there are four) or a second read surface (the ADR
        // 0042 document-level carve-out says there is one). Comments survive; code does not.
        var sources = ProductionSources();
        foreach (var relative in ProseOnlyMentions)
        {
            var stripped = SourceScan.StripLineComments(
                sources.Single(s => s.Relative == relative).Text);
            stripped.Should().NotContain("ExtractionTrust", relative
                + " is allow-listed for PROSE only: every mention of the column there must be inside a "
                + "comment. A line of code is a new writer or a new read surface, and both owe ADR 0052 "
                + "an answer before they are added to this list on their own terms");
        }
    }

    [Fact]
    public void The_queue_re_arm_does_not_write_trust()
    {
        // The narrow, high-value half of the gate above, spelled out so the failure message names the bug
        // rather than a set difference. Reextract is allowed to MENTION ExtractionTrust — its comment
        // explains the deliberate absence, and naming the forbidden shape there is the whole point of the
        // comment — so whole-line comments are stripped first, exactly as Adr0050EnforcementTests does for
        // the same reason (one shared stripper since #459's review; SourceScanTests pins its behaviour).
        var endpoints = SourceScan.StripLineComments(ProductionSources()
            .Single(s => s.Relative == "Endpoints/DocumentEndpoints.cs").Text);

        endpoints.Should().Contain("ResolveManualReview",
            "anti-no-op: the file we read must be the one that owns the confirmation writer");
        endpoints.Should().Contain("SetProperty(d => d.ExtractionStatus, ExtractionStatus.Pending)",
            "anti-no-op: the stripper must have left the re-arm's own SetProperty list intact");
        endpoints.Should().NotContain("SetProperty(d => d.ExtractionTrust",
            "Reextract's ExecuteUpdateAsync must leave trust alone — writing it there is precisely the "
            + "conflation #459 removes (the re-arm would destroy the ADR 0042 distrust signal again)");

        // The request-side MIRROR of the worker's per-writer count (#459 review round 2). The assertion
        // above only rejects the ExecuteUpdateAsync shape, and AllowedMentions whitelists this file WHOLE —
        // so a plain tracked-entity write, `doc.ExtractionTrust = ExtractionTrust.Trusted;` dropped into
        // UpdateDocument, DeleteDocument or a brand-new helper, used to pass every gate in this class while
        // .claude/reviewers.md says "FOUR writers, and only four". Counted rather than located, exactly as
        // the worker half is, so it also catches the write arriving somewhere nobody thought to look.
        Regex.Matches(endpoints, @"\.ExtractionTrust\s*=[^=]").Count.Should().Be(1,
            "ResolveManualReview is the ONE request-side trust writer (ADR 0052 §2). A second assignment "
            + "in this file is a fifth writer overall — most dangerously one that grants Trusted without "
            + "asking DocumentFieldReadability, which is how a document with an unreadable canonical value "
            + "gets back into vendor coverage");
    }

    [Fact]
    public void The_worker_writes_trust_in_exactly_three_places_and_forces_every_one_of_them()
    {
        // Per-WRITER, not per-file (#459 review). The allow-list above whitelists the whole worker, so a
        // fourth `doc.ExtractionTrust = …` inside it — RequeueInterruptedAsync being the most plausible
        // one, since it already resets the rest of the queue tuple and would look like tidy bookkeeping —
        // used to pass silently while every deploy that interrupted a distrusted document's re-extract
        // restored its vendor to Covered. Counted rather than located, so it also catches the write
        // arriving in a brand-new helper.
        var worker = SourceScan.StripLineComments(ProductionSources()
            .Single(s => s.Relative == "BackgroundServices/ExtractionWorker.cs").Text);

        worker.Should().Contain("private static void SetTrust(",
            "anti-no-op: the file we read must still declare the one funnel the counts below assume");
        worker.Should().Contain("private async Task RequeueInterruptedAsync(",
            "anti-no-op, and the member this class's doc comment claims is pinned BY NAME (#459 review "
            + "round 2 — it was not). A renamed or deleted requeue must fail here rather than silently "
            + "make the assertions below describe a member that no longer exists");

        Regex.Matches(worker, @"\.ExtractionTrust\s*=[^=]").Count.Should().Be(1,
            "every trust write in the worker goes through SetTrust, whose single assignment this is. A "
            + "second assignment means a writer bypassed the funnel — and therefore skipped the IsModified "
            + "force, so its decision silently no-ops whenever it matches the minutes-old snapshot "
            + "ProcessDocumentAsync is still holding (ADR 0052 §2)");

        Regex.Matches(worker, @"SetTrust\(db, doc,").Count.Should().Be(3,
            "exactly three worker writers own trust: PersistSuccess, MarkFailed and RecordFailedAttempt's "
            + "TERMINAL arm. A FOURTH call is a queue writer taking a decision it has no basis for — the "
            + "retry arm, the shutdown requeue and the re-arm all leave trust alone, and that ABSENCE is "
            + "the #459 fix. A call going missing is the other direction: an extraction that no longer "
            + "re-decides trust");

        worker.Should().NotContain("SetProperty(d => d.ExtractionTrust",
            "the endpoints gate's twin, and the hole it closes is this file's (#459 review round 2): the "
            + "two counts above only see `.ExtractionTrust =` and `SetTrust(db, doc,`, so an "
            + "ExecuteUpdateAsync bolted onto RequeueInterruptedAsync — which already resets the rest of "
            + "the queue tuple, and where resetting trust looks like tidy bookkeeping — matched neither "
            + "and passed all three");

        ExtractionWorker.ClaimSql.Should().NotContain("ExtractionTrust",
            "the claim moves PIPELINE POSITION only. Adding trust to its SET list would re-arm the "
            + "distrust away on every claim — the original bug, one layer lower than Reextract");
    }
}
