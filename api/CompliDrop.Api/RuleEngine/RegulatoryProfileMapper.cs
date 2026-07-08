using System.Text.Json;
using CompliDrop.Api.Entities;

namespace CompliDrop.Api.RuleEngine;

/// <summary>
/// Projects the persisted entity-profile columns (Organization.State/RegulatoryFactsJson,
/// Vendor.EntityType/RegulatoryFactsJson — the SCHEMA §4 facts) onto the engine's pure
/// <see cref="EntityProfile"/>, and a tracked <see cref="Document"/> onto <see cref="IDocumentLike"/>.
/// The engine core stays DB-free; this adapter is the only place EF entities meet it.
///
/// Fail-safe parsing: a fact whose name is not in the frozen registry, or whose JSON value doesn't match
/// the registered kind, is SKIPPED — the fact stays UNKNOWN (Kleene), so the engine asks the question
/// again (needs-profile-info) rather than ever deriving a verdict from malformed data.
/// </summary>
public static class RegulatoryProfileMapper
{
    /// <summary>The org itself evaluates as the venue (its own cross-cutting obligations).</summary>
    public static EntityProfile ForOrganization(Organization org)
    {
        var builder = EntityProfile.Builder();
        AddFactsFromJson(builder, org.RegulatoryFactsJson);
        if (!string.IsNullOrWhiteSpace(org.State)) builder.State(org.State);
        builder.EntityType(EntityTypes.VenueOrg);
        return builder.Build();
    }

    /// <summary>A vendor evaluates in its org's state with its own entity type + facts.</summary>
    public static EntityProfile ForVendor(Vendor vendor, Organization org)
    {
        var builder = EntityProfile.Builder();
        AddFactsFromJson(builder, vendor.RegulatoryFactsJson);
        if (!string.IsNullOrWhiteSpace(org.State)) builder.State(org.State);
        if (!string.IsNullOrWhiteSpace(vendor.EntityType)) builder.EntityType(vendor.EntityType);
        return builder.Build();
    }

    /// <summary>
    /// The engine-facing view of a tracked document. <c>IssueDate</c> maps from
    /// <see cref="Document.EffectiveDate"/> (the extracted policy-effective / completion date — the same
    /// field the issueDate-anchored cadences reason about); <c>GeneralLiabilityLimit</c> feeds the v1.2
    /// insurance amount gate for general-liability floors only.
    /// </summary>
    public static DocumentLike ToDocumentLike(Document document) => new(
        document.Id.ToString(),
        document.DocumentType,
        document.DocumentSubType,
        ExpirationDate: ToDateOnly(document.ExpirationDate),
        IssueDate: ToDateOnly(document.EffectiveDate),
        GeneralLiabilityLimit: document.GeneralLiabilityLimit);

    private static DateOnly? ToDateOnly(DateTime? value) =>
        value is { } dt ? DateOnly.FromDateTime(dt.Date) : null;

    /// <summary>Reads a flat {factName: bool|int|string} JSON map, keeping only registry-valid entries.
    /// The dedicated State/EntityType columns are applied AFTER this, so they win over any JSON duplicate.</summary>
    internal static void AddFactsFromJson(EntityProfileBuilder builder, JsonDocument? facts)
    {
        if (facts is null || facts.RootElement.ValueKind != JsonValueKind.Object)
            return;

        foreach (var property in facts.RootElement.EnumerateObject())
        {
            if (!FactRegistry.TryGet(property.Name, out var fact))
                continue; // unknown fact name: stays UNKNOWN, never guessed

            switch (fact.Kind)
            {
                case FactKind.Bool when property.Value.ValueKind is JsonValueKind.True or JsonValueKind.False:
                    builder.Set(fact.Name, FactValue.Of(property.Value.GetBoolean()));
                    break;
                case FactKind.Int when property.Value.ValueKind == JsonValueKind.Number && property.Value.TryGetInt64(out var n):
                    builder.Set(fact.Name, FactValue.Of(n));
                    break;
                case FactKind.String when property.Value.ValueKind == JsonValueKind.String:
                    builder.Set(fact.Name, FactValue.Of(property.Value.GetString()!));
                    break;
                // Kind/value mismatch: skip — the fact stays UNKNOWN (fail-safe, needs-profile-info).
            }
        }
    }
}
