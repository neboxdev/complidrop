using System.Reflection;
using CompliDrop.Api.Entities;
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
/// exists to reject. The plumbing underneath all of that — the tree walk, the comment stripper, the
/// brace-matching body extractor and the occurrence counter — belongs to
/// <see cref="TestHelpers.SourceScan"/> and is self-tested in <c>SourceScanTests</c>; this class holds
/// only the invariant it enforces.
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
        SourceScan.Count(body, GuardCall).Should().Be(1,
            "ADR 0030 Amendment 3: the pure re-grade runs inside DocumentWriteConcurrency's REPEATABLE "
            + "READ + bounded re-run. Without it a PUT /fields committing in the read→compute→write window "
            + "leaves the row holding the EDITED limit beside THIS verdict — a stored Compliant over a "
            + "value somebody just lowered, with check rows citing what the row no longer holds, and "
            + "nothing to heal it (#461)");

        SourceScan.Count(body, EvaluateCall).Should().Be(1,
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
        var body = SourceScan.ExtractMethodBody(
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
            .SelectMany(s => Enumerable.Repeat(s.Relative, SourceScan.Count(s.Text, EvaluateCall)))
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

        var body = SourceScan.ExtractMethodBody(unguarded, RunCheckSignature);
        // Proven, not assumed: the fixture's one call IS present, so the rejection below comes from the
        // MISSING guard rather than from an empty read.
        SourceScan.Count(body, EvaluateCall).Should().Be(1);

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

        var body = SourceScan.ExtractMethodBody(hoisted, RunCheckSignature);
        SourceScan.Count(body, GuardCall).Should().Be(1, "the guard-presence assertion must PASS here…");
        SourceScan.Count(body, EvaluateCall).Should().Be(1, "…and so must the call-count one, so only ORDER rejects it");

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

    /// <summary>The Amendment 5 comparison the census below covers.</summary>
    private const string OutcomeMatchesSignature = "private static bool OutcomeMatches(";

    /// <summary>
    /// The <see cref="ComplianceCheck"/> columns deliberately OUTSIDE the comparison, each for a stated
    /// reason: <c>Id</c> is freshly minted per evaluation, <c>DocumentId</c> is the key the outcomes are
    /// paired ON, and <c>CheckedAt</c> is the fan-out's own clock. None of the three is an assertion ABOUT
    /// the document, so a difference in one is not a moved input.
    /// </summary>
    private static readonly string[] NonAssertionCheckColumns =
        [nameof(ComplianceCheck.Id), nameof(ComplianceCheck.DocumentId), nameof(ComplianceCheck.CheckedAt)];

    /// <summary>Below this the extractor latched onto the wrong span and the census is vacuous.</summary>
    private const int MinOutcomeMatchesLines = 8;

    [Fact]
    public void OutcomeMatches_compares_every_assertion_bearing_ComplianceCheck_column()
    {
        // Amendment 5's detection is a MECHANISM on the input side — a fresh grade that disagrees IS the
        // signal, so nothing enumerates verdict inputs and a column added to Document tomorrow is covered
        // the day it is added. The comparison of the resulting CHECK ROWS is the one half that is a
        // hand-written enumeration, and nothing kept it honest: a column added to ComplianceCheck would
        // drop silently out of it, so a genuinely-changed outcome would compare EQUAL, no mover would be
        // found, and the stale verdict this whole amendment exists to correct would be left standing.
        //
        // The codebase's answer to an enumeration that must not drift is a mechanical census (the
        // HarnessSmokeTests shape): adding a column goes RED until somebody decides whether it is part of
        // the assertion, and records that decision in the exclusion list above.
        var body = SourceScan.ExtractMethodBody(
            File.ReadAllText(SourceScan.ProductionFile("Services", "ComplianceCheckService.cs")),
            OutcomeMatchesSignature);

        body.Split('\n').Length.Should().BeGreaterOrEqualTo(MinOutcomeMatchesLines,
            $"the extracted OutcomeMatches body is implausibly short ({body.Length} chars) — the extractor "
            + "latched onto the wrong span and this census would pass on an empty read");

        var scalars = typeof(ComplianceCheck)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            // Navigations are the entity graph, not the assertion: ComputeOutcome never populates them and
            // an EvaluationOutcome carries none.
            .Where(p => p.PropertyType.Namespace?.StartsWith("CompliDrop", StringComparison.Ordinal) != true)
            .Select(p => p.Name)
            .ToList();

        scalars.Should().Contain(NonAssertionCheckColumns,
            "the exclusion list must still name real columns — a renamed one would otherwise silently "
            + "excuse a column that no longer exists while leaving its replacement uncompared");

        foreach (var column in scalars.Except(NonAssertionCheckColumns))
            body.Should().Contain($".{column}",
                $"ComplianceCheck.{column} is part of what a check row ASSERTS, so two evaluations that "
                + "differ in it are not the same evaluation. Either compare it in OutcomeMatches or add it "
                + "to NonAssertionCheckColumns with the reason (#470, ADR 0030 Amendment 5)");
    }

    [Fact]
    public void The_census_rejects_a_comparison_that_dropped_a_column()
    {
        // The census's own anti-no-op: prove it REJECTS the regression it exists to catch, rather than
        // passing because every property name happens to appear somewhere in a long method.
        const string dropped = """
            class C
            {
                private static bool OutcomeMatches(in EvaluationOutcome fresh, in EvaluationOutcome applied)
                {
                    if (fresh.Status != applied.Status || fresh.ClearExistingChecks != applied.ClearExistingChecks)
                        return false;
                    if (!fresh.ClearExistingChecks) return true;
                    if (fresh.NewChecks.Count != applied.NewChecks.Count) return false;
                    var appliedByRule = applied.NewChecks.ToDictionary(c => c.ComplianceRuleId);
                    foreach (var check in fresh.NewChecks)
                    {
                        if (!appliedByRule.TryGetValue(check.ComplianceRuleId, out var before)) return false;
                        if (before.IsPassed != check.IsPassed) return false;
                    }
                    return true;
                }
            }
            """;

        var body = SourceScan.ExtractMethodBody(dropped, OutcomeMatchesSignature);
        body.Should().Contain(".IsPassed", "the fixture's remaining comparisons must PASS the census…");

        var act = () => body.Should().Contain($".{nameof(ComplianceCheck.ActualValue)}");
        act.Should().Throw<Exception>(
            "…so only the DROPPED column rejects it. A shape like this leaves a check row citing a value "
            + "the row no longer holds, with the headline verdict unchanged — exactly #470's terminal state");
    }
}
