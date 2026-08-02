using CompliDrop.Api.Tests.TestHelpers;
using FluentAssertions;

namespace CompliDrop.Api.Tests;

/// <summary>
/// Mechanical CI gate for <see href="https://github.com/neboxdev/complidrop/blob/main/docs/adr/0030-compliance-verdict-combined-unit-of-work.md">ADR 0030</see>
/// Amendment 3 (#461) — the single-document pure re-grade runs inside
/// <c>DocumentWriteConcurrency</c>'s <c>REPEATABLE READ</c> + bounded re-run, and it is the ONLY
/// production caller of <c>IComplianceCheckService.EvaluateAsync</c>.
///
/// <para>
/// The guard lives at the CALL SITE, not inside the service, and that is a decision (Amendment 3
/// § Alternatives — Option J): <c>DocumentWriteConcurrency</c> needs an <see cref="Api.Data.AppDbContext"/>
/// and an <c>IResult</c> to answer with, transaction scope belongs to <c>Endpoints/</c> everywhere else in
/// this codebase, and the BATCHED fan-out deliberately does not take the guard. The cost of that choice is
/// exactly what this gate pays back: a second caller of <c>EvaluateAsync</c> added anywhere else re-opens
/// #461's window in a new place, silently, with every behavioural test still green — the bare method is a
/// <c>FirstOrDefaultAsync → ApplyEvaluationAsync → SaveChangesAsync</c> with no lock and no token, and
/// nothing about a single-threaded test can see that.
/// </para>
///
/// <para>
/// Same discipline as its neighbours (<see cref="Adr0050EnforcementTests"/>,
/// <see cref="Adr0052EnforcementTests"/>, <see cref="Adr0009EnforcementTests"/>): comments stripped so
/// prose ABOUT a shape cannot read as the shape, an anti-no-op floor so a renamed method or a botched read
/// fails the gate rather than emptying it, and hermetic fixtures proving the gate REJECTS the shapes it
/// exists to reject.
/// </para>
/// </summary>
public class Adr0030EnforcementTests
{
    /// <summary>The signature the pin anchors on, spelled exactly as the endpoint declares it.</summary>
    private const string RunCheckSignature = "private static Task<IResult> RunCheck(";

    /// <summary>
    /// The one call shape the gate counts. The leading dot matters: it matches the INVOCATION
    /// (<c>checker.EvaluateAsync(…)</c>) and not the interface declaration or the implementation's
    /// expression body, both of which live in <c>Services/ComplianceCheckService.cs</c> and neither of
    /// which is a call site.
    /// </summary>
    private const string EvaluateCall = ".EvaluateAsync(";

    private const string GuardCall = "DocumentWriteConcurrency.RunAsync(";

    /// <summary>
    /// Lowest plausible size for the extracted body. <c>RunCheck</c> is the guard call plus its callback
    /// plus their comments; materially smaller means the extractor latched onto the wrong span and every
    /// assertion below would pass vacuously.
    /// </summary>
    private const int MinBodyLines = 8;

    /// <summary>Below this the production walk found the wrong tree and the call-count pin is vacuous.</summary>
    private const int MinScannedFiles = 50;

    private static int Count(string haystack, string needle)
    {
        var (count, at) = (0, 0);
        while ((at = haystack.IndexOf(needle, at, StringComparison.Ordinal)) >= 0)
        {
            count++;
            at += needle.Length;
        }
        return count;
    }

    /// <summary>
    /// The shape assertions, so the hermetic fixtures below drive the SAME ones the production check runs.
    /// Counts alone are not enough — the pre-#461 shape has one <c>EvaluateAsync</c> and zero guards, but a
    /// shape that computed the verdict ABOVE the guard and merely RETURNED it from inside the callback
    /// would have both calls and still be un-retryable, since the re-run would re-serve the losing
    /// attempt's answer without recomputing anything. So the ORDER is pinned too.
    /// <para/>
    /// The anti-no-op floor deliberately does NOT live in here, unlike
    /// <see cref="Adr0050EnforcementTests.AssertAtomicReArmShape"/>'s. It is a property of the READ, and
    /// the fixtures below hand their body in directly — there is no extraction for them to have got wrong,
    /// so requiring them to clear a size floor would only mean padding them with filler.
    /// </summary>
    internal static void AssertGuardedRegradeShape(string body)
    {
        Count(body, GuardCall).Should().Be(1,
            "ADR 0030 Amendment 3: the pure re-grade runs inside DocumentWriteConcurrency's REPEATABLE "
            + "READ + bounded re-run. Without it a PUT /fields committing in the read→compute→write window "
            + "leaves the row holding the EDITED limit beside THIS verdict — a stored Compliant over a "
            + "value somebody just lowered, with check rows citing what the row no longer holds, and "
            + "nothing to heal it (#461)");

        Count(body, EvaluateCall).Should().Be(1,
            "exactly one re-grade per request — a second call would grade twice, once outside whatever "
            + "attempt committed");

        body.IndexOf(EvaluateCall, StringComparison.Ordinal).Should()
            .BeGreaterThan(body.IndexOf(GuardCall, StringComparison.Ordinal),
                "the evaluate must sit INSIDE the retryable callback. Hoisted above the guard it runs once, "
                + "on the losing snapshot, and the re-run re-serves that same answer — the retry would "
                + "reload nothing and recompute nothing, which is the whole mechanism");
    }

