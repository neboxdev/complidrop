namespace CompliDrop.Api.RuleEngine;

/// <summary>
/// A single entity-profile fact value — a tiny tagged union over the three <see cref="FactKind"/>s. A fact
/// that is simply ABSENT from an <see cref="EntityProfile"/> represents "unknown" (Kleene); a present
/// <see cref="FactValue"/> is always a concrete bool / int / string.
/// </summary>
public readonly struct FactValue : IEquatable<FactValue>
{
    public FactKind Kind { get; }
    private readonly bool _bool;
    private readonly long _int;
    private readonly string? _string;

    private FactValue(FactKind kind, bool b, long i, string? s)
    {
        Kind = kind;
        _bool = b;
        _int = i;
        _string = s;
    }

    public static FactValue Of(bool value) => new(FactKind.Bool, value, 0, null);
    public static FactValue Of(long value) => new(FactKind.Int, false, value, null);
    public static FactValue Of(int value) => Of((long)value);
    public static FactValue Of(string value) => new(FactKind.String, false, 0, value ?? throw new ArgumentNullException(nameof(value)));

    public bool AsBool => Kind == FactKind.Bool ? _bool : throw new InvalidOperationException($"Fact is {Kind}, not Bool.");
    public long AsInt => Kind == FactKind.Int ? _int : throw new InvalidOperationException($"Fact is {Kind}, not Int.");
    public string AsString => Kind == FactKind.String ? _string! : throw new InvalidOperationException($"Fact is {Kind}, not String.");

    public bool Equals(FactValue other) => Kind == other.Kind && Kind switch
    {
        FactKind.Bool => _bool == other._bool,
        FactKind.Int => _int == other._int,
        FactKind.String => string.Equals(_string, other._string, StringComparison.OrdinalIgnoreCase),
        _ => false,
    };

    public override bool Equals(object? obj) => obj is FactValue v && Equals(v);

    public override int GetHashCode() => Kind switch
    {
        FactKind.Bool => HashCode.Combine(Kind, _bool),
        FactKind.Int => HashCode.Combine(Kind, _int),
        FactKind.String => HashCode.Combine(Kind, _string?.ToLowerInvariant()),
        _ => 0,
    };

    public override string ToString() => Kind switch
    {
        FactKind.Bool => _bool.ToString(),
        FactKind.Int => _int.ToString(),
        FactKind.String => _string ?? "",
        _ => "",
    };
}

/// <summary>
/// An entity's answers to the frozen §4 profile questions. Facts are typed over <see cref="FactRegistry"/>:
/// a fact not present is UNKNOWN (representable, and the whole point — the engine must never treat an
/// unanswered question as False). Build one with <see cref="Builder"/> (typed setters) or the dictionary
/// constructor. The constructor rejects an unregistered fact name or a value whose kind disagrees with the
/// registry, so a profile can never carry a fact the rules can't reference.
/// </summary>
public sealed class EntityProfile
{
    private readonly IReadOnlyDictionary<string, FactValue> _facts;

    public EntityProfile(IReadOnlyDictionary<string, FactValue> facts)
    {
        foreach (var (name, value) in facts)
        {
            if (!FactRegistry.TryGet(name, out var fact))
                throw new ArgumentException($"'{name}' is not a registered fact (SCHEMA §4).", nameof(facts));
            if (fact.Kind != value.Kind)
                throw new ArgumentException($"Fact '{name}' is {fact.Kind} but was given a {value.Kind} value.", nameof(facts));
        }
        // Defensive copy: storing the caller's reference would let a reused builder (or any later mutation
        // of the source dictionary) change an already-constructed profile, bypassing the validation above.
        _facts = facts.ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);
    }

    /// <summary>An empty profile — every fact unknown.</summary>
    public static EntityProfile Empty { get; } = new(new Dictionary<string, FactValue>());

    /// <summary>The set facts, keyed by frozen fact name. Absence = unknown.</summary>
    public IReadOnlyDictionary<string, FactValue> Facts => _facts;

    public bool IsSet(string factName) => _facts.ContainsKey(factName);

    public bool TryGet(string factName, out FactValue value) => _facts.TryGetValue(factName, out value);

    public static EntityProfileBuilder Builder() => new();
}

/// <summary>
/// A fluent builder with a typed setter per frozen §4 fact, so callers describe an entity in terms of the
/// registry rather than raw strings. Any fact left unset stays UNKNOWN in the built profile.
/// </summary>
public sealed class EntityProfileBuilder
{
    private readonly Dictionary<string, FactValue> _facts = new(StringComparer.Ordinal);

    public EntityProfileBuilder State(string state) => Set(FactNames.State, FactValue.Of(state));
    public EntityProfileBuilder EntityType(string entityType) => Set(FactNames.EntityType, FactValue.Of(entityType));
    public EntityProfileBuilder EmployeeCount(int count) => Set(FactNames.EmployeeCount, FactValue.Of(count));
    public EntityProfileBuilder CarriesWorkersComp(bool value) => Set(FactNames.CarriesWorkersComp, FactValue.Of(value));
    public EntityProfileBuilder ServesOrSellsAlcohol(bool value) => Set(FactNames.ServesOrSellsAlcohol, FactValue.Of(value));
    public EntityProfileBuilder PreparesOrServesFood(bool value) => Set(FactNames.PreparesOrServesFood, FactValue.Of(value));
    public EntityProfileBuilder OperatesFoodVendingVehicle(bool value) => Set(FactNames.OperatesFoodVendingVehicle, FactValue.Of(value));
    public EntityProfileBuilder RentsInflatableAmusementDevices(bool value) => Set(FactNames.RentsInflatableAmusementDevices, FactValue.Of(value));
    public EntityProfileBuilder OperatesForklifts(bool value) => Set(FactNames.OperatesForklifts, FactValue.Of(value));
    public EntityProfileBuilder ProvidesArmedGuards(bool value) => Set(FactNames.ProvidesArmedGuards, FactValue.Of(value));
    public EntityProfileBuilder ProvidesArmedCloseProtection(bool value) => Set(FactNames.ProvidesArmedCloseProtection, FactValue.Of(value));
    public EntityProfileBuilder ProvidesUnarmedGuards(bool value) => Set(FactNames.ProvidesUnarmedGuards, FactValue.Of(value));
    public EntityProfileBuilder OperatesVehiclesForHire(bool value) => Set(FactNames.OperatesVehiclesForHire, FactValue.Of(value));
    public EntityProfileBuilder OperatesInterstate(bool value) => Set(FactNames.OperatesInterstate, FactValue.Of(value));
    public EntityProfileBuilder OperatesIntrastate(bool value) => Set(FactNames.OperatesIntrastate, FactValue.Of(value));
    public EntityProfileBuilder MaxPassengerSeatingCapacity(int seats) => Set(FactNames.MaxPassengerSeatingCapacity, FactValue.Of(seats));
    public EntityProfileBuilder OperatesDronesCommercially(bool value) => Set(FactNames.OperatesDronesCommercially, FactValue.Of(value));
    public EntityProfileBuilder SellsTaxableGoodsOrServices(bool value) => Set(FactNames.SellsTaxableGoodsOrServices, FactValue.Of(value));
    public EntityProfileBuilder IsFranchiseTaxableEntity(bool value) => Set(FactNames.IsFranchiseTaxableEntity, FactValue.Of(value));

    /// <summary>Escape hatch: set any registered fact by name. Validated (name + kind) by <see cref="Build"/>.</summary>
    public EntityProfileBuilder Set(string factName, FactValue value)
    {
        _facts[factName] = value;
        return this;
    }

    public EntityProfile Build() => new(_facts);
}
