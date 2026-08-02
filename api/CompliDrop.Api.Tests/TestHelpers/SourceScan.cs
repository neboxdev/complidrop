namespace CompliDrop.Api.Tests.TestHelpers;

/// <summary>
/// Shared plumbing for the source-scanning CI gates — <see cref="Adr0050EnforcementTests"/>,
/// <see cref="Adr0052EnforcementTests"/> and their neighbours — which pin reviewer-memory rules no
/// runtime assertion can reach ("no <c>AT TIME ZONE</c> in raw SQL", "the re-arm is one atomic
/// UPDATE", "nothing else reads <c>ExtractionTrust</c>").
///
/// <para>
/// Extracted because the copies had started to multiply and one of them could DRIFT: two gates carried
/// byte-identical comment strippers while one of their doc comments asserted, unpinned, that it behaved
/// "exactly as" the other. A gate whose stripper quietly grows stricter stops enforcing and still passes
/// green. One implementation, one set of hermetic self-tests (<c>SourceScanTests</c>), every gate.
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
}
