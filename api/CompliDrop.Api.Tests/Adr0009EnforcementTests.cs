using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using FluentAssertions;
using Xunit;

namespace CompliDrop.Api.Tests;

/// <summary>
/// Mechanical CI gate for <see href="https://github.com/neboxdev/complidrop/blob/main/docs/adr/0009-no-at-time-zone-on-timestamptz-in-raw-sql.md">ADR 0009</see>
/// — the "no <c>AT TIME ZONE</c> on timestamptz in raw SQL" rule.
///
/// Replaces reviewer-memory enforcement (the rule lives in
/// <c>CLAUDE.md</c> + ADR 0009; both are point-in-time documents not
/// re-run on every PR) with a build-breaking test. Mirrors the pattern
/// in <see cref="ExtractionWorkerTests"/> that pins
/// <c>ExtractionWorker.ClaimSql</c> by name — this test generalises
/// that pin to every <c>.cs</c> file under <c>api/CompliDrop.Api/</c>.
///
/// Scope (per ADR 0009):
///   - BackgroundServices/*.cs raw SQL
///   - Migrations/*.cs (the <c>migrationBuilder.Sql(...)</c> surface)
///   - Anywhere else in production code that builds <c>ExecuteSqlRaw</c>
///     / <c>ExecuteSqlInterpolated</c> string literals
///
/// Out of scope: test code (deliberately exercises <c>SET TIME ZONE</c>
/// against non-UTC sessions — see ExtractionWorkerTests's
/// <c>Claim_under_non_UTC_session_*</c> theories).
///
/// Allow-list entries name the file + the clause-3 ADR-0009 reason
/// they're exempt so a future maintainer can audit the allow-list
/// itself, not just the violations it covers.
/// </summary>
public class Adr0009EnforcementTests
{
    /// <summary>
    /// Files exempted from the "no AT TIME ZONE on timestamptz in raw
    /// SQL" rule because they fall under clause 3 (output-only
    /// conversion to <c>date</c> / wall-clock display, where the
    /// <c>AT TIME ZONE</c> output is NOT assigned to or compared
    /// against a timestamptz column).
    ///
    /// Each entry pairs a normalized relative path (forward slashes)
    /// with a one-line rationale. A new clause-3 site should be added
    /// here at the time the new code lands, and the rationale should
    /// name which ADR clause makes it legitimate.
    /// </summary>
    private static readonly Dictionary<string, string> ClauseThreeAllowList = new()
    {
        // The Reminder.SendDate backfill migration derives a `date` value from a
        // `timestamptz` for output (ADR 0007's org-local calendar-day semantic) —
        // covered by ADR 0009 clause 3.
        ["Migrations/20260525101534_BackfillReminderLogSendDateToOrgLocal.cs"] =
            "ADR 0009 clause 3 — output-only conversion to date for ADR 0007 org-local SendDate backfill",
    };

    /// <summary>
    /// Resolve the repository's <c>api/CompliDrop.Api/</c> directory.
    /// The test assembly lives at
    /// <c>api/CompliDrop.Api.Tests/bin/&lt;config&gt;/net10.0/</c> by
    /// default; climb up looking for the sibling project directory.
    /// </summary>
    private static string FindProductionRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 8 && dir != null; i++, dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "api", "CompliDrop.Api");
            if (Directory.Exists(candidate))
            {
                return candidate;
            }
        }
        throw new DirectoryNotFoundException(
            $"Could not locate api/CompliDrop.Api/ from {AppContext.BaseDirectory}");
    }

    /// <summary>
    /// Normalize a relative path to forward slashes so the allow-list
    /// keys match identically on Windows and Linux CI.
    /// </summary>
    private static string Normalize(string relativePath) =>
        relativePath.Replace('\\', '/');

    /// <summary>
    /// Identify lines that are purely comment content (single-line
    /// <c>//</c>, XML doc <c>///</c>, or block-style <c>*</c>
    /// continuation). Does NOT attempt to handle the rare end-of-line
    /// comment case (e.g. <c>cmd.CommandText = "...AT TIME ZONE..."; // note</c>)
    /// — that line still contains the offending SQL and should fail.
    /// </summary>
    private static bool IsCommentLine(string line)
    {
        var trimmed = line.TrimStart();
        return trimmed.StartsWith("//", StringComparison.Ordinal)
            || trimmed.StartsWith("*", StringComparison.Ordinal);
    }

    [Fact]
    public void No_raw_SQL_in_production_code_uses_AT_TIME_ZONE_outside_the_clause_three_allow_list()
    {
        var productionRoot = FindProductionRoot();
        var violations = new List<string>();

        foreach (var file in Directory.EnumerateFiles(
                     productionRoot, "*.cs", SearchOption.AllDirectories))
        {
            // Skip obj/ and bin/ generated artifacts.
            var relative = Normalize(Path.GetRelativePath(productionRoot, file));
            if (relative.StartsWith("obj/", StringComparison.OrdinalIgnoreCase) ||
                relative.StartsWith("bin/", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            // Case-insensitive substring match — Postgres accepts
            // `AT TIME ZONE` in any case, so does the rule. The scan
            // deliberately ignores single-line / XML-doc / block-style
            // COMMENT lines: a comment mentioning `AT TIME ZONE` to
            // EXPLAIN the rule (e.g. the doc-comment on
            // ExtractionWorker.ClaimSql) is exactly the kind of
            // counter-example we WANT future maintainers to read.
            // The rule's intent is to forbid the SQL itself, not the
            // word.
            var lines = File.ReadAllLines(file);
            for (var i = 0; i < lines.Length; i++)
            {
                if (lines[i].IndexOf(
                        "AT TIME ZONE",
                        StringComparison.OrdinalIgnoreCase) < 0)
                {
                    continue;
                }

                if (IsCommentLine(lines[i]))
                {
                    continue;
                }

                if (ClauseThreeAllowList.ContainsKey(relative))
                {
                    // Allow-listed — clause 3 legitimate.
                    break;
                }

                violations.Add($"  {relative}:{i + 1}: {lines[i].Trim()}");
                // First hit per file is enough; the message lists the
                // file so the contributor can grep the rest themselves.
                break;
            }
        }

        violations.Should().BeEmpty(
            "ADR 0009 forbids `AT TIME ZONE` on a timestamptz expression " +
            "whose result feeds back into a timestamptz comparison or " +
            "assignment. Output-only conversion to `date` / wall-clock " +
            "display (clause 3) stays legitimate — add the file to " +
            "ClauseThreeAllowList with the clause reason if you have a " +
            "new such case. See docs/adr/0009-no-at-time-zone-on-" +
            "timestamptz-in-raw-sql.md.\n\nOffenders:\n" +
            string.Join("\n", violations));
    }

    [Fact]
    public void Clause_three_allow_list_entries_exist()
    {
        // Belt-and-suspenders: if a future contributor accidentally
        // deletes the clause-3 migration that the allow-list points
        // at (e.g. during a squash-migrations pass), the absence of
        // the file should surface as a test failure — not silently
        // shrink the allow-list to zero entries.
        var productionRoot = FindProductionRoot();
        foreach (var (relative, reason) in ClauseThreeAllowList)
        {
            var absolute = Path.Combine(
                productionRoot,
                relative.Replace('/', Path.DirectorySeparatorChar));
            File.Exists(absolute).Should().BeTrue(
                $"allow-list entry '{relative}' (reason: {reason}) " +
                "should point at a real file. If the file was " +
                "intentionally removed (e.g. migrations squashed), " +
                "drop the corresponding allow-list entry.");
        }
    }
}
