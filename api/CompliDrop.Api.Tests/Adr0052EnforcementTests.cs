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
        ["DTOs/Vendors/VendorDtos.cs"] = ProseOnlyLabel + " — VendorCoverage's contract comment names the "
            + "operand its Status is computed from; it reads and writes nothing",
        ["Services/DocumentGradingBasis.cs"] = ProseOnlyLabel + " — the basis BOTH of PersistSuccess's "
            + "conclusions are now computed from (#467 / ADR 0052 Amendment 1); it reads and writes "
            + "nothing, and the trust write stays in the worker",
    };

    /// <summary>The prefix that turns an allow-list entry into a PROSE-ONLY one. The label IS the
    /// enforcement trigger, so it is a constant both sides read rather than a string typed twice.</summary>
    private const string ProseOnlyLabel = "PROSE ONLY";

    /// <summary>
    /// The entries above whose entitlement is PROSE. A comment naming the column is cheap and useful; a
    /// LINE of code in one of these is a fifth writer or a second read surface arriving in a file the
    /// allow-list waves through whole. <see cref="Services_that_merely_TALK_about_trust_never_touch_it"/>
    /// makes that distinction mechanical rather than a promise in the value strings above.
    /// <para/>
    /// DERIVED from the label rather than re-listed (#467 review, S3). A second hand-maintained list is
    /// the same "promise in a value string" hole one level up: a file admitted with a PROSE ONLY label
    /// but forgotten here would get no mechanical enforcement at all. Same rule the repo applies to ADR
    /// 0050's <c>ClaimSql</c> (BUILT from <c>ZombieClaimTimeout</c>) and ADR 0046's <c>InputLengths</c>.
    /// </summary>
    private static IEnumerable<string> ProseOnlyMentions =>
        AllowedMentions
            .Where(kv => kv.Value.StartsWith(ProseOnlyLabel, StringComparison.Ordinal))
            .Select(kv => kv.Key);

    /// <summary>
    /// A token that must SURVIVE the comment stripper in each prose-only file — the per-file anti-no-op
    /// both sibling gates lead with, and which this one shipped without (#467 review, S2). Every anchor
    /// is CODE, so an over-stripping <see cref="SourceScan.StripLineComments"/> (a shared helper) takes
    /// the anchor with it and this gate goes RED instead of silently green while claiming to enforce ADR
    /// 0052's prose-only entitlement.
    /// <para/>
    /// A prose-only entry with no anchor here FAILS rather than being waved through, so deriving the set
    /// from the label above cannot buy an entitlement nothing checks.
    /// </summary>
    private static readonly Dictionary<string, string> ProseOnlyAnchors = new()
    {
        ["DTOs/Vendors/VendorDtos.cs"] = "record VendorCoverage",
        ["Services/DocumentGradingBasis.cs"] = "AfterPendingCommitAsync",
    };

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
        sources.Should().HaveCountGreaterThan(MinScannedFiles,
            "the scan must have found the real production tree, or every assertion below is vacuous");

        var proseOnly = ProseOnlyMentions.ToArray();
        proseOnly.Should().NotBeEmpty(
            "anti-no-op: the derivation must still recognise the label the allow-list writes, or this "
            + "gate enforces the prose-only entitlement over an EMPTY set of files");

        // The REVERSE direction (#467 review round 2, S4), and it is the same "promise in a value
        // string" hole one level up. ProseOnlyMentions is derived by StartsWith, so an entry whose
        // label drifted off the START of its value — a reword, an added lead-in like "the #460 basis —
        // PROSE ONLY — …" — silently leaves the derived set and its whole file goes back to being
        // waved through by AllowedMentions, while this dictionary still names it as anchored. The
        // per-entry loop below cannot see that: it only ever visits files the derivation returned.
        ProseOnlyAnchors.Keys.Should().BeSubsetOf(proseOnly,
            "every file this class claims to anchor must still BE prose-only. A key here that the "
            + "derivation no longer returns is a file with a whole-file waiver and no code-level check "
            + "at all — put " + ProseOnlyLabel + " back at the start of its AllowedMentions value, or "
            + "delete the anchor deliberately");

        foreach (var relative in proseOnly)
        {
            ProseOnlyAnchors.TryGetValue(relative, out var anchor).Should().BeTrue(
                relative + " is allow-listed " + ProseOnlyLabel + ", so it needs an entry in "
                + "ProseOnlyAnchors before this gate can claim to enforce anything about it — a label with "
                + "no anchor is the promise-in-a-value-string hole one level up");

            var stripped = SourceScan.StripLineComments(
                sources.Single(s => s.Relative == relative).Text);
            stripped.Should().Contain(anchor!,
                "anti-no-op (#467 review, S2): the stripper is SHARED, so an over-stripping change to it "
                + "would leave " + relative + "'s NotContain below trivially satisfied while the sibling "
                + "gates went red on their own Contain anchors. This anchor is code, so it dies with any "
                + "text the stripper should have kept");
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

        // The THIRD shape, which neither assertion above can see (#465 review round 2). MarkVerified
        // FORCES its trust write into the UPDATE — the SetTrust argument one writer over, because it
        // commits under READ COMMITTED where the row can have moved under its snapshot. `IsModified = true`
        // is neither an assignment nor a SetProperty, so it passes both counts, and it is only SOUND
        // beside the basis check that re-asks readability of the row the commit will leave: forced without
        // it, a stale Trusted lands over a competitor's fresher Distrusted on a value that competitor had
        // just broken. Counted, not located, so a second force arriving anywhere in this file — most
        // plausibly bolted onto UpdateFields, which needs none (REPEATABLE READ aborts instead) — has to
        // answer for itself here.
        Regex.Matches(endpoints, @"Property\(d => d\.ExtractionTrust\)").Count.Should().Be(1,
            "exactly one request-side writer forces trust: MarkVerified, whose check earns it (ADR 0052 "
            + "Amendment 2). Deleting that force is the other direction — a confirmation whose conclusion "
            + "matches its own snapshot would then leave a competitor's judgment standing in its place");
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
