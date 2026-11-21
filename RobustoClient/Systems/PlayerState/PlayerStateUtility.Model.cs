using Content.Shared.Mobs;
using Content.Shared.Security;
using Robust.Shared.GameObjects;
using RobustoClient.Systems;

namespace RobustoClient.Systems.PlayerState;

/// <summary>
/// One block of player state. Each block is the top layer of the data collection: a typed answer
/// that either applies to the entity or is null.
/// </summary>
public enum PlayerStateData
{
    Identity,
    Vitality,
    Command,
    Security,
    Syndicate,
    Antagonist,
    Theft,
    Chemistry,
    Equipment,
    Inventory
}

/// <summary>One reported data block. A null block means it does not apply to the entity.</summary>
public interface IPlayerData
{
    PlayerStateData Category { get; }
}

/// <summary>Who the entity is, as far as the client can see. Applies to every entity.</summary>
public sealed record IdentityData(
    EntityUid Entity,
    string EntityName,
    string SessionName,
    string JobTitle,
    EntityUid? IdCard,
    string? CommandRole,
    bool HasMind,
    float? RangeMeters) : IPlayerData
{
    public PlayerStateData Category => PlayerStateData.Identity;
}

/// <summary>Life state and control. Null for entities that are not mobs.</summary>
public sealed record VitalityData(
    MobState State,
    bool HasMind,
    bool IsSilicon) : IPlayerData
{
    public PlayerStateData Category => PlayerStateData.Vitality;
}

/// <summary>A command role, with the items that attributed it. Null when the entity is not command.</summary>
public sealed record CommandData(
    string Key,
    string Label,
    bool Mindshielded,
    IReadOnlyList<CommandItemEntry> Items) : IPlayerData
{
    public PlayerStateData Category => PlayerStateData.Command;
}

/// <summary>Security department membership and the station's criminal status.</summary>
public sealed record SecurityData(
    SecurityStatus Status,
    bool IsSecurity) : IPlayerData
{
    public PlayerStateData Category => PlayerStateData.Security;
}

/// <summary>Everything that reads as a syndicate link. Emagged silicons land here too.</summary>
public sealed record SyndicateData(
    string? UplinkStatus,
    bool HasContraband,
    bool Emagged,
    IReadOnlyList<ImplantInfo> Implants,
    IReadOnlyList<string> Gear,
    bool HasGear,
    bool HasHedgedSignal) : IPlayerData
{
    public PlayerStateData Category => PlayerStateData.Syndicate;
}

/// <summary>
/// A recognised antagonist. <paramref name="Retained"/> means the verdict was latched earlier in the
/// round and outlives the evidence that produced it, <paramref name="Hedged"/> means it rests on gear
/// rather than a marker component.
/// </summary>
public sealed record AntagonistData(
    AntagonistKind Kind,
    bool Retained,
    bool Hedged,
    ChangelingData? Changeling) : IPlayerData
{
    public PlayerStateData Category => PlayerStateData.Antagonist;
}

/// <summary>What another client can see about a changeling, reconstructed from replicated state.</summary>
public sealed record ChangelingData(
    IReadOnlyList<string> Triggers,
    IReadOnlyList<string> PurchasedActionIds,
    bool HasFleshClothing,
    int? Dna,
    TimeSpan? HorrorRemaining);

/// <summary>Carried contraband the target's own ID does not clear.</summary>
public sealed record TheftData(IReadOnlyList<TheftEntry> Items) : IPlayerData
{
    public PlayerStateData Category => PlayerStateData.Theft;
}

/// <summary>Reagents and medical items the target carries.</summary>
public sealed record ChemistryData(
    IReadOnlyList<ChemicalEntry> Contents,
    IReadOnlyList<HealingEntry> Healing,
    bool Dangerous) : IPlayerData
{
    public PlayerStateData Category => PlayerStateData.Chemistry;
}

/// <summary>Weapons, non-lethals and the combined armour coefficients.</summary>
public sealed record EquipmentData(
    IReadOnlyList<WeaponEntry> Weapons,
    IReadOnlyList<WeaponEntry> NonLethals,
    IReadOnlyDictionary<string, float> ArmorCoefficients) : IPlayerData
{
    public PlayerStateData Category => PlayerStateData.Equipment;
}

/// <summary>The flat inventory list behind the inventory window.</summary>
public sealed record InventoryData(IReadOnlyList<InventoryEntry> Entries) : IPlayerData
{
    public PlayerStateData Category => PlayerStateData.Inventory;
}

/// <summary>A container the target carries and what is inside it.</summary>
public sealed record ChemicalEntry(string Holder, string Reagent, float Amount, string Severity);

/// <summary>Rounds left or loaded count, kept numeric so formatting stays in the UI.</summary>
public sealed record AmmunitionState(int? Shots = null, int? Capacity = null, int? HitsLeft = null);

/// <summary>A weapon, or a non-lethal that shares the name and shape.</summary>
public sealed record WeaponEntry(string Name, float Damage, AmmunitionState Ammunition, bool IsRanged);

/// <summary>A command-flavoured item, attributed to the role it belongs to.</summary>
public sealed record CommandItemEntry(string Role, string ItemName);

/// <summary>A carried item that reads as theft, with the severity that flagged it.</summary>
public sealed record TheftEntry(string Name, string Severity);

/// <summary>A healing item the target carries, with how many of that name.</summary>
public sealed record HealingEntry(string Name, int Count);
