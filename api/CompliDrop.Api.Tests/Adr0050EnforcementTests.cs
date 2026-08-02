using CompliDrop.Api.Tests.TestHelpers;
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
///
/// <para>
/// The pin is on the guard's LOCATION, not merely on call counts — counts alone fail OPEN. Option A
/// re-spelled with an <c>AnyAsync</c> pre-check (instead of a <c>FirstOrDefaultAsync</c> load) presents
/// exactly the production method's counts: one <c>ExecuteUpdateAsync</c>, one <c>AnyAsync</c>, none of
/// the forbidden loads — while the race is fully back. So the staleness predicate must be found INSIDE
/// the <c>WHERE</c> feeding the update, and the single existence read must come AFTER it. Two hermetic
/// fixtures prove the gate REJECTS each of those shapes, which is the only way to know an enforcement
/// test still enforces.
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
                return SourceScan.StripLineComments(source[(open + 1)..i]);
        }
        throw new InvalidOperationException($"Unbalanced braces in the body of: {signature}");
    }

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
    /// The whole gate, so the hermetic fixtures below can drive the SAME assertions the production check
    /// runs. Call counts alone are not enough: ADR 0050 Option A can be re-spelled with
    /// <c>AnyAsync</c> as its pre-check, which keeps every count identical (one
    /// <c>ExecuteUpdateAsync</c>, one <c>AnyAsync</c>, zero forbidden loads) while the guard is once
    /// again a read-then-write. So the guard's LOCATION is pinned too — the staleness predicate must sit
    /// inside the UPDATE's own <c>WHERE</c>, and the single existence read must come AFTER the update,
    /// never before it.
    /// </summary>
    internal static void AssertAtomicReArmShape(string body)
    {
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

        var updateAt = body.IndexOf("ExecuteUpdateAsync(", StringComparison.Ordinal);
        var anyAt = body.IndexOf("AnyAsync(", StringComparison.Ordinal);
        anyAt.Should().BeGreaterThan(updateAt,
            "the one AnyAsync must be the REFUSAL path's existence re-read, which runs after the update — "
            + "an AnyAsync BEFORE it is a pre-check, i.e. ADR 0050 Option A's read-then-write wearing the "
            + "call counts of the atomic shape: the worker can flip Pending → Processing between that "
            + "SELECT and the UPDATE, so the guard would be exactly as racy as the bug it guards");

        // The guard's LOCATION. A WHERE that has lost the staleness predicate is an UNGUARDED update no
        // matter what ran above it, and the call counts cannot see the difference.
        var whereAt = body[..updateAt].LastIndexOf(".Where(", StringComparison.Ordinal);
        whereAt.Should().BeGreaterOrEqualTo(0,
            "the re-arm UPDATE must be filtered by a .Where( — an unfiltered ExecuteUpdateAsync would "
            + "re-arm every document in the table");
        var updateFilter = body[whereAt..updateAt];
        updateFilter.Should().Contain("ExtractionStatus.Processing",
            "the staleness guard lives INSIDE the UPDATE's WHERE (ADR 0050 §1) — a WHERE that only "
            + "matches the id means the guard was hoisted into a separate read-then-write");
        updateFilter.Should().Contain("ExtractionClaims.ZombieTimeout",
            "…and it compares against the WORKER'S OWN staleness threshold, from the one shared constant "
            + "both layers read (ADR 0050 §2) — a hand-rolled interval here lets the endpoint and the "
            + "worker disagree about which claims are live");

        foreach (var readThenWrite in new[]
                 {
                     "SaveChangesAsync(", "SaveChanges(", "FirstOrDefaultAsync(", "SingleOrDefaultAsync(",
                     "FirstAsync(", "SingleAsync(", "FindAsync(", "ToListAsync(", "CountAsync(",
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
    public void Reextract_re_arms_with_one_conditional_ExecuteUpdateAsync_and_one_existence_read()
    {
        AssertAtomicReArmShape(ExtractMethodBody(
            File.ReadAllText(SourceScan.ProductionFile("Endpoints", "DocumentEndpoints.cs")),
            ReextractSignature));
    }

    [Fact]
    public void The_pin_rejects_a_read_then_write_guard_spelled_with_AnyAsync()
    {
        // The shape that used to slip through, and the reason the location assertions exist. Every count
        // this gate checked before is IDENTICAL to the production method's — one ExecuteUpdateAsync, one
        // AnyAsync, zero forbidden loads — yet ADR 0050 Option A's race is fully back: the status is read
        // in one statement and written in another, with the worker free to claim the row in between.
        const string readThenWrite = """
            class C
            {
                private static async Task<IResult> Reextract(Guid id)
                {
                    // Option A, re-spelled so the CALL COUNTS still line up exactly.
                    if (await db.Documents.AnyAsync(d => d.Id == id
                            && d.ExtractionStatus == ExtractionStatus.Processing
                            && d.ProcessingStartedAt > DateTime.UtcNow - ExtractionClaims.ZombieTimeout, ct))
                    {
                        return Error(409, "document.extraction_in_progress", "We're still reading this document.");
                    }

                    var rearmed = await db.Documents
                        .Where(d => d.Id == id)
                        .ExecuteUpdateAsync(set => set
                            .SetProperty(d => d.ExtractionStatus, ExtractionStatus.Pending)
                            .SetProperty(d => d.ProcessingStartedAt, (DateTime?)null)
                            .SetProperty(d => d.ProcessingError, (string?)null)
                            .SetProperty(d => d.ProcessingAttempts, 0)
                            .SetProperty(d => d.FailedAttempts, 0)
                            .SetProperty(d => d.UpdatedAt, DateTime.UtcNow),
                            ct);

                    if (rearmed == 0) return NotFound();

                    await audit.LogAsync("document.reextract_queued", nameof(Document), id);
                    return Ok();
                }
            }
            """;

        var body = ExtractMethodBody(readThenWrite, ReextractSignature);
        // Proven, not assumed: the fixture clears the anti-no-op floor and every count the old gate
        // checked, so the rejection below can only come from the new location assertions.
        body.Split('\n').Length.Should().BeGreaterOrEqualTo(MinBodyLines);
        Count(body, "ExecuteUpdateAsync(").Should().Be(1);
        Count(body, "AnyAsync(").Should().Be(1);

        var act = () => AssertAtomicReArmShape(body);
        act.Should().Throw<Exception>("this shape re-opens the race the gate exists to prevent")
            .WithMessage("*existence re-read*");
    }

    [Fact]
    public void The_pin_rejects_an_update_whose_WHERE_lost_the_staleness_predicate()
    {
        // The other new assertion, isolated: correct ORDER (the existence read follows the update), but
        // the UPDATE matches on the id alone — an unguarded re-arm that reports 409 for a row it just
        // overwrote. Counts and ordering both pass; only the WHERE-contents pin catches it.
        const string unguarded = """
            class C
            {
                private static async Task<IResult> Reextract(Guid id)
                {
                    var rearmed = await db.Documents
                        .Where(d => d.Id == id)
                        .ExecuteUpdateAsync(set => set
                            .SetProperty(d => d.ExtractionStatus, ExtractionStatus.Pending)
                            .SetProperty(d => d.ProcessingStartedAt, (DateTime?)null)
                            .SetProperty(d => d.ProcessingError, (string?)null)
                            .SetProperty(d => d.ProcessingAttempts, 0)
                            .SetProperty(d => d.FailedAttempts, 0)
                            .SetProperty(d => d.UpdatedAt, DateTime.UtcNow),
                            ct);

                    if (rearmed == 0)
                    {
                        return await db.Documents.AnyAsync(d => d.Id == id, ct)
                            ? Error(409, "document.extraction_in_progress", "We're still reading this document.")
                            : NotFound();
                    }

                    await audit.LogAsync("document.reextract_queued", nameof(Document), id);
                    return Ok();
                }
            }
            """;

        var body = ExtractMethodBody(unguarded, ReextractSignature);
        body.Split('\n').Length.Should().BeGreaterOrEqualTo(MinBodyLines);
        body.IndexOf("AnyAsync(", StringComparison.Ordinal).Should()
            .BeGreaterThan(body.IndexOf("ExecuteUpdateAsync(", StringComparison.Ordinal),
                "the ordering assertion must PASS here, so the rejection is the WHERE-contents pin alone");

        var act = () => AssertAtomicReArmShape(body);
        act.Should().Throw<Exception>("an UPDATE matching only on the id carries no guard at all")
            .WithMessage("*staleness guard lives INSIDE*");
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
        File.ReadAllText(SourceScan.ProductionFile("Endpoints", "DocumentEndpoints.cs"))
            .Should().Contain(ReextractSignature,
                "the ADR 0050 gate anchors on this exact declaration; update the constant with the rename");
    }
}
