using System.Text.RegularExpressions;
using FluentAssertions;

namespace CompliDrop.Api.Tests;

/// <summary>
/// Meta-test: pins the load-bearing invariants of the backend <c>dotnet format</c>
/// cleanup gate config (the repo-root <c>.editorconfig</c>), added with #42 / epic #41.
///
/// Why: the CI gate <c>dotnet format --verify-no-changes</c> enforces the tree as it is
/// TODAY, but its value depends on <c>.editorconfig</c> invariants a careless edit could
/// silently weaken — dropping <c>dotnet_diagnostic.IDE0005.severity = warning</c> (so
/// unused usings stop being stripped), or broadening the generated-code exclusion back to
/// <c>[**/Migrations/*.cs]</c> (which would silently drop the test project's hand-written
/// migration TESTS out of the gate). Mirrors the config-pin pattern of
/// <see cref="Adr0009EnforcementTests"/>.
/// </summary>
public class CleanupGateConfigTests
{
    private static string ReadRepoEditorConfig()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 8 && dir != null; i++, dir = dir.Parent)
        {
            // The repo root is the directory that holds BOTH api/CompliDrop.Api and the
            // root .editorconfig that drives the format gate.
            var editorConfig = Path.Combine(dir.FullName, ".editorconfig");
            if (File.Exists(editorConfig) &&
                Directory.Exists(Path.Combine(dir.FullName, "api", "CompliDrop.Api")))
            {
                return File.ReadAllText(editorConfig);
            }
        }

        throw new FileNotFoundException(
            $"Could not locate the repo-root .editorconfig from {AppContext.BaseDirectory}");
    }

    [Fact]
    public void EditorConfig_enforces_unused_using_removal()
    {
        // IDE0005 at warning is what makes `dotnet format` strip unnecessary usings — the
        // headline mechanical win of the backend gate.
        ReadRepoEditorConfig()
            .Should().Contain("dotnet_diagnostic.IDE0005.severity = warning");
    }

    [Fact]
    public void Generated_code_exclusion_is_anchored_to_the_api_migrations_only()
    {
        var text = ReadRepoEditorConfig();

        // Every editorconfig section header ([...]) that scopes a Migrations folder must be
        // EXACTLY the API project's generated-migrations glob — never a broader shape (e.g.
        // [**/Migrations/*.cs], [api/**/Migrations/*.cs], [**/Migrations/**/*.cs]) that would
        // also re-swallow the test project's hand-written
        // api/CompliDrop.Api.Tests/Migrations/*Tests.cs out of the format gate.
        var migrationSections = Regex
            .Matches(text, @"^\[[^\]]*Migrations[^\]]*\]", RegexOptions.Multiline)
            .Select(m => m.Value)
            .ToList();

        migrationSections.Should().Equal("[api/CompliDrop.Api/Migrations/*.cs]");
    }
}
