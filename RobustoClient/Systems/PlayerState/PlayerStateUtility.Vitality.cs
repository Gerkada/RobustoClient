using Content.Shared.Mobs;
using Content.Shared.Mobs.Components;
using Content.Shared.Mobs.Systems;
using Content.Shared.Silicons.Borgs.Components;
using Robust.Shared.GameObjects;

namespace RobustoClient.Systems.PlayerState;

/// <summary>Vitality block: whether the entity is a mob, and its life state.</summary>
public sealed partial class PlayerStateUtility
{
    private IPlayerData? BuildVitality(EntityUid entity, PlayerFlagReport flags)
    {
        if (!TryComp<MobStateComponent>(entity, out var mobState))
            return null;

        // CurrentState is a field upstream and a property on Goob, so read it through the mob-state
        // system, whose own assembly resolves the member correctly on either build.
        var mobStateSystem = EntityManager.System<MobStateSystem>();
        var state = mobStateSystem.IsDead(entity, mobState) ? MobState.Dead
            : mobStateSystem.IsCritical(entity, mobState) ? MobState.Critical
            : mobStateSystem.IsInvalidState(entity, mobState) ? MobState.Invalid
            : MobState.Alive;

        return new VitalityData(
            state,
            flags.Has(PlayerFlag.HasMind),
            HasComp<BorgChassisComponent>(entity));
    }
}
