using Robust.Shared.GameObjects;

namespace RobustoClient.Systems.PlayerState;

/// <summary>
/// Every data block known for one entity, resolved lazily so a blip that only needs the antagonist
/// state never pays for an inventory scan. Built by <see cref="PlayerStateUtility.GetSnapshot"/>,
/// which caches and retains it.
/// </summary>
public sealed class PlayerSnapshot
{
    private readonly Func<PlayerStateData, IPlayerData?>? _resolve;
    private readonly Dictionary<PlayerStateData, IPlayerData?> _blocks = new();
    private PlayerFlagReport? _flags;

    /// <summary>Literal snapshot, for tests and for callers that already have the blocks.</summary>
    public PlayerSnapshot()
    {
    }

    internal PlayerSnapshot(
        EntityUid entity,
        PlayerFlagReport flags,
        TimeSpan stamp,
        Func<PlayerStateData, IPlayerData?> resolve)
    {
        Entity = entity;
        _flags = flags;
        Stamp = stamp;
        _resolve = resolve;
    }

    public EntityUid Entity { get; init; }

    /// <summary>Round time this snapshot was built.</summary>
    internal TimeSpan Stamp { get; set; }

    /// <summary>True when the answer comes from the retained snapshot instead of a live scan.</summary>
    public bool Stale { get; internal set; }

    /// <summary>Every flag evaluated for the entity, with the trigger behind each one that fired.</summary>
    public PlayerFlagReport Flags
    {
        get => _flags ??= PlayerFlagReport.Empty;
        init => _flags = value;
    }

    public IdentityData? Identity
    {
        get => Get<IdentityData>(PlayerStateData.Identity);
        init => _blocks[PlayerStateData.Identity] = value;
    }

    public VitalityData? Vitality
    {
        get => Get<VitalityData>(PlayerStateData.Vitality);
        init => _blocks[PlayerStateData.Vitality] = value;
    }

    public CommandData? Command
    {
        get => Get<CommandData>(PlayerStateData.Command);
        init => _blocks[PlayerStateData.Command] = value;
    }

    public SecurityData? Security
    {
        get => Get<SecurityData>(PlayerStateData.Security);
        init => _blocks[PlayerStateData.Security] = value;
    }

    public SyndicateData? Syndicate
    {
        get => Get<SyndicateData>(PlayerStateData.Syndicate);
        init => _blocks[PlayerStateData.Syndicate] = value;
    }

    public AntagonistData? Antagonist
    {
        get => Get<AntagonistData>(PlayerStateData.Antagonist);
        init => _blocks[PlayerStateData.Antagonist] = value;
    }

    public TheftData? Theft
    {
        get => Get<TheftData>(PlayerStateData.Theft);
        init => _blocks[PlayerStateData.Theft] = value;
    }

    public ChemistryData? Chemistry
    {
        get => Get<ChemistryData>(PlayerStateData.Chemistry);
        init => _blocks[PlayerStateData.Chemistry] = value;
    }

    public EquipmentData? Equipment
    {
        get => Get<EquipmentData>(PlayerStateData.Equipment);
        init => _blocks[PlayerStateData.Equipment] = value;
    }

    public InventoryData? Inventory
    {
        get => Get<InventoryData>(PlayerStateData.Inventory);
        init => _blocks[PlayerStateData.Inventory] = value;
    }

    /// <summary>The block for a category, or null when it does not apply to the entity.</summary>
    public IPlayerData? GetData(PlayerStateData category) => category switch
    {
        PlayerStateData.Identity => Identity,
        PlayerStateData.Vitality => Vitality,
        PlayerStateData.Command => Command,
        PlayerStateData.Security => Security,
        PlayerStateData.Syndicate => Syndicate,
        PlayerStateData.Antagonist => Antagonist,
        PlayerStateData.Theft => Theft,
        PlayerStateData.Chemistry => Chemistry,
        PlayerStateData.Equipment => Equipment,
        PlayerStateData.Inventory => Inventory,
        _ => null
    };

    /// <summary>The block for a category, typed.</summary>
    public T? GetData<T>() where T : class, IPlayerData => GetData(CategoryOf(typeof(T))) as T;

    internal static PlayerStateData CategoryOf(Type type) => type switch
    {
        _ when type == typeof(IdentityData) => PlayerStateData.Identity,
        _ when type == typeof(VitalityData) => PlayerStateData.Vitality,
        _ when type == typeof(CommandData) => PlayerStateData.Command,
        _ when type == typeof(SecurityData) => PlayerStateData.Security,
        _ when type == typeof(SyndicateData) => PlayerStateData.Syndicate,
        _ when type == typeof(AntagonistData) => PlayerStateData.Antagonist,
        _ when type == typeof(TheftData) => PlayerStateData.Theft,
        _ when type == typeof(ChemistryData) => PlayerStateData.Chemistry,
        _ when type == typeof(EquipmentData) => PlayerStateData.Equipment,
        _ when type == typeof(InventoryData) => PlayerStateData.Inventory,
        _ => PlayerStateData.Identity
    };

    private T? Get<T>(PlayerStateData category) where T : class, IPlayerData
    {
        if (_blocks.TryGetValue(category, out var known))
            return known as T;

        var resolved = _resolve?.Invoke(category);
        _blocks[category] = resolved;
        return resolved as T;
    }
}
