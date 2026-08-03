using CompliDrop.Api.Data;
using CompliDrop.Api.Entities;
using CompliDrop.Api.Tests.TestHelpers;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;

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

    /// <summary>
    /// The locals <c>OutcomeMatches</c> binds a <see cref="ComplianceCheck"/> ROW to: <c>check</c> (the
    /// fresh evaluation's row), <c>before</c> (the row the page applied) and <c>c</c> (the pairing-key
    /// lambda's parameter). A column counts as compared only when it is read off at least
    /// <see cref="MinCheckRowOperands"/> DISTINCT ones — which is what makes the census say "this column is
    /// compared BETWEEN THE TWO ROWS" rather than "this token appears somewhere in the method".
    /// <para/>
    /// It has to be a declared list because source text carries no types. That is the same shape as
    /// <see cref="NonAssertionCheckColumns"/> and it fails the same safe way: renaming a local goes RED
    /// here until somebody updates it. Matching the whole body instead is what the first cut did, and three
    /// identifiers already in that method would have satisfied a future column for free — <c>fresh.Status</c>
    /// and <c>applied.Status</c> (an <c>EvaluationOutcome</c>, not a check row) a <c>Status</c> column,
    /// <c>NewChecks.Count</c> a <c>Count</c> one, <c>StringComparison.Ordinal</c> an <c>Ordinal</c> one.
    /// </summary>
    private static readonly string[] CheckRowOperands = ["check", "before", "c"];

    /// <summary>
    /// Two, because a comparison reads the column off BOTH sides. The value comparisons spell that
    /// <c>before.X != check.X</c>; the pairing key spells it <c>ToDictionary(c =&gt; c.ComplianceRuleId)</c>
    /// keyed against <c>check.ComplianceRuleId</c> — different shape, same fact, so one rule covers both
    /// and neither needs its own exemption.
    /// </summary>
    private const int MinCheckRowOperands = 2;

    /// <summary>
    /// The MAPPED COLUMNS of <paramref name="entity"/>, read off the EF model rather than off reflection —
    /// the authoritative "is this a column" answer, and one that cannot drift from
    /// <c>Data/ModelConfiguration</c>.
    /// <para/>
    /// The reflection filter this replaced excluded every property whose TYPE is declared under
    /// <c>CompliDrop</c>, which is wrong in BOTH directions. This codebase's enums live in
    /// <c>CompliDrop.Api.Entities</c> (<c>ComplianceStatus</c>, <c>ExtractionStatus</c>,
    /// <c>ExtractionTrust</c> are all declared in <c>Entities/Document.cs</c>), so an enum-typed
    /// <see cref="ComplianceCheck"/> column — this codebase's own idiom for a graded assertion — was
    /// classified as a NAVIGATION and never censused, leaving the gate green while the column dropped
    /// silently out of <c>OutcomeMatches</c>. And a collection navigation (<c>ICollection&lt;T&gt;</c>,
    /// namespace <c>System.Collections.Generic</c>) was DEMANDED in the comparison. <c>IEntityType</c>
    /// answers both by construction: enums are properties, navigations are not. Pinned by
    /// <see cref="The_census_reads_its_columns_off_the_EF_model_so_an_enum_column_counts"/>.
    /// </summary>
    private static IReadOnlyList<string> MappedColumns(Type entity)
    {
        // Model-only: building the model never opens a connection, so this needs no fixture and no
        // container. The connection string is a syntactic placeholder.
        using var context = new SystemDbContext(
            new DbContextOptionsBuilder<SystemDbContext>()
                .UseNpgsql("Host=localhost;Database=model-only;Username=u;Password=p")
                .Options);
        var entityType = context.Model.FindEntityType(entity)
            ?? throw new InvalidOperationException(
                $"{entity.Name} is not a mapped entity — the census would read nothing");
        return [.. entityType.GetProperties().Select(p => p.Name)];
    }

    /// <summary>
    /// Whether <paramref name="body"/> reads <paramref name="column"/> off <paramref name="operand"/> as a
    /// WHOLE identifier. The boundary check on both sides is the point: a bare <c>Contain(".Notes")</c> is
    /// satisfied by a longer name, so a future <c>Note</c> column would be excused by the existing
    /// <c>check.Notes</c> and an <c>Actual</c> one by <c>check.ActualValue</c>.
    /// </summary>
    private static bool ReadsColumnOff(string body, string operand, string column)
    {
        var needle = $"{operand}.{column}";
        for (var at = 0; (at = body.IndexOf(needle, at, StringComparison.Ordinal)) >= 0; at += needle.Length)
        {
            var after = at + needle.Length;
            if ((at == 0 || !IsIdentifierChar(body[at - 1]))
                && (after >= body.Length || !IsIdentifierChar(body[after])))
                return true;
        }
        return false;

        static bool IsIdentifierChar(char c) => char.IsLetterOrDigit(c) || c == '_';
    }

    /// <summary>
    /// The census's per-column rule, callable with an arbitrary column name so the anti-no-ops can drive it
    /// against the REAL body with a hypothetical column instead of hand-copying an assertion.
    /// </summary>
    internal static void AssertColumnIsCompared(string body, string column)
    {
        var operands = CheckRowOperands.Where(o => ReadsColumnOff(body, o, column)).ToList();
        operands.Should().HaveCountGreaterOrEqualTo(MinCheckRowOperands,
            $"ComplianceCheck.{column} is part of what a check row ASSERTS, so two evaluations that differ "
            + "in it are not the same evaluation, and OutcomeMatches must read it off BOTH rows "
            + $"(found it on: {(operands.Count == 0 ? "neither row" : string.Join(", ", operands))}). Either "
            + "compare it in OutcomeMatches or add it to NonAssertionCheckColumns with the reason "
            + "(#470, ADR 0030 Amendment 5)");
    }

    /// <summary>
    /// The census itself — the reflection, the column selection and the per-column rule in ONE place, so
    /// the production check and its anti-no-op below drive the SAME logic. Extracted because the anti-no-op
    /// used to hand-copy a single <c>Contain</c> line and assert that FluentAssertions throws when a string
    /// lacks a substring: a tautology independent of the enumeration, the filter and the loop, which is
    /// exactly why it could not see the enum hole in that filter.
    /// </summary>
    internal static void AssertComparesEveryAssertionBearingColumn(string body)
    {
        var columns = MappedColumns(typeof(ComplianceCheck));

        columns.Should().Contain(NonAssertionCheckColumns,
            "the exclusion list must still name real columns — a renamed one would otherwise silently "
            + "excuse a column that no longer exists while leaving its replacement uncompared");

        var assertionBearing = columns.Except(NonAssertionCheckColumns).ToList();
        assertionBearing.Should().NotBeEmpty(
            "a census with nothing to census is a no-op — if every ComplianceCheck column is excluded, "
            + "OutcomeMatches asserts nothing about a check row and #470's check-tuple half is gone");

        foreach (var column in assertionBearing)
            AssertColumnIsCompared(body, column);
    }

    /// <summary>The real, current <c>OutcomeMatches</c> body — the census's input, and the anti-no-ops'.</summary>
    private static string ReadOutcomeMatchesBody() =>
        SourceScan.ExtractMethodBody(
            File.ReadAllText(SourceScan.ProductionFile("Services", "ComplianceCheckService.cs")),
            OutcomeMatchesSignature);

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
        var body = ReadOutcomeMatchesBody();

        body.Split('\n').Length.Should().BeGreaterOrEqualTo(MinOutcomeMatchesLines,
            $"the extracted OutcomeMatches body is implausibly short ({body.Length} chars) — the extractor "
            + "latched onto the wrong span and this census would pass on an empty read");

        AssertComparesEveryAssertionBearingColumn(body);
    }

    [Fact]
    public void The_census_reads_its_columns_off_the_EF_model_so_an_enum_column_counts()
    {
        // The half no ComplianceCheck-shaped assertion can reach today, because that entity happens to
        // carry no enum and no collection navigation. Document carries all three kinds, so it is where the
        // COLUMN SELECTOR itself is pinned — and both directions matter: the reflection filter this
        // replaced would have skipped ComplianceStatus (an enum declared under CompliDrop, so it read as a
        // navigation) and demanded ComplianceChecks (a collection navigation, so it read as a scalar).
        var columns = MappedColumns(typeof(Document));

        columns.Should().Contain(nameof(Document.ComplianceStatus),
            "an enum-typed column IS a column — and it is this codebase's idiom for a graded assertion, so "
            + "an enum added to ComplianceCheck must be censused rather than silently skipped");
        columns.Should().NotContain(nameof(Document.Vendor),
            "a reference navigation is the entity graph, not a column");
        columns.Should().NotContain(nameof(Document.ComplianceChecks),
            "and neither is a collection navigation — demanding it in the comparison would be the same "
            + "error in the other direction");
    }

    [Fact]
    public void The_census_rejects_a_comparison_that_dropped_a_column()
    {
        // The census's own anti-no-op: prove it REJECTS the regression it exists to catch — by running the
        // REAL census (its enumeration, its column selection, its per-column rule) against a fixture, not
        // by re-typing one of its assertions.
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
                        if (before.IsPassed != check.IsPassed
                            || !string.Equals(before.Notes, check.Notes, StringComparison.Ordinal))
                            return false;
                    }
                    return true;
                }
            }
            """;

        var body = SourceScan.ExtractMethodBody(dropped, OutcomeMatchesSignature);
        // Proven, not assumed: the fixture's SURVIVING comparisons satisfy the census's own per-column
        // rule, so the rejection below comes from the DROPPED column and not from a fixture the census
        // could not read at all.
        AssertColumnIsCompared(body, nameof(ComplianceCheck.IsPassed));
        AssertColumnIsCompared(body, nameof(ComplianceCheck.Notes));
        AssertColumnIsCompared(body, nameof(ComplianceCheck.ComplianceRuleId));

        var act = () => AssertComparesEveryAssertionBearingColumn(body);
        act.Should().Throw<Exception>(
            "…so only the DROPPED column rejects it. A shape like this leaves a check row citing a value "
            + "the row no longer holds, with the headline verdict unchanged — exactly #470's terminal state")
            .WithMessage($"*{nameof(ComplianceCheck.ActualValue)}*");
    }

    [Theory]
    // The substring collisions: a future column whose name is a PREFIX of one already compared.
    [InlineData("Note", "check.Notes / before.Notes")]
    [InlineData("Actual", "check.ActualValue / before.ActualValue")]
    // The wrong-operand collisions: identifiers already in the method that are not check ROWS.
    [InlineData("Status", "fresh.Status / applied.Status, which are EvaluationOutcomes")]
    [InlineData("Count", "NewChecks.Count")]
    [InlineData("Ordinal", "StringComparison.Ordinal")]
    public void The_census_is_not_satisfied_by_a_token_that_is_not_the_column(string hypotheticalColumn, string collidesWith)
    {
        // Driven against the REAL, CURRENT body: each of these names would be satisfied for free by
        // something already in it under a bare `Contain($".{column}")`, so a column added to
        // ComplianceCheck under any of these names would pass the census while being compared nowhere.
        // The two rules that reject them are the identifier boundary and the operand scoping.
        var body = ReadOutcomeMatchesBody();

        var act = () => AssertColumnIsCompared(body, hypotheticalColumn);
        act.Should().Throw<Exception>(
            $"a ComplianceCheck.{hypotheticalColumn} column is compared NOWHERE, and must not be excused "
            + $"by {collidesWith}");
    }
}
