using CompliDrop.Api.Tests.TestHelpers;
using FluentAssertions;

namespace CompliDrop.Api.Tests;

/// <summary>
/// Hermetic self-tests for <see cref="SourceScan"/>, the plumbing every source-scanning gate now shares.
/// An enforcement gate is only as good as its stripper, its locator, its body extractor and its counter: a
/// stripper that dropped everything would make every <c>NotContain</c> assertion pass on any source at
/// all, a locator that silently found the wrong tree would scan nothing, an extractor that returned
/// <c>""</c> instead of throwing would empty every gate that reads a method body, and a counter that
/// mis-advanced would report the wrong number of call sites. These used to live inside one gate and cover
/// only it.
/// </summary>
public class SourceScanTests
{
    /// <summary>
    /// A signature belonging to no production method. The extractor's fixtures are about the EXTRACTOR, so
    /// they must not borrow a real gate's anchor — that coupling is what put these cases in
    /// <see cref="Adr0050EnforcementTests"/> in the first place.
    /// </summary>
    private const string TargetSignature = "private static async Task<IResult> Target(";

    [Fact]
    public void The_comment_stripper_removes_prose_without_touching_code()
    {
        SourceScan.StripLineComments("    // SetProperty(d => d.ExtractionTrust, x)\n    var a = 1;")
            .Should().NotContain("ExtractionTrust").And.Contain("var a = 1;");
        SourceScan.StripLineComments("    /// <summary>SaveChangesAsync()</summary>\n    var b = 2;")
            .Should().NotContain("SaveChangesAsync").And.Contain("var b = 2;");
        SourceScan.StripLineComments("    .SetProperty(d => d.ExtractionTrust, x) // why\n")
            .Should().Contain("SetProperty(d => d.ExtractionTrust",
                "a trailing comment must not smuggle the statement past a gate");
    }

    [Fact]
    public void The_locator_finds_the_real_production_tree_and_fails_closed_otherwise()
    {
        var root = SourceScan.ProductionRoot();
        root.Name.Should().Be("CompliDrop.Api");
        File.Exists(SourceScan.ProductionFile("Endpoints", "DocumentEndpoints.cs")).Should().BeTrue();

        var missing = () => SourceScan.ProductionFile("Endpoints", "NoSuchEndpoints.cs");
        missing.Should().Throw<FileNotFoundException>(
            "a renamed or moved file must fail the gate loudly, not leave it reading nothing");
    }

    [Fact]
    public void The_body_extractor_finds_the_right_span_and_ignores_commented_out_calls()
    {
        const string fixture = """
            class C
            {
                private static void Other() { SaveChangesAsync(); }

                private static async Task<IResult> Target(Guid id)
                {
                    // A comment naming SaveChangesAsync( must not count as the call.
                    var n = await db.Documents.Where(d => d.Id == id).ExecuteUpdateAsync(s => s);
                    if (n == 0) { return await db.Documents.AnyAsync(d => d.Id == id) ? Busy() : NotFound(); }
                    return Ok();
                }

                private static void After() { FirstOrDefaultAsync(); }
            }
            """;

        var body = SourceScan.ExtractMethodBody(fixture, TargetSignature);

        SourceScan.Count(body, "ExecuteUpdateAsync(").Should().Be(1);
        SourceScan.Count(body, "AnyAsync(").Should().Be(1);
        SourceScan.Count(body, "SaveChangesAsync(").Should().Be(0, "the only occurrence inside the body is a comment");
        SourceScan.Count(body, "FirstOrDefaultAsync(").Should().Be(0,
            "the sibling methods around the target must be outside the extracted span");
    }

    [Fact]
    public void The_body_extractor_fails_closed_when_the_method_is_missing_or_unbalanced()
    {
        // Both failure modes THROW rather than returning "" — the difference between a gate that is
        // enforcing and one that has quietly stopped.
        var act = () => SourceScan.ExtractMethodBody("class C { }", TargetSignature);
        act.Should().Throw<InvalidOperationException>().WithMessage("*not found*");

        var unbalanced = () => SourceScan.ExtractMethodBody(
            TargetSignature + "Guid id)\n{\n    var x = 1;\n", TargetSignature);
        unbalanced.Should().Throw<InvalidOperationException>().WithMessage("*Unbalanced*");
    }

    [Fact]
    public void The_counter_counts_non_overlapping_occurrences_only()
    {
        // The gates spend this on call SHAPES — "exactly one .EvaluateAsync(", "zero SaveChangesAsync(" —
        // so both directions matter: a counter that re-scanned from inside a match would report two call
        // sites where there is one (a gate failing on correct code), and one that never advanced past the
        // first hit would report one where there are two (a gate passing on a second, unguarded caller).
        SourceScan.Count("a.Foo( b.Foo( c", ".Foo(").Should().Be(2);
        SourceScan.Count("aaaa", "aa").Should().Be(2, "a match consumes its own length and is not re-counted from inside itself");
        SourceScan.Count("nothing here", ".Foo(").Should().Be(0);
    }
}
