using System.Text.Json;
using CompliDrop.Api.Configuration;
using FluentAssertions;

namespace CompliDrop.Api.Tests;

/// <summary>
/// Pins the default-OFF posture of the #416 template-corrections gate (ADR 0036 Amendment 3) at
/// BOTH layers a production boot reads: the bound settings type's CLR default and the checked-in
/// appsettings.json section. Mirrors <see cref="RuleEngine.RegulatoryWiringTests"/>' default pin
/// for <see cref="RuleEngineSettings"/> and the repo-file pin pattern of
/// <see cref="CleanupGateConfigTests"/>. Flipping either default to true would deploy the
/// legally-gated corrected checklist set + its cross-org re-grade on the next merge — exactly what
/// the gate exists to prevent until the G1-COUNSEL-BRIEF §0 attorney/broker sign-off.
/// </summary>
public class TemplateCorrectionsSettingsTests
{
    [Fact]
    public void The_flag_defaults_off()
    {
        new TemplateCorrectionsSettings().Enabled.Should().BeFalse(
            "the corrected checklist set ships merged but INVISIBLE until the G1 sign-off — a missing " +
            "config section must never enable it");
    }

    [Fact]
    public void Appsettings_carries_the_section_explicitly_off()
    {
        var appsettingsPath = FindRepoFile(Path.Combine("api", "CompliDrop.Api", "appsettings.json"));
        using var doc = JsonDocument.Parse(File.ReadAllText(appsettingsPath));

        doc.RootElement.GetProperty("TemplateCorrections").GetProperty("Enabled").GetBoolean()
            .Should().BeFalse(
                "the checked-in default must keep prod on the legacy (pre-#416) set until the legal " +
                "sign-off; flipping it in prod is a deliberate one-value config change");
    }

    // Walk up from the test bin dir to the repo checkout — same discovery approach as
    // CleanupGateConfigTests.ReadRepoEditorConfig (the test must read the CHECKED-IN file, not a
    // build-output copy that could drift from what ships).
    private static string FindRepoFile(string relative)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 8 && dir != null; i++, dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, relative);
            if (File.Exists(candidate)) return candidate;
        }

        throw new FileNotFoundException($"Could not locate {relative} from {AppContext.BaseDirectory}");
    }
}
