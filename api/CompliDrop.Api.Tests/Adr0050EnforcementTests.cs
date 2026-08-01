using FluentAssertions;

namespace CompliDrop.Api.Tests;

/// <summary>
/// Mechanical CI gate for <see href="https://github.com/neboxdev/complidrop/blob/main/docs/adr/0050-reextract-refuses-a-live-extraction-claim.md">ADR 0050</see>
/// § Decision 1 — <c>DocumentEndpoints.Reextract</c> re-arms with ONE conditional
/// <c>ExecuteUpdateAsync</c>, never a read-then-write.
///
/// <para>
/// The ATOMICITY is the fix, not the condition: an <c>if (doc.ExtractionStatus == Processing) return
/// Error(409, …);</c> above a tracked-entity <c>SaveChangesAsync</c> tests a status the worker can flip
/// between the <c>SELECT</c> and the <c>UPDATE</c> — exactly as racy as the double-OCR/duplicate-field bug
/// it guards (ADR 0050 Option A, rejected). But no BEHAVIOURAL test can tell the two shapes apart: every
/// re-extract test is single-threaded (seed a state, POST once, assert), so the rejected shape returns the
/// same 409, leaves the same untouched row and writes the same absent audit row. "Simplify it back to a
/// tracked-entity save with an if" would therefore ship green while silently re-opening the race.
/// </para>
///
/// <para>
/// So the invariant is pinned MECHANICALLY, the way this repo already pins reviewer-memory rules that no
/// runtime assertion can reach — <see cref="Adr0009EnforcementTests"/> ("no <c>AT TIME ZONE</c> in raw
/// SQL") and <c>ExportDisclaimerTests</c> both scan production source. Same anti-no-op discipline as
/// those: the method must be FOUND and non-trivial, or the gate fails closed rather than passing on an
/// empty read.
/// </para>
/// </summary>
public class Adr0050EnforcementTests
{
    /// <summary>The signature the pin anchors on, spelled exactly as the endpoint declares it.</summary>
    private const string ReextractSignature = "private static async Task<IResult> Reextract(";

    /// <summary>
    /// Lowest plausible size for the extracted method body. <c>Reextract</c> is ~40 lines of guard comment
    /// plus the statement; a body materially smaller than this means the extractor latched onto the wrong
    /// span (a signature match inside a comment, a brace-counting bug) and every "contains no
    /// <c>SaveChangesAsync</c>" assertion below would pass vacuously.
    /// </summary>
    private const int MinBodyLines = 20;

