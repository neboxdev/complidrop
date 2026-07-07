namespace CompliDrop.Api.RuleEngine;

/// <summary>The value type of an entity-profile fact.</summary>
public enum FactKind
{
    Bool,
    Int,
    String
}

/// <summary>
/// The frozen entity-profile fact NAMES (SCHEMA §4). Constants so rule code and profile builders never
/// spell a fact name as a bare string. The loader rejects any leaf whose <c>fact</c> is not one of these.
/// </summary>
public static class FactNames
{
    public const string State = "state";
    public const string EntityType = "entityType";
    public const string EmployeeCount = "employeeCount";
    public const string CarriesWorkersComp = "carriesWorkersComp";
    public const string ServesOrSellsAlcohol = "servesOrSellsAlcohol";
    public const string PreparesOrServesFood = "preparesOrServesFood";
    public const string OperatesFoodVendingVehicle = "operatesFoodVendingVehicle";
    public const string RentsInflatableAmusementDevices = "rentsInflatableAmusementDevices";
    public const string OperatesForklifts = "operatesForklifts";
    public const string ProvidesArmedGuards = "providesArmedGuards";
    public const string OperatesVehiclesForHire = "operatesVehiclesForHire";
    public const string OperatesInterstate = "operatesInterstate";
    public const string MaxPassengerSeatingCapacity = "maxPassengerSeatingCapacity";
    public const string OperatesDronesCommercially = "operatesDronesCommercially";
    public const string SellsTaxableGoodsOrServices = "sellsTaxableGoodsOrServices";
    public const string IsFranchiseTaxableEntity = "isFranchiseTaxableEntity";
}

/// <summary>One entry in the frozen fact registry (SCHEMA §4): name + value type + UI prompt text.</summary>
public sealed record RuleFact(string Name, FactKind Kind, string PromptText);

/// <summary>
/// The FROZEN v1 applicability-fact registry (SCHEMA §4). Locked against the dossier's actual triggers.
/// The engine's closed fact vocabulary: the loader fails fast on any leaf referencing a fact not here,
/// and the profile refuses a fact of the wrong <see cref="FactKind"/>. Fact NAMES + kinds are frozen;
/// changing one is a schemaVersion bump. (The value SPACE of the enum-ish string facts — which entity
/// types / which state slugs exist — is rule CONTENT resolved in the rule-data step, not gated here.)
/// </summary>
public static class FactRegistry
{
    public static IReadOnlyList<RuleFact> All { get; } =
    [
        new(FactNames.State, FactKind.String, "In which US state does this entity operate?"),
        new(FactNames.EntityType, FactKind.String, "What type of business is this entity?"),
        new(FactNames.EmployeeCount, FactKind.Int, "How many employees does this entity have?"),
        new(FactNames.CarriesWorkersComp, FactKind.Bool, "Does this entity carry workers' compensation insurance?"),
        new(FactNames.ServesOrSellsAlcohol, FactKind.Bool, "Does this entity serve or sell alcohol?"),
        new(FactNames.PreparesOrServesFood, FactKind.Bool, "Does this entity prepare or serve food?"),
        new(FactNames.OperatesFoodVendingVehicle, FactKind.Bool, "Does this entity serve food from a vehicle?"),
        new(FactNames.RentsInflatableAmusementDevices, FactKind.Bool, "Does this entity rent inflatable amusement devices?"),
        new(FactNames.OperatesForklifts, FactKind.Bool, "Does this entity operate forklifts or powered industrial trucks?"),
        new(FactNames.ProvidesArmedGuards, FactKind.Bool, "Does this entity provide armed security guards?"),
        new(FactNames.OperatesVehiclesForHire, FactKind.Bool, "Does this entity operate vehicles for hire?"),
        new(FactNames.OperatesInterstate, FactKind.Bool, "Does this entity operate across state lines (interstate)?"),
        new(FactNames.MaxPassengerSeatingCapacity, FactKind.Int, "What is the largest vehicle's seating capacity, including the driver?"),
        new(FactNames.OperatesDronesCommercially, FactKind.Bool, "Does this entity operate drones commercially?"),
        new(FactNames.SellsTaxableGoodsOrServices, FactKind.Bool, "Does this entity sell taxable goods or services?"),
        new(FactNames.IsFranchiseTaxableEntity, FactKind.Bool, "Is this entity subject to the Texas franchise tax?"),
    ];

    private static readonly IReadOnlyDictionary<string, RuleFact> ByNameMap =
        All.ToDictionary(f => f.Name, StringComparer.Ordinal);

    public static bool IsKnown(string factName) => ByNameMap.ContainsKey(factName);

    public static bool TryGet(string factName, out RuleFact fact) => ByNameMap.TryGetValue(factName, out fact!);

    public static RuleFact Get(string factName) =>
        TryGet(factName, out var f) ? f : throw new KeyNotFoundException($"'{factName}' is not a registered fact.");
}
