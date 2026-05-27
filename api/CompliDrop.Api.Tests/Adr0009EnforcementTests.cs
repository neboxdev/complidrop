using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
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
///
/// ## Self-test (#64 followup)
///
/// The scanner is itself test-covered so a future refactor that
/// silently no-ops the gate (e.g. `IsCommentLine` returning true for
/// every line, or `FindProductionRoot` resolving to an empty tree)
/// surfaces as a test failure. The hermetic <c>FindViolations</c>
/// pure-function path takes a synthetic root in <c>%TEMP%</c> and
/// exercises both directions: a known-bad fixture must surface as
/// a violation; a comment-only fixture must not. The
/// <c>IsCommentLine</c> classifier has its own [Theory] pinning the
/// contract directly. Together these prevent the gate from silently
/// regressing into a no-op.
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
    /// here at the time the new code lands, and the rationale MUST
    /// name which ADR clause makes it legitimate (the
    /// <see cref="Clause_three_allow_list_entries_cite_an_ADR_clause"/>
    /// test enforces a `clause N` or `ADR NNNN` substring).
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
    /// Lowest-plausible production-tree file count. A real
    /// <c>api/CompliDrop.Api/</c> tree contains dozens of .cs files
    /// (~80 at the time of #64). If the scanner sees fewer than this,
    /// <see cref="FindProductionRoot"/> almost certainly resolved to
    /// the wrong directory and the gate is silently a no-op.
    /// </summary>
    private const int MinExpectedProductionFiles = 50;

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
    /// <c>//</c>, XML doc <c>///</c>, block-comment opener <c>/*</c>,
    /// or block-comment continuation <c>*</c>). Does NOT attempt to
    /// handle the rare end-of-line comment case
    /// (e.g. <c>cmd.CommandText = "...AT TIME ZONE..."; // note</c>)
    /// — that line still contains the offending SQL and should fail.
    ///
    /// Internal so the companion theory test can pin the contract;
    /// a refactor that silently broke this helper would otherwise
    /// regress the entire gate to a no-op without any test failure.
    /// </summary>
    internal static bool IsCommentLine(string line)
    {
        var trimmed = line.TrimStart();
        return trimmed.StartsWith("//", StringComparison.Ordinal)
            || trimmed.StartsWith("/*", StringComparison.Ordinal)
            || trimmed.StartsWith("*", StringComparison.Ordinal);
    }

    /// <summary>
    /// Scan every <c>.cs</c> file under <paramref name="productionRoot"/>
    /// for non-comment lines containing <c>AT TIME ZONE</c>
    /// (case-insensitive) outside the supplied allow-list. Returns
    /// the formatted violation strings in deterministic order
    /// (<c>"{relative-path}:{line-number}: {trimmed-line}"</c>) and
    /// the count of files actually scanned (so callers can guard
    /// against a silent zero-file no-op).
    ///
    /// Reports <em>ALL</em> violations per file — not just the first.
    /// A multi-violation file (rare today since production is at
    /// zero-violations) lists every hit so the contributor can fix
    /// them in a single pass instead of running the gate N times for
    /// N violations in the same file. (<see href="https://github.com/neboxdev/complidrop/issues/142">#142</see>,
    /// <see href="https://github.com/neboxdev/complidrop/issues/64">#64</see> followup.)
    ///
    /// Pure relative to its inputs — takes the root + allow-list,
    /// reads from disk, returns the result. Internal so hermetic
    /// fixture tests can drive it against a synthetic <c>%TEMP%</c>
    /// root in <see cref="Scanner_flags_a_known_violation_under_a_temp_root"/>
    /// and <see cref="Scanner_skips_a_comment_only_line_under_a_temp_root"/>.
    /// </summary>
    internal static (IReadOnlyList<string> Violations, int ScannedCount) FindViolations(
        string productionRoot,
        IReadOnlyDictionary<string, string> allowList)
    {
        var violations = new List<string>();
        var scanned = 0;
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

            scanned++;

            // Case-insensitive substring match — Postgres accepts
            // `AT TIME ZONE` in any case, so does the rule. The scan
            // deliberately ignores comment lines: a comment mentioning
            // `AT TIME ZONE` to EXPLAIN the rule (e.g. the doc-comment
            // on ExtractionWorker.ClaimSql) is exactly the kind of
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

                if (allowList.ContainsKey(relative))
                {
                    // Allow-listed — clause 3 legitimate. The entire
                    // file is exempt, so the rest of its lines can be
                    // skipped (no further matches in this file can
                    // produce a violation).
                    break;
                }

                violations.Add($"  {relative}:{i + 1}: {lines[i].Trim()}");
                // No `break` here — continue scanning the file to
                // report ALL violations, not just the first. (#142)
                // A future migration squash that consolidates several
                // backfills could land multiple `AT TIME ZONE` sites
                // in one file; reporting all of them at once spares
                // the contributor N rebuild cycles where 1 would do.
            }
        }
        return (violations, scanned);
    }

    [Fact]
    public void No_raw_SQL_in_production_code_uses_AT_TIME_ZONE_outside_the_clause_three_allow_list()
    {
        var productionRoot = FindProductionRoot();
        var (violations, scanned) = FindViolations(productionRoot, ClauseThreeAllowList);

        // Silent-no-op guard: if FindProductionRoot ever resolves to
        // an empty/wrong tree (e.g. project rename, partial checkout,
        // broken CI cache layout), the scanner sees zero files,
        // returns zero violations, and the assertion below would
        // false-pass. The floor below ensures the gate either does
        // real work or fails loudly.
        scanned.Should().BeGreaterOrEqualTo(
            MinExpectedProductionFiles,
            $"the api/CompliDrop.Api/ tree should contain at least " +
            $"{MinExpectedProductionFiles} .cs files; if the scanner sees " +
            "fewer, FindProductionRoot likely resolved to the wrong " +
            "directory and the gate would be a silent no-op.");

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

    [Fact]
    public void Clause_three_allow_list_entries_cite_an_ADR_clause()
    {
        // The reviewer-audit rationale only works if every entry
        // actually says WHY the file is exempt. A future contributor
        // adding `["Migrations/foo.cs"] = ""` or `= "TODO"` defeats
        // the audit. Regex matches `clause N` or `ADR NNNN` (any
        // 4-digit number) — both legitimate citations.
        var citationRegex = new Regex(
            @"clause\s*\d|ADR\s*\d{4}",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        foreach (var (relative, reason) in ClauseThreeAllowList)
        {
            reason.Should().NotBeNullOrWhiteSpace(
                $"allow-list entry '{relative}' must have a non-empty rationale.");
            citationRegex.IsMatch(reason).Should().BeTrue(
                $"allow-list entry '{relative}' rationale '{reason}' must name " +
                "the ADR clause that makes it legitimate (e.g. " +
                "\"ADR 0009 clause 3 — ...\"). See the existing entry for " +
                "the canonical shape.");
        }
    }

    [Theory]
    [InlineData("// foo", true)]
    [InlineData("    // foo", true)]
    [InlineData("/// XML doc", true)]
    [InlineData("    /// XML doc", true)]
    [InlineData("/* block-comment opener */", true)]
    [InlineData("    /* opener */", true)]
    [InlineData(" * continuation", true)]
    [InlineData("    * continuation", true)]
    [InlineData("var sql = \"SELECT 1\";", false)]
    [InlineData("cmd.CommandText = \"...AT TIME ZONE...\"; // note", false)]
    [InlineData("\"ProcessingStartedAt\" = now() at time zone 'utc',", false)]
    [InlineData("", false)]
    [InlineData("   ", false)]
    public void IsCommentLine_classifies_lines_correctly(string line, bool expected)
    {
        // Pins the classifier's contract. The scanner's whole "skip
        // comments" branch hinges on this — a refactor that broke
        // IsCommentLine (e.g. returning true for everything) would
        // silently disable the gate without any other test noticing.
        // This theory makes that failure mode loud.
        IsCommentLine(line).Should().Be(expected);
    }

    [Fact]
    public void Scanner_flags_a_known_violation_under_a_temp_root()
    {
        // Hermetic positive-direction test: write a synthetic fixture
        // containing the offending SQL into a %TEMP% subdir, run the
        // scanner against it, assert the violation surfaces. Without
        // this test, the scanner's "find a violation" branch is
        // verified only against the production tree's all-clean state
        // — which proves the negative path but leaves the positive
        // path untested after merge.
        var temp = Directory.CreateTempSubdirectory("adr0009-positive-").FullName;
        try
        {
            var fixture = Path.Combine(temp, "Worker.cs");
            File.WriteAllText(
                fixture,
                "var sql = \"SELECT now() AT TIME ZONE 'utc' FROM t\";\n");

            var (violations, scanned) = FindViolations(
                temp,
                new Dictionary<string, string>());

            scanned.Should().Be(1, "the temp tree contains exactly one .cs file");
            violations.Should().ContainSingle()
                .Which.Should()
                    .Contain("Worker.cs").And
                    .Contain("1:").And
                    .Contain("AT TIME ZONE");
        }
        finally
        {
            Directory.Delete(temp, recursive: true);
        }
    }

    [Fact]
    public void Scanner_skips_a_comment_only_line_under_a_temp_root()
    {
        // Hermetic negative-direction test: write a synthetic fixture
        // where `AT TIME ZONE` appears ONLY in a comment, run the
        // scanner against it, assert no violation surfaces. Without
        // this test, an over-eager refactor of IsCommentLine (e.g.
        // dropping the check entirely) would surface as a flood of
        // false positives on production XML-doc comments — but only
        // AFTER the over-eager change had landed and broken CI for
        // legitimate code.
        var temp = Directory.CreateTempSubdirectory("adr0009-negative-").FullName;
        try
        {
            var fixture = Path.Combine(temp, "Worker.cs");
            File.WriteAllText(
                fixture,
                "// AT TIME ZONE is forbidden in raw SQL (this comment is fine).\n" +
                "/// <summary>Also at time zone in an XML doc.</summary>\n" +
                " * AT TIME ZONE in a block-comment continuation.\n" +
                "/* AT TIME ZONE in a block-comment opener */\n" +
                "var sql = \"SELECT 1\";\n");

            var (violations, _) = FindViolations(
                temp,
                new Dictionary<string, string>());

            violations.Should().BeEmpty(
                "every `AT TIME ZONE` occurrence in the fixture lives inside a " +
                "comment line; the scanner must skip all four shapes.");
        }
        finally
        {
            Directory.Delete(temp, recursive: true);
        }
    }

    [Fact]
    public void Scanner_reports_all_violations_in_a_single_file_under_a_temp_root()
    {
        // Pins the #142 behavior: when a single file contains MULTIPLE
        // non-comment `AT TIME ZONE` sites, the scanner surfaces every
        // one in a single test-failure message — not just the first.
        // A `break;` regression that re-truncated to first-hit-per-file
        // would fail this test loudly. Without this fixture the new
        // behavior is only verified by the production tree's all-clean
        // state (which can't disambiguate "0 matched" from "1 matched
        // and break-ed out after the first").
        var temp = Directory.CreateTempSubdirectory("adr0009-multi-").FullName;
        try
        {
            var fixture = Path.Combine(temp, "Worker.cs");
            // Three distinct non-comment `AT TIME ZONE` sites, each on
            // its own line number so the formatted violation strings
            // are unambiguous. Mix of `AT TIME ZONE` casings to keep
            // the case-insensitive contract honest.
            File.WriteAllText(
                fixture,
                "var a = \"SELECT now() AT TIME ZONE 'utc'\";\n" +
                "var b = \"UPDATE t SET ts = now() at time zone 'America/Denver'\";\n" +
                "var c = \"DELETE FROM t WHERE ts < (now() At Time Zone 'utc')\";\n");

            var (violations, scanned) = FindViolations(
                temp,
                new Dictionary<string, string>());

            scanned.Should().Be(1, "the temp tree contains exactly one .cs file");
            violations.Should().HaveCount(3,
                "all three non-comment `AT TIME ZONE` sites in the single fixture " +
                "file must surface — a `break;` regression that truncated to the " +
                "first hit per file would fail this assertion (#142).");

            // Pin that each line number is reported individually — a
            // future refactor that collapsed multi-hits into a single
            // "violations: 3" summary would also fail loudly.
            violations.Should().Contain(v => v.Contains("Worker.cs:1:"));
            violations.Should().Contain(v => v.Contains("Worker.cs:2:"));
            violations.Should().Contain(v => v.Contains("Worker.cs:3:"));
        }
        finally
        {
            Directory.Delete(temp, recursive: true);
        }
    }

    [Fact]
    public void Scanner_respects_the_allow_list_under_a_temp_root()
    {
        // Hermetic allow-list test: write a synthetic fixture WITH a
        // real violation, but include its relative path in the
        // supplied allow-list. The scanner must NOT report it. Pins
        // the allow-list bypass against an over-eager refactor that
        // forgets to honor the dictionary.
        var temp = Directory.CreateTempSubdirectory("adr0009-allowlist-").FullName;
        try
        {
            var subdir = Path.Combine(temp, "Migrations");
            Directory.CreateDirectory(subdir);
            var fixture = Path.Combine(subdir, "Legitimate.cs");
            File.WriteAllText(
                fixture,
                "var sql = \"SELECT (ts AT TIME ZONE 'UTC')::date\";\n");

            var allowList = new Dictionary<string, string>
            {
                ["Migrations/Legitimate.cs"] = "ADR 0009 clause 3 — test fixture",
            };

            var (violations, scanned) = FindViolations(temp, allowList);

            scanned.Should().Be(1);
            violations.Should().BeEmpty(
                "the fixture's relative path is in the allow-list; the violation " +
                "must be suppressed even though the substring is present on a " +
                "non-comment line.");
        }
        finally
        {
            Directory.Delete(temp, recursive: true);
        }
    }
}
