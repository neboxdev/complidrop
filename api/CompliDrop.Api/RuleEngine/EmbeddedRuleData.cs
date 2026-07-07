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
        RuleSetLoader.LoadFromJsonSources(ReadAllSources(), options);

    private static IEnumerable<(string Name, string Json)> ReadAllSources()
    {
        foreach (var resourceName in ResourceNames)
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
