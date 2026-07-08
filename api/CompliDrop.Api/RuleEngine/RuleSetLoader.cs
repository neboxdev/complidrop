using System.Text.Json;
using System.Text.RegularExpressions;

namespace CompliDrop.Api.RuleEngine;

/// <summary>Options controlling how a rule set loads. The defaults are the SAFE production posture (A-5/CC-8):
/// verified-only AND review-gated rule-sets excluded.</summary>
/// <param name="VerifiedOnly">
/// When true (the DEFAULT — the production posture, SCHEMA §6), only <see cref="RuleConfidence.Verified"/>
/// versions are kept and a rule left with no versions is dropped. Validation still runs over the FULL set
/// first, so a state rule's <c>satisfiesFederal</c> reference to a to-be-filtered federal rule is still checked.
/// </param>
/// <param name="IncludeReviewGated">
/// When false (the DEFAULT), rules from any rule-set FILE that declares a <c>reviewGate</c> (e.g. the TX
/// security set) are excluded — independent of confidence — so a human-gated rule-set can't drive a verdict
/// until it clears its founder/counsel gate (A-5). Set true only behind the review flag.
/// </param>
public sealed record RuleLoadOptions(bool VerifiedOnly = true, bool IncludeReviewGated = false);

/// <summary>
/// Thrown when a rule file violates the frozen schema (SCHEMA §1–§5). Fail-fast, like the migration drift
/// guard: a malformed rule set must stop the boot rather than silently mis-evaluate obligations.
/// </summary>
public sealed class RuleSchemaException : Exception
{
    public RuleSchemaException(string message) : base(message) { }
    public RuleSchemaException(string message, Exception inner) : base(message, inner) { }
}

/// <summary>
/// Loads and validates rule sets from JSON (SCHEMA §1). Deserializes the versioned rule files, then runs a
/// semantic validation pass that fails fast on: a leaf referencing a fact not in the frozen registry, an
/// unknown operator, an operator/type mismatch, a bad date or inverted version window, an out-of-vocabulary
/// category / documentType / jurisdiction, or a DANGLING <c>satisfiesFederal</c> reference. jsonc comments
/// and trailing commas are tolerated (the schema's rule files are authored as jsonc).
/// </summary>
public static class RuleSetLoader
{
    public const int SupportedSchemaVersion = 1;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private static readonly Regex StateJurisdiction = new("^us-[a-z]{2}$", RegexOptions.Compiled);
    private static readonly HashSet<string> Categories = new(StringComparer.Ordinal)
        { "license", "permit", "worker-certification", "insurance", "filing" };
    private static readonly HashSet<string> DocumentTypes = new(StringComparer.OrdinalIgnoreCase)
        { "coi", "license", "certification", "other" };

    /// <summary>Loads a single JSON document.</summary>
    public static RuleSet LoadFromJson(string sourceName, string json, RuleLoadOptions? options = null) =>
        LoadFromJsonSources([(sourceName, json)], options);

    /// <summary>
    /// Loads and MERGES several JSON documents (e.g. us-fed + us-tx for one entity type) into one validated
    /// rule set, so federal/state layering and satisfiesFederal suppression resolve against the whole set.
    /// </summary>
    public static RuleSet LoadFromJsonSources(IEnumerable<(string Name, string Json)> sources, RuleLoadOptions? options = null)
    {
        options ??= new RuleLoadOptions();
        var rules = new List<Rule>();
        // Rule ids belonging to a review-gated FILE (A-5). Tracked by id so the exclusion survives the
        // flatten-into-one-list merge.
        var reviewGatedRuleIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var (name, json) in sources)
        {
            RuleSet? file;
            try
            {
                file = JsonSerializer.Deserialize<RuleSet>(json, JsonOptions);
            }
            catch (JsonException ex)
            {
                throw new RuleSchemaException($"{name}: could not parse rule JSON — {ex.Message}", ex);
            }

            if (file is null)
                throw new RuleSchemaException($"{name}: rule file deserialized to null.");
            if (file.SchemaVersion != SupportedSchemaVersion)
                throw new RuleSchemaException($"{name}: unsupported schemaVersion {file.SchemaVersion} (this engine supports {SupportedSchemaVersion}).");

            var fileLocalObligations = file.LocalObligations ?? [];
            var fileIsGated = !string.IsNullOrWhiteSpace(file.ReviewGate);

            foreach (var rule in file.Rules)
            {
                // Denormalize the file's localObligations onto each rule (CC-7) so the evaluator can union
                // the pointers of the rule-sets that applied to an entity.
                rules.Add(fileLocalObligations.Count > 0 ? rule with { LocalObligations = fileLocalObligations } : rule);
                if (fileIsGated)
                    reviewGatedRuleIds.Add(rule.Id);
            }
        }