    private static string ProductionFile(params string[] relativeSegments)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 8 && dir != null; i++, dir = dir.Parent)
        {
            var candidate = Path.Combine([dir.FullName, "api", "CompliDrop.Api", .. relativeSegments]);
            if (File.Exists(candidate)) return candidate;
        }
        throw new FileNotFoundException(
            $"Could not locate api/CompliDrop.Api/{string.Join('/', relativeSegments)} from {AppContext.BaseDirectory}");
    }

    /// <summary>
    /// Returns the source text between the braces of the method whose declaration contains
    /// <paramref name="signature"/>, with whole-line comments stripped (the guard's own doc comment names
    /// the shapes it rejects — "an <c>if</c> above a SaveChanges" — and prose about a forbidden call must
    /// not read as the call itself). Throws when the signature is absent or its braces never balance, so a
    /// renamed method or a botched read fails the gate instead of silently emptying it.
    /// <para/>
    /// Internal so the hermetic fixtures below can drive it against synthetic input.
    /// </summary>
    internal static string ExtractMethodBody(string source, string signature)
    {
        var start = source.IndexOf(signature, StringComparison.Ordinal);
        if (start < 0)
            throw new InvalidOperationException($"Method signature not found: {signature}");

        var open = source.IndexOf('{', start);
        if (open < 0)
            throw new InvalidOperationException($"No opening brace after: {signature}");

        var depth = 0;
        for (var i = open; i < source.Length; i++)
        {
            if (source[i] == '{') depth++;
            else if (source[i] == '}' && --depth == 0)
                return StripCommentLines(source[(open + 1)..i]);
        }
        throw new InvalidOperationException($"Unbalanced braces in the body of: {signature}");
    }

    private static string StripCommentLines(string body) =>
        string.Join('\n', body
            .Split('\n')
            .Where(line => !line.TrimStart().StartsWith("//", StringComparison.Ordinal)));

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

    [Fact]
    public void Reextract_re_arms_with_one_conditional_ExecuteUpdateAsync_and_one_existence_read()
    {
        var body = ExtractMethodBody(
            File.ReadAllText(ProductionFile("Endpoints", "DocumentEndpoints.cs")),
            ReextractSignature);

        // Anti-no-op floor: the gate must have actually read a real method body. Without this, a renamed
        // method or a brace-counting regression would return "" and every assertion below would pass.
        body.Split('\n').Length.Should().BeGreaterOrEqualTo(MinBodyLines,
            $"the extracted Reextract body is implausibly short ({body.Length} chars) — the extractor "
            + "latched onto the wrong span and this gate would be a silent no-op");

        Count(body, "ExecuteUpdateAsync(").Should().Be(1,
            "ADR 0050 §1: the re-arm is ONE conditional bulk UPDATE whose WHERE carries the guard, so "
            + "Postgres re-evaluates the predicate under the row's own UPDATE lock");

        // The INTENDED shape, pinned precisely rather than forbidding all reads: zero rows is ambiguous
        // on its own ("no such document" vs "mine, but busy"), so the refusal path re-reads EXISTENCE —
        // one AnyAsync, deliberately AFTER the update, never a load feeding the write (ADR 0050 §4).
        Count(body, "AnyAsync(").Should().Be(1,
            "the refusal path answers 404-vs-409 with exactly one existence re-read");

        foreach (var readThenWrite in new[]
                 {
                     "SaveChangesAsync(", "FirstOrDefaultAsync(", "SingleOrDefaultAsync(",
                     "FirstAsync(", "SingleAsync(", "FindAsync(",
                 })
        {
            Count(body, readThenWrite).Should().Be(0,
                $"'{readThenWrite}' in Reextract means the guard became a read-then-write: it would test a "
                + "status the worker can flip between the SELECT and the UPDATE, i.e. exactly as racy as "
                + "the double-OCR / duplicate-DocumentField bug #365 closed (ADR 0050 Option A). No "
                + "single-threaded test can see that regression, which is why this pin exists");
        }
    }

    [Fact]
    public void The_body_extractor_finds_the_right_span_and_ignores_commented_out_calls()
    {
        const string fixture = """
            class C
            {
                private static void Other() { SaveChangesAsync(); }

                private static async Task<IResult> Reextract(Guid id)
                {
                    // A comment naming SaveChangesAsync( must not count as the call.
                    var n = await db.Documents.Where(d => d.Id == id).ExecuteUpdateAsync(s => s);
                    if (n == 0) { return await db.Documents.AnyAsync(d => d.Id == id) ? Busy() : NotFound(); }
                    return Ok();
                }

                private static void After() { FirstOrDefaultAsync(); }
            }
            """;

        var body = ExtractMethodBody(fixture, ReextractSignature);

        Count(body, "ExecuteUpdateAsync(").Should().Be(1);
        Count(body, "AnyAsync(").Should().Be(1);
        Count(body, "SaveChangesAsync(").Should().Be(0, "the only occurrence inside the body is a comment");
        Count(body, "FirstOrDefaultAsync(").Should().Be(0,
            "the sibling methods around the target must be outside the extracted span");
    }

    [Fact]
    public void The_body_extractor_fails_closed_when_the_method_is_missing_or_unbalanced()
    {
        // Both failure modes THROW rather than returning "" — the difference between a gate that is
        // enforcing and one that has quietly stopped.
        var act = () => ExtractMethodBody("class C { }", ReextractSignature);
        act.Should().Throw<InvalidOperationException>().WithMessage("*not found*");

        var unbalanced = () => ExtractMethodBody(
            ReextractSignature + "Guid id)\n{\n    var x = 1;\n", ReextractSignature);
        unbalanced.Should().Throw<InvalidOperationException>().WithMessage("*Unbalanced*");
    }

    [Fact]
    public void The_pinned_signature_is_the_one_the_endpoint_actually_declares()
    {
        // If Reextract is ever renamed or its signature reshaped, this fails LOUDLY here rather than
        // leaving the gate above throwing an opaque "signature not found" from a helper.
        File.ReadAllText(ProductionFile("Endpoints", "DocumentEndpoints.cs"))
            .Should().Contain(ReextractSignature,
                "the ADR 0050 gate anchors on this exact declaration; update the constant with the rename");
    }
}
