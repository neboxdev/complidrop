using System.Reflection;

namespace CompliDrop.Api.RuleEngine;

/// <summary>
/// Loads the REAL regulatory rule set from the JSON files embedded in this assembly under
/// <c>RuleData/{us-fed,us-tx}/&lt;entity&gt;.json</c> (SCHEMA §1). The files ship as embedded resources (see
/// the API csproj), so they travel with the compiled API and are validated the same way whether loaded at
/// boot or in a test. Fail-fast: a malformed rule set throws <see cref="RuleSchemaException"/>, exactly like
/// the migration drift guard.
/// </summary>
public static class EmbeddedRuleData
{
    private static readonly Assembly OwningAssembly = typeof(EmbeddedRuleData).Assembly;

    /// <summary>The embedded RuleData resource names, in a stable (ordinal) order for deterministic merges.</summary>
    public static IReadOnlyList<string> ResourceNames { get; } = OwningAssembly
        .GetManifestResourceNames()
        .Where(IsRuleDataResource)
        .OrderBy(n => n, StringComparer.Ordinal)
        .ToList();

    /// <summary>
    /// Loads and validates every embedded RuleData file, merged into one <see cref="RuleSet"/> so
    /// federal/state layering and <c>satisfiesFederal</c> suppression resolve across files. Pass
    /// <see cref="RuleLoadOptions.VerifiedOnly"/> for the production posture (probable rules dropped).
    /// </summary>
    public static RuleSet LoadAll(RuleLoadOptions? options = null) =>
        RuleSetLoader.LoadFromJsonSources(ReadSources(ResourceNames), options);

    /// <summary>
    /// Loads only the rule-set FILES named by "jurisdiction/entity" keys (e.g. "us-fed/caterer",
    /// "us-tx/cross-cutting") — the per-rule-set feature-flag unit (SCHEMA §6). Throws on a key that
    /// matches no embedded file (a config typo must fail the boot, not silently load nothing), and the
    /// merged validation throws on an incoherent selection (e.g. a state file whose satisfiesFederal
    /// references a federal file that was not selected).
    /// </summary>
    public static RuleSet LoadSelected(IEnumerable<string> ruleSetKeys, RuleLoadOptions? options = null)
    {
        var selected = new List<string>();
        foreach (var key in ruleSetKeys)
        {
            var match = ResourceNames.FirstOrDefault(r => ResourceMatchesKey(r, key))
                ?? throw new RuleSchemaException(
                    $"Enabled rule-set '{key}' matches no embedded RuleData file. Known files: " +
                    string.Join(", ", ResourceNames.Select(FriendlyKey)));
            if (!selected.Contains(match))
                selected.Add(match);
        }
        return RuleSetLoader.LoadFromJsonSources(ReadSources(selected), options);
    }

    /// <summary>The "jurisdiction/entity" key a resource name answers to (e.g. "us-fed/caterer").</summary>
    public static string FriendlyKey(string resourceName)
    {
        var i = resourceName.IndexOf(".RuleData.", StringComparison.Ordinal);
        var tail = resourceName[(i + ".RuleData.".Length)..];
        tail = tail[..^".json".Length];
        // The folder segment separator is '.', and the SDK may mangle '-' to '_' — normalize back.
        var parts = tail.Split('.');
        return string.Join("/", parts).Replace('_', '-');
    }

    private static bool ResourceMatchesKey(string resourceName, string key) =>
        string.Equals(FriendlyKey(resourceName), key.Trim().Replace('_', '-'), StringComparison.OrdinalIgnoreCase);

    private static IEnumerable<(string Name, string Json)> ReadSources(IEnumerable<string> resourceNames)
    {
        foreach (var resourceName in resourceNames)
        {
            using var stream = OwningAssembly.GetManifestResourceStream(resourceName)
                ?? throw new RuleSchemaException($"Embedded rule resource '{resourceName}' could not be opened.");
            using var reader = new StreamReader(stream);
            yield return (resourceName, reader.ReadToEnd());
        }
    }

    // Resource names are "{RootNamespace}.RuleData.<sub>.<entity>.json" (the folder segment mangling of
    // "us-fed"/"us-tx" is SDK-version dependent, so match on the stable ".RuleData." + ".json" markers).
    private static bool IsRuleDataResource(string name) =>
        name.Contains(".RuleData.", StringComparison.Ordinal) &&
        name.EndsWith(".json", StringComparison.OrdinalIgnoreCase);
}