        // Validate the FULL merged set first (before any filtering) so satisfiesFederal refs, facts, and
        // shapes are checked even for rules a filter will later drop.
        Validate(rules);

        IEnumerable<Rule> effective = rules;
        if (!options.IncludeReviewGated && reviewGatedRuleIds.Count > 0)
            effective = effective.Where(r => !reviewGatedRuleIds.Contains(r.Id));
        var effectiveList = effective.ToList();
        if (options.VerifiedOnly)
            effectiveList = [.. FilterVerified(effectiveList)];

        return new RuleSet { SchemaVersion = SupportedSchemaVersion, Rules = effectiveList };
    }

    /// <summary>Loads and merges every <c>*.json</c> under a directory tree.</summary>
    public static RuleSet LoadFromDirectory(string directory, RuleLoadOptions? options = null)
    {
        if (!Directory.Exists(directory))
            throw new RuleSchemaException($"Rule directory not found: {directory}");

        var sources = Directory
            .EnumerateFiles(directory, "*.json", SearchOption.AllDirectories)
            .OrderBy(p => p, StringComparer.Ordinal)
            .Select(path => (Name: Path.GetFileName(path), Json: File.ReadAllText(path)))
            .ToList();

        return LoadFromJsonSources(sources, options);
    }

    private static IReadOnlyList<Rule> FilterVerified(IReadOnlyList<Rule> rules)
    {
        var kept = new List<Rule>();
        foreach (var rule in rules)
        {
            var verified = rule.Versions.Where(v => v.Confidence == RuleConfidence.Verified).ToList();
            if (verified.Count > 0)
                kept.Add(rule with { Versions = verified });
        }
        return kept;
    }

    // ------------------------------------------------------------------------------------------------
    // Validation
    // ------------------------------------------------------------------------------------------------

    private static void Validate(IReadOnlyList<Rule> rules)
    {
        var seenIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var rule in rules)
        {
            if (string.IsNullOrWhiteSpace(rule.Id))
                throw new RuleSchemaException("A rule is missing its 'id'.");
            if (!seenIds.Add(rule.Id))
                throw new RuleSchemaException($"Duplicate rule id '{rule.Id}'.");
        }

        var federalIds = rules
            .Where(r => string.Equals(r.Jurisdiction, "us-fed", StringComparison.OrdinalIgnoreCase))
            .Select(r => r.Id)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var rule in rules)
            ValidateRule(rule, federalIds);
    }

    private static void ValidateRule(Rule rule, IReadOnlySet<string> federalIds)
    {
        var where = $"rule '{rule.Id}'";

        if (string.IsNullOrWhiteSpace(rule.ObligationRef))
            throw new RuleSchemaException($"{where}: 'obligationRef' is required.");

        if (string.IsNullOrWhiteSpace(rule.Jurisdiction) ||
            !(string.Equals(rule.Jurisdiction, "us-fed", StringComparison.OrdinalIgnoreCase) || StateJurisdiction.IsMatch(rule.Jurisdiction)))
            throw new RuleSchemaException($"{where}: invalid jurisdiction '{rule.Jurisdiction}' (expected 'us-fed' or a 'us-xx' state slug).");

        if (!Categories.Contains(rule.Category))
            throw new RuleSchemaException($"{where}: invalid category '{rule.Category}' (expected one of {string.Join(", ", Categories)}).");

        if (!string.Equals(rule.Basis, "regulatory", StringComparison.OrdinalIgnoreCase))
            throw new RuleSchemaException($"{where}: basis must be 'regulatory' (only regulatory rules ship; contractual requirements live in per-org checklists).");

        foreach (var et in rule.EntityTypes)
            if (string.IsNullOrWhiteSpace(et))
                throw new RuleSchemaException($"{where}: an entityTypes entry is empty.");

        if (rule.Versions.Count == 0)
            throw new RuleSchemaException($"{where}: must have at least one version.");

        foreach (var version in rule.Versions)
            ValidateVersion(rule, version, federalIds);
    }

    private static void ValidateVersion(Rule rule, RuleVersion version, IReadOnlySet<string> federalIds)
    {
        var where = $"rule '{rule.Id}' v{version.Version}";

        if (version.Version < 1)
            throw new RuleSchemaException($"{where}: 'version' must be >= 1.");

        if (version.ValidTo is { } to && to < version.ValidFrom)
            throw new RuleSchemaException($"{where}: validTo ({to:O}) is before validFrom ({version.ValidFrom:O}).");

        if (version.Applicability is null)
            throw new RuleSchemaException($"{where}: 'applicability' is required (use {{ \"all\": [] }} for 'always applies').");
        ValidateApplicability(version.Applicability, where);

        if (version.Obligation is null)
            throw new RuleSchemaException($"{where}: 'obligation' is required.");
        ValidateObligation(version.Obligation, where);

        if (version.Cadence is not null)
            ValidateCadence(version.Cadence, where);

        if (version.InsuranceMinimums is not null)
        {
            if (!string.Equals(rule.Category, "insurance", StringComparison.OrdinalIgnoreCase))
                throw new RuleSchemaException($"{where}: insuranceMinimums is only valid on a category='insurance' rule.");
            if (version.InsuranceMinimums.PerOccurrence < 0 || version.InsuranceMinimums.Aggregate < 0)
                throw new RuleSchemaException($"{where}: insuranceMinimums amounts must be non-negative.");
            if (string.IsNullOrWhiteSpace(version.InsuranceMinimums.Currency))
                throw new RuleSchemaException($"{where}: insuranceMinimums.currency is required.");
        }

        if (version.Citation is not null && string.IsNullOrWhiteSpace(version.Citation.Section))
            throw new RuleSchemaException($"{where}: citation.section is required when a citation is present.");

        if (string.IsNullOrWhiteSpace(version.Rationale))
            throw new RuleSchemaException($"{where}: 'rationale' (user-facing) is required.");
        if (string.IsNullOrWhiteSpace(version.UserAction))
            throw new RuleSchemaException($"{where}: 'userAction' (user-facing) is required.");

        foreach (var fedRef in version.SatisfiesFederal)
            if (!federalIds.Contains(fedRef))
                throw new RuleSchemaException($"{where}: satisfiesFederal references '{fedRef}', which is not a loaded federal (us-fed) rule id.");
    }

    private static void ValidateApplicability(Applicability node, string where)
    {
        switch (node)
        {
            case AllCondition all:
                foreach (var c in all.Conditions) ValidateApplicability(c, where);
                break;
            case AnyCondition any:
                foreach (var c in any.Conditions) ValidateApplicability(c, where);
                break;
            case NotCondition not:
                ValidateApplicability(not.Inner, where);
                break;
            case LeafCondition leaf:
                ValidateLeaf(leaf, where);
                break;
            default:
                throw new RuleSchemaException($"{where}: unknown applicability node type {node.GetType().Name}.");
        }
    }

    private static void ValidateLeaf(LeafCondition leaf, string where)
    {
        if (!FactRegistry.TryGet(leaf.Fact, out var fact))
            throw new RuleSchemaException($"{where}: leaf references unknown fact '{leaf.Fact}' (not in the frozen §4 registry).");

        switch (leaf.Op)
        {
            case ConditionOp.Gte:
            case ConditionOp.Lte:
                if (fact.Kind != FactKind.Int)
                    throw new RuleSchemaException($"{where}: op '{RuleTokens.ToToken(leaf.Op)}' requires a numeric fact, but '{leaf.Fact}' is {fact.Kind}.");
                if (leaf.Value.ValueKind != JsonValueKind.Number || !leaf.Value.TryGetInt64(out _))
                    throw new RuleSchemaException($"{where}: op '{RuleTokens.ToToken(leaf.Op)}' on '{leaf.Fact}' needs an integer value.");
                break;

            case ConditionOp.In:
                if (leaf.Value.ValueKind != JsonValueKind.Array)
                    throw new RuleSchemaException($"{where}: op 'in' on '{leaf.Fact}' needs an array value.");
                var count = 0;
                foreach (var element in leaf.Value.EnumerateArray())
                {
                    ValidateScalarMatchesKind(element, fact.Kind, leaf.Fact, where);
                    count++;
                }
                if (count == 0)
                    throw new RuleSchemaException($"{where}: op 'in' on '{leaf.Fact}' needs a non-empty array.");
                break;

            case ConditionOp.Eq:
            case ConditionOp.Neq:
                ValidateScalarMatchesKind(leaf.Value, fact.Kind, leaf.Fact, where);
                break;

            default:
                throw new RuleSchemaException($"{where}: unknown operator on '{leaf.Fact}'.");
        }
    }

    private static void ValidateScalarMatchesKind(JsonElement value, FactKind kind, string factName, string where)
    {
        var ok = kind switch
        {
            FactKind.Bool => value.ValueKind is JsonValueKind.True or JsonValueKind.False,
            FactKind.Int => value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out _),
            FactKind.String => value.ValueKind == JsonValueKind.String,
            _ => false,
        };
        if (!ok)
            throw new RuleSchemaException($"{where}: value {value.GetRawText()} is not a valid {kind} for fact '{factName}'.");
    }

    private static void ValidateObligation(Obligation obligation, string where)
    {
        if (string.IsNullOrWhiteSpace(obligation.Name))
            throw new RuleSchemaException($"{where}: obligation.name is required.");
        if (!DocumentTypes.Contains(obligation.DocumentType))
            throw new RuleSchemaException($"{where}: obligation.documentType '{obligation.DocumentType}' is not one of coi|license|certification|other (RD-c).");
    }

    private static void ValidateCadence(Cadence cadence, string where)
    {
        if (cadence.GracePeriodDays < 0)
            throw new RuleSchemaException($"{where}: cadence.gracePeriodDays must be non-negative.");

        if (cadence.PeriodMonths is { } months && months <= 0)
            throw new RuleSchemaException($"{where}: cadence.periodMonths must be positive when present.");

        if (cadence.Anchor == CadenceAnchor.IssueDate && cadence.PeriodMonths is not > 0)
            throw new RuleSchemaException($"{where}: a cadence anchored on issueDate needs a positive periodMonths.");

        if (cadence.Anchor is CadenceAnchor.FixedDate or CadenceAnchor.CalendarDate)
        {
            if (cadence.FixedDate is null)
                throw new RuleSchemaException($"{where}: a {RuleTokens.ToToken(cadence.Anchor)} cadence needs a fixedDate {{month, day}}.");
        }

        if (cadence.FixedDate is { } md)
        {
            if (md.Month is < 1 or > 12)
                throw new RuleSchemaException($"{where}: cadence.fixedDate.month {md.Month} is out of range 1–12.");
            if (md.Day is < 1 or > 31)
                throw new RuleSchemaException($"{where}: cadence.fixedDate.day {md.Day} is out of range 1–31.");
        }
    }
}
