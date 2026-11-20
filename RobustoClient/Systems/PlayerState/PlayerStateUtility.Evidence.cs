using Robust.Shared.GameObjects;

namespace RobustoClient.Systems.PlayerState;

/// <summary>
/// Identity of a player classification, a string-backed struct so new flags need no central list.
/// </summary>
public readonly record struct PlayerFlag(string Id)
{
    public static readonly PlayerFlag HasMind = new("HasMind");
    public static readonly PlayerFlag IsCommand = new("IsCommand");
    public static readonly PlayerFlag IsSecurity = new("IsSecurity");
    public static readonly PlayerFlag IsSyndicate = new("IsSyndicate");
    public static readonly PlayerFlag HasNoSlips = new("HasNoSlips");
    public static readonly PlayerFlag HasWeapon = new("HasWeapon");
    public static readonly PlayerFlag HasSyndicateGear = new("HasSyndicateGear");
    public static readonly PlayerFlag HasStolenGear = new("HasStolenGear");
    public static readonly PlayerFlag HasDangerousChemicals = new("HasDangerousChemicals");
    public static readonly PlayerFlag HasMedicalGear = new("HasMedicalGear");
    public static readonly PlayerFlag IsThief = new("IsThief");
    public static readonly PlayerFlag IsWanted = new("IsWanted");
    public static readonly PlayerFlag IsEmagged = new("IsEmagged");
    public static readonly PlayerFlag IsAntagonist = new("IsAntagonist");

    /// <summary>Every flag the utility registers by default. Used by the registration test.</summary>
    public static readonly PlayerFlag[] Defaults =
    [
        HasMind, IsCommand, IsSecurity, IsSyndicate, HasNoSlips, HasWeapon,
        HasSyndicateGear, HasStolenGear, HasDangerousChemicals, HasMedicalGear, IsThief,
        IsWanted, IsEmagged, IsAntagonist
    ];

    public override string ToString() => Id;
}

/// <summary>Flags grouped by the data block they answer for, for discoverability at the call site.</summary>
public static class PlayerFlags
{
    public static class Vitality
    {
        public static readonly PlayerFlag HasMind = PlayerFlag.HasMind;
    }

    public static class Command
    {
        public static readonly PlayerFlag IsCommand = PlayerFlag.IsCommand;
    }

    public static class Security
    {
        public static readonly PlayerFlag IsSecurity = PlayerFlag.IsSecurity;
        public static readonly PlayerFlag IsWanted = PlayerFlag.IsWanted;
    }

    public static class Syndicate
    {
        public static readonly PlayerFlag IsSyndicate = PlayerFlag.IsSyndicate;
        public static readonly PlayerFlag HasSyndicateGear = PlayerFlag.HasSyndicateGear;
        public static readonly PlayerFlag IsEmagged = PlayerFlag.IsEmagged;
    }

    public static class Antagonist
    {
        public static readonly PlayerFlag IsAntagonist = PlayerFlag.IsAntagonist;
    }

    public static class Theft
    {
        public static readonly PlayerFlag HasStolenGear = PlayerFlag.HasStolenGear;
        public static readonly PlayerFlag IsThief = PlayerFlag.IsThief;
    }

    public static class Chemistry
    {
        public static readonly PlayerFlag HasDangerousChemicals = PlayerFlag.HasDangerousChemicals;
        public static readonly PlayerFlag HasMedicalGear = PlayerFlag.HasMedicalGear;
    }

    public static class Equipment
    {
        public static readonly PlayerFlag HasWeapon = PlayerFlag.HasWeapon;
        public static readonly PlayerFlag HasNoSlips = PlayerFlag.HasNoSlips;
    }
}

/// <summary>Where a flag's evidence came from, so a consumer can say "inventory" or "implant".</summary>
public enum FlagTriggerSource
{
    Mind,
    Identity,
    Inventory,
    Equipped,
    Implant,
    Chemistry,
    Store,
    Component,
    Prototype,
    Record
}

/// <summary>
/// Why a flag fired and what is responsible. <paramref name="Entities"/> holds every item, implant,
/// card or other entity behind the trigger, so a consumer can list them without re-scanning.
/// </summary>
public sealed record PlayerFlagTrigger(
    FlagTriggerSource Source,
    string Reason,
    IReadOnlyList<EntityUid> Entities,
    string? Detail = null)
{
    /// <summary>Trigger backed by at most one entity.</summary>
    public static PlayerFlagTrigger For(
        FlagTriggerSource source,
        string reason,
        EntityUid? entity = null,
        string? detail = null) =>
        new(source, reason, entity is { } uid ? [uid] : [], detail);
}

/// <summary>Outcome of one flag, with every trigger collected while evaluating it.</summary>
public sealed record PlayerFlagResult(
    PlayerFlag Flag,
    PlayerStateData Category,
    bool Triggered,
    IReadOnlyList<PlayerFlagTrigger> Triggers,
    bool Display = false)
{
    public static PlayerFlagResult Miss(PlayerFlag flag, PlayerStateData category) =>
        new(flag, category, false, []);
}

/// <summary>Everything the utility found for one entity: the flag layer of the data collection.</summary>
public sealed record PlayerFlagReport(IReadOnlyList<PlayerFlagResult> Results)
{
    public static PlayerFlagReport Empty { get; } = new([]);

    /// <summary>Whether a flag fired.</summary>
    public bool Has(PlayerFlag flag)
    {
        for (var i = 0; i < Results.Count; i++)
        {
            if (Results[i].Flag == flag && Results[i].Triggered)
                return true;
        }

        return false;
    }

    /// <summary>One flag's result, or null when the flag was never evaluated.</summary>
    public PlayerFlagResult? Result(PlayerFlag flag)
    {
        for (var i = 0; i < Results.Count; i++)
        {
            if (Results[i].Flag == flag)
                return Results[i];
        }

        return null;
    }

    /// <summary>The flags belonging to one data block.</summary>
    public PlayerFlagReport ForCategory(PlayerStateData category) =>
        new(Results.Where(result => result.Category == category).ToList());

    public IEnumerable<PlayerFlagResult> Triggered => Results.Where(result => result.Triggered);
}

/// <summary>A check that asks <see cref="RobustaSyndicateDetectorSystem"/> whether an emag landed.</summary>
public sealed record EmaggedCheck : PlayerFlagCheck;

/// <summary>A check for a criminal status security has flagged.</summary>
public sealed record WantedCheck : PlayerFlagCheck;

/// <summary>A check for any recognised antagonist, the detector latches the verdict for the round.</summary>
public sealed record AntagonistCheck : PlayerFlagCheck;
