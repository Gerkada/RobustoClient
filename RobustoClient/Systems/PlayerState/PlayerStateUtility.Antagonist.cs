using System.Collections;
using Content.Shared.Actions.Components;
using Content.Shared.Changeling.Components;
using Content.Shared.Store.Components;
using Robust.Shared.GameObjects;
using RobustoClient.Reflection;

namespace RobustoClient.Systems.PlayerState;

/// <summary>
/// The antagonist blocks: the recognised ghost role with its changeling detail, and the syndicate
/// block (uplink, contraband, implants, gear and an emag).
/// </summary>
public sealed partial class PlayerStateUtility
{
    #region Syndicate

    private IPlayerData? BuildSyndicate(EntityUid entity, PlayerFlagReport flags)
    {
        var detector = EntityManager.System<RobustaSyndicateDetectorSystem>();
        var status = detector.CheckPlayerStatus(entity);
        var uplink = detector.GetUplinkStatus(entity);
        var emagged = detector.IsEmagged(entity);
        var hasGear = flags.Has(PlayerFlag.HasSyndicateGear);
        var hasDangerousChems = flags.Has(PlayerFlag.HasDangerousChemicals);

        if (!ShouldBuildSyndicate(uplink, status.Contra, status.Implants.Count, hasGear, emagged,
                hasDangerousChems))
        {
            return null;
        }

        // The flag already walked the container tree, so the gear list comes from its triggers
        // instead of a second scan.
        var gear = flags.Result(PlayerFlag.HasSyndicateGear)?.Triggers
            .SelectMany(trigger => trigger.Entities)
            .Distinct()
            .Select(EntityName)
            .ToList() ?? [];

        return new SyndicateData(
            uplink,
            status.Contra,
            emagged,
            status.Implants,
            gear,
            hasGear,
            hasGear || hasDangerousChems);
    }

    /// <summary>
    /// Whether the entity has anything to say under the syndicate header. Every signal that raises the
    /// radar's tag has to be in here, or the tag appears with no section explaining it. Pure so it is
    /// testable.
    /// </summary>
    internal static bool ShouldBuildSyndicate(
        string? uplinkStatus,
        bool hasContraband,
        int implantCount,
        bool hasSyndicateGear,
        bool emagged,
        bool hasDangerousChemicals) =>
        uplinkStatus != null ||
        hasContraband ||
        implantCount > 0 ||
        hasSyndicateGear ||
        emagged ||
        hasDangerousChemicals;

    #endregion

    #region Antagonist

    private IPlayerData? BuildAntagonist(EntityUid entity, PlayerFlagReport flags)
    {
        var detector = EntityManager.System<RobustaSyndicateDetectorSystem>();
        if (detector.DetectAntagonist(entity) is not { } kind)
            return null;

        return new AntagonistData(
            kind,
            detector.IsAntagonistRetained(entity),
            detector.IsAntagonistHedged(entity),
            ScanChangeling(entity));
    }

    #endregion

    #region Changeling

    /// <summary>What the panel can say about a changeling, or null when nothing points that way.</summary>
    private ChangelingData? ScanChangeling(EntityUid entity)
    {
        var detector = EntityManager.System<RobustaSyndicateDetectorSystem>();
        var triggers = detector.GetChangelingTriggers(entity);
        if (triggers.Count == 0)
            return null;

        return new ChangelingData(
            triggers,
            GrantedActionIds(entity).ToList(),
            HasComp<ChangelingFleshClothingAbilityComponent>(entity),
            ReadChangelingDna(entity),
            ReadHorrorRemaining(entity));
    }

    /// <summary>Prototype ids of every action the entity holds.</summary>
    private IEnumerable<string> GrantedActionIds(EntityUid entity)
    {
        if (!TryComp<ActionsComponent>(entity, out var actions))
            yield break;

        foreach (var action in actions.Actions)
        {
            if (TryComp<MetaDataComponent>(action, out var meta) && meta.EntityPrototype is { } prototype)
                yield return prototype.ID;
        }
    }

    /// <summary>
    /// DNA left in the store, read through reflection because <c>FixedPoint2</c> binds differently
    /// across forks.
    /// </summary>
    private int? ReadChangelingDna(EntityUid entity)
    {
        if (!TryComp<StoreComponent>(entity, out var store))
            return null;

        if (RobustaSyndicateDetectorSystem.StoreBalance(store) is not { } balance)
            return null;

        foreach (DictionaryEntry entry in balance)
        {
            if (entry.Key?.ToString() != RobustaSyndicateDetectorSystem.ChangelingCurrency)
                continue;

            if (ReflectConvert.TryToDouble(entry.Value, out var amount))
                return (int)Math.Round(amount);
        }

        return null;
    }

    /// <summary>Time left in horror form, or null when the entity is not in it.</summary>
    private TimeSpan? ReadHorrorRemaining(EntityUid entity)
    {
        // The horror component is upstream-only, so it is resolved by name and read reflectively.
        var horror = EntityManager.System<RobustaSyndicateDetectorSystem>()
            .GetComponentNamed(entity, RobustaSyndicateDetectorSystem.ChangelingHorrorComponentName);

        if (horror == null ||
            !Reflect.TryGet(horror, "EndTime", out var endTime, RobustaSyndicateDetectorSystem.OptionalMember) ||
            endTime is not TimeSpan end)
        {
            return null;
        }

        return end - _timing.CurTime;
    }
    #endregion

}
