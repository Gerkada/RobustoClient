using System;
using System.Collections.Generic;
using Content.Shared.Weapons.Ranged.Components;
using Robust.Shared.GameObjects;
using RobustoClient.Systems.Abstract;
using RobustoClient.Systems.PlayerState;

namespace RobustoClient.Systems.Macros;

/// <summary>
/// Runs the selectable macros. A macro is a named action the Binds picker offers next to the
/// <c>robusta.*</c> console commands, selecting one and firing the bound key runs it.
/// </summary>
/// <remarks> Currently only inventory related quick equips by type, eventually will show more </remarks>
public sealed partial class RobustaMacroSystem : EntitySystem
{
    /// <summary>
    /// One selectable macro: the id a bind or radial sector stores, the label the picker shows, the
    /// group it is filed under, and the action that runs it.
    /// </summary>
    internal sealed record MacroDefinition(
        string Id,
        string Label,
        string Category,
        Func<RobustaMacroSystem, bool> Execute);

    [Dependency] private readonly ItemUtilitySystem _items = default!;

    /// <summary>
    /// Equips the item with the highest <paramref name="score"/> among everything carrying
    /// <typeparamref name="TComp"/>, searching hands, slots, storage and the vicinity.
    /// </summary>
    public bool EquipBest<TComp>(Func<TComp, float> score) where TComp : IComponent
    {
        var scored = new List<(EntityUid Entity, float Score)>();

        foreach (var candidate in _items.FindItemsByPredicate(uid => HasComp<TComp>(uid)))
        {
            if (TryComp<TComp>(candidate.Entity, out var comp))
                scored.Add((candidate.Entity, score(comp)));
        }

        return Equip(SelectBest(scored), typeof(TComp).Name);
    }

    /// <summary>
    /// Equips the gun with the most available shots. Reads ammo through upstream's own count event
    /// rather than inspecting the gun's components, so it works for every ammo provider without
    /// touching <see cref="GunComponent"/> fields, which are access-restricted to the gun system.
    /// </summary>
    public bool EquipBestRanged() => EquipBestGunWithShots(requireStamina: false, "ranged weapon");

    /// <summary>
    /// Equips the gun with the most available shots among those whose projectile carries stamina
    /// damage, which is what makes a disabler a disabler.
    /// </summary>
    public bool EquipBestDisabler() => EquipBestGunWithShots(requireStamina: true, "disabler");

    private bool EquipBestGunWithShots(bool requireStamina, string description)
    {
        var state = EntityManager.System<PlayerStateUtility>();
        var scored = new List<(EntityUid Entity, float Score)>();

        foreach (var candidate in _items.FindItemsByPredicate(uid => HasComp<GunComponent>(uid)))
        {
            bool stamina = state.GetProjectileStamina(candidate.Entity) > 0f;
            var ammo = state.ReadAmmunition(candidate.Entity);
            int shots = ammo.Shots ?? ammo.HitsLeft ?? 0;

            // Leaves a trail when a gun is present but scores nothing, which is the case that used
            // to fail silently.
            MarseyLogger.Debug(
                $"[Macro] {description}: {ToPrettyString(candidate.Entity)} stamina={stamina} " +
                $"shots={shots} capacity={ammo.Capacity} hitsLeft={ammo.HitsLeft}");

            if (stamina != requireStamina || shots <= 0)
                continue;

            scored.Add((candidate.Entity, shots));
        }

        return Equip(SelectBest(scored), description);
    }

    /// <summary>
    /// Highest score wins, ties keep the earlier candidate, which is the one the item search
    /// already ranked first.
    /// </summary>
    internal static EntityUid? SelectBest(IReadOnlyList<(EntityUid Entity, float Score)> scored)
    {
        EntityUid? best = null;
        float bestScore = 0f;

        foreach (var (entity, score) in scored)
        {
            if (score <= 0f || (best != null && score <= bestScore))
                continue;

            best = entity;
            bestScore = score;
        }

        return best;
    }

    /// <summary>
    /// Equips exactly <paramref name="best"/>. Matching the one entity is what makes the best item
    /// win: <see cref="ItemUtilitySystem.TryEquipByPredicate"/> otherwise ranks by source and stack.
    /// </summary>
    private bool Equip(EntityUid? best, string description) =>
        best is { } uid && _items.TryEquipByPredicate(entity => entity == uid, description);
}