    [Fact]
    public void The_pure_re_grade_runs_inside_the_concurrency_guard()
    {
        var body = Adr0050EnforcementTests.ExtractMethodBody(
            File.ReadAllText(SourceScan.ProductionFile("Endpoints", "ComplianceEndpoints.cs")),
            RunCheckSignature);

        // Anti-no-op floor, on the READ. RunCheck is block-bodied precisely so the shared brace-matching
        // extractor spans the whole method: against an EXPRESSION body the first '{' it finds is the
        // callback lambda's, which would leave this gate inspecting two statements and passing on a
        // hoisted evaluate it could no longer see.
        body.Split('\n').Length.Should().BeGreaterOrEqualTo(MinBodyLines,
            $"the extracted RunCheck body is implausibly short ({body.Length} chars) — the extractor "
            + "latched onto the wrong span and this gate would be a silent no-op");

        AssertGuardedRegradeShape(body);
    }

    [Fact]
    public void EvaluateAsync_has_exactly_one_production_call_site_and_it_is_the_guarded_one()
    {
        // The half no behavioural test can reach. The guard is at the CALL SITE, so the invariant is not
        // "RunCheck is guarded" but "the guarded RunCheck is the only caller". A future endpoint that
        // re-grades a document — a bulk action, a post-import hook — re-opens #461 in a new place the
        // moment it calls the bare service method, and every existing test stays green.
        var root = SourceScan.ProductionRoot();
        var sources = root.EnumerateFiles("*.cs", SearchOption.AllDirectories)
            .Select(f => (Relative: Path.GetRelativePath(root.FullName, f.FullName).Replace('\\', '/'), File: f))
            .Where(x => !x.Relative.StartsWith("bin/", StringComparison.Ordinal)
                && !x.Relative.StartsWith("obj/", StringComparison.Ordinal))
            .Select(x => (x.Relative, Text: SourceScan.StripLineComments(File.ReadAllText(x.File.FullName))))
            .ToList();

        sources.Should().HaveCountGreaterThan(MinScannedFiles,
            "the scan must have found the real production tree, or the count below is vacuous");

        var callSites = sources
            .SelectMany(s => Enumerable.Repeat(s.Relative, Count(s.Text, EvaluateCall)))
            .ToList();

        callSites.Should().Equal(["Endpoints/ComplianceEndpoints.cs"],
            "IComplianceCheckService.EvaluateAsync is a bare read → compute → write with no lock and no "
            + "token; ADR 0030 Amendment 3 closes its window at its ONE call site. A new caller must "
            + "either take the same guard or justify why the window cannot matter there — and then update "
            + "this pin deliberately");
    }

    [Fact]
    public void The_pin_rejects_the_pre_461_unguarded_re_grade()
    {
        // Exactly what shipped before #461: load, grade, save, answer. One EvaluateAsync, no transaction,
        // no retry — and the interleave that leaves a Compliant verdict over a lowered limit.
        const string unguarded = """
            class C
            {
                private static Task<IResult> RunCheck(
                    Guid documentId,
                    IComplianceCheckService checker,
                    CancellationToken ct)
                {
                    var status = await checker.EvaluateAsync(documentId, ct);
                    return Results.Ok(new { data = new { status = status.ToString() }, error = (object?)null });
                }
            }
            """;

        var body = Adr0050EnforcementTests.ExtractMethodBody(unguarded, RunCheckSignature);
        // Proven, not assumed: the fixture's one call IS present, so the rejection below comes from the
        // MISSING guard rather than from an empty read.
        Count(body, EvaluateCall).Should().Be(1);

        var act = () => AssertGuardedRegradeShape(body);
        act.Should().Throw<Exception>("this shape is the #461 bug itself")
            .WithMessage("*REPEATABLE READ*");
    }

    [Fact]
    public void The_pin_rejects_a_re_grade_hoisted_above_the_guard()
    {
        // The subtler regression, and the reason the ordering assertion exists: both calls are present and
        // the counts are identical to production's, but the verdict is computed ONCE, outside the retry.
        // Every attempt then commits the same losing snapshot's answer, so the guard buys nothing at all.
        const string hoisted = """
            class C
            {
                private static Task<IResult> RunCheck(
                    Guid documentId,
                    IComplianceCheckService checker,
                    AppDbContext db,
                    ILoggerFactory loggerFactory,
                    CancellationToken ct)
                {
                    var status = await checker.EvaluateAsync(documentId, ct);
                    return DocumentWriteConcurrency.RunAsync(db, loggerFactory, documentId,
                        DocumentWriteConcurrency.RegradeConflictMessage,
                        innerCt => Task.FromResult(Results.Ok(new { data = new { status = status.ToString() } })),
                        onAttemptAbandoned: null,
                        ct);
                }
            }
            """;

        var body = Adr0050EnforcementTests.ExtractMethodBody(hoisted, RunCheckSignature);
        Count(body, GuardCall).Should().Be(1, "the guard-presence assertion must PASS here…");
        Count(body, EvaluateCall).Should().Be(1, "…and so must the call-count one, so only ORDER rejects it");

        var act = () => AssertGuardedRegradeShape(body);
        act.Should().Throw<Exception>("a re-grade computed outside the retry is re-served, never re-run")
            .WithMessage("*INSIDE the retryable callback*");
    }

    [Fact]
    public void The_pinned_signature_is_the_one_the_endpoint_actually_declares()
    {
        // If RunCheck is renamed or reshaped, fail LOUDLY here rather than leaving the gate above throwing
        // an opaque "signature not found" out of a helper.
        File.ReadAllText(SourceScan.ProductionFile("Endpoints", "ComplianceEndpoints.cs"))
            .Should().Contain(RunCheckSignature,
                "the ADR 0030 Amendment 3 gate anchors on this exact declaration; update the constant with "
                + "the rename");
    }
}
