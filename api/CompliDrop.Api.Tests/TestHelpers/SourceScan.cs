namespace CompliDrop.Api.Tests.TestHelpers;

/// <summary>
/// Shared plumbing for the source-scanning CI gates — <see cref="Adr0030EnforcementTests"/>,
/// <see cref="Adr0050EnforcementTests"/>, <see cref="Adr0052EnforcementTests"/> and their neighbours —
/// which pin reviewer-memory rules no runtime assertion can reach ("no <c>AT TIME ZONE</c> in raw SQL",
/// "the re-arm is one atomic UPDATE", "nothing else reads <c>ExtractionTrust</c>", "the pure re-grade has
/// one call site and it is inside the guard").
///
/// <para>
/// Extracted because the copies had started to multiply and one of them could DRIFT: two gates carried
/// byte-identical comment strippers while one of their doc comments asserted, unpinned, that it behaved
/// "exactly as" the other. A gate whose stripper quietly grows stricter stops enforcing and still passes
/// green. One implementation, one set of hermetic self-tests (<c>SourceScanTests</c>), every gate.
/// </para>
///
/// <para>
/// <see cref="ExtractMethodBody"/> and <see cref="Count"/> moved here for the same reason, one gate later
/// (#461 review): the ADR 0030 gate re-copied the counter verbatim and reached into the ADR 0050 TEST
/// CLASS for the extractor, which made one gate's plumbing a dependency of another gate's existence. The
/// rule is the one above — plumbing lives here, its self-tests live in <c>SourceScanTests</c>, and a gate
/// class holds only the invariant it enforces.
/// </para>
/// </summary>
internal static class SourceScan
{
    /// <summary>
    /// The production project root (<c>api/CompliDrop.Api</c>), found by walking up from the test
    /// assembly's output directory. Throws rather than returning a guess, so a moved/renamed tree fails
    /// the gate loudly instead of leaving it scanning nothing.
    /// </summary>
    internal static DirectoryInfo ProductionRoot()
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

    /// <summary>Absolute path to one production source file, given its segments below
    /// <c>api/CompliDrop.Api</c>. Throws when it is absent — a renamed file must fail the gate.</summary>
    internal static string ProductionFile(params string[] relativeSegments)
    {
        var path = Path.Combine([ProductionRoot().FullName, .. relativeSegments]);
        if (!File.Exists(path))
            throw new FileNotFoundException(
                $"Could not locate api/CompliDrop.Api/{string.Join('/', relativeSegments)} from {AppContext.BaseDirectory}");
        return path;
    }

    /// <summary>
    /// Drops whole-line <c>//</c> comments (and the <c>///</c> doc-comment lines that are a special case
    /// of them), so PROSE ABOUT a forbidden call cannot read as the call itself — the gates' own comments
    /// deliberately name the shapes they reject. Not a real parser: the only false negative it can produce
    /// is a trailing comment on a code line, which cannot hide a statement.
    /// </summary>
    internal static string StripLineComments(string source) =>
        string.Join('\n', source.Split('\n')
            .Where(line => !line.TrimStart().StartsWith("//", StringComparison.Ordinal)));

    /// <summary>
    /// Returns the source text between the braces of the method whose declaration contains
    /// <paramref name="signature"/>, with whole-line comments stripped (a gate's own doc comment names the
    /// shapes it rejects — "an <c>if</c> above a SaveChanges" — and prose about a forbidden call must not
    /// read as the call itself). Throws when the signature is absent or its braces never balance, so a
    /// renamed method or a botched read fails the gate instead of silently emptying it.
    /// <para/>
    /// The stripping happens BEFORE the anchor lookup, not only on the result (#390 review): a gate's
    /// anchor is itself a code shape, and the prose around it routinely quotes that shape — a comment
    /// containing <c>app.MapGet("/health/ready"</c> would otherwise re-anchor the scan onto the prose and
    /// leave the gate inspecting whatever braces happened to follow. It also means a signature that exists
    /// ONLY in a comment throws "not found" rather than matching.
    /// <para/>
    /// Brace-matching, not a parser: it spans from the first <c>{</c> after the signature to its match, so
    /// a target method must be BLOCK-bodied. Against an expression body the first brace it finds belongs
    /// to whatever object or lambda the expression opens with, and the gate would then inspect that
    /// instead — which is why <c>ComplianceEndpoints.RunCheck</c> is deliberately block-bodied
    /// (ADR 0030 Amendment 3).
    /// </summary>
    internal static string ExtractMethodBody(string source, string signature)
    {
        var code = StripLineComments(source);
        var start = code.IndexOf(signature, StringComparison.Ordinal);
        if (start < 0)
            throw new InvalidOperationException($"Method signature not found: {signature}");

        var open = code.IndexOf('{', start);
        if (open < 0)
            throw new InvalidOperationException($"No opening brace after: {signature}");

        var depth = 0;
        for (var i = open; i < code.Length; i++)
        {
            if (code[i] == '{') depth++;
            else if (code[i] == '}' && --depth == 0)
                return code[(open + 1)..i];
        }
        throw new InvalidOperationException($"Unbalanced braces in the body of: {signature}");
    }

    /// <summary>
    /// Non-overlapping occurrences of <paramref name="needle"/> in <paramref name="haystack"/>, ordinal.
    /// The gates count CALL SHAPES ("exactly one <c>ExecuteUpdateAsync</c>", "exactly one
    /// <c>.EvaluateAsync(</c>"), so a match consumes its own length and cannot be re-counted from inside
    /// itself.
    /// </summary>
    internal static int Count(string haystack, string needle)
    {
        var (count, at) = (0, 0);
        while ((at = haystack.IndexOf(needle, at, StringComparison.Ordinal)) >= 0)
        {
            count++;
            at += needle.Length;
        }
        return count;
    }
}
