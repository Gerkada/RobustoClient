using System.Collections.Generic;
using System.Reflection.Emit;
using Content.Client.Weapons.Ranged.Systems;
using Content.Shared.CombatMode;
using Content.Shared.Hands.Components;
using Content.Shared.Hands.EntitySystems;
using Content.Shared.Weapons.Ranged.Components;
using Content.Shared.Weapons.Ranged.Events;
using HarmonyLib;
using Robust.Client.GameObjects;
using Robust.Client.Input;
using Robust.Client.Player;
using Robust.Shared.GameObjects;
using Robust.Shared.Input;
using Robust.Shared.IoC;
using Robust.Shared.Map;
using Robust.Shared.Timing;
using RobustoClient.Systems;
using RobustoClient.Systems.Aim;

namespace RobustoClient.Patches;

/// <summary>
/// Redirects gun shots at the solved intercept point and names the target so the server can hit it.
/// </summary>
/// <remarks>
/// The solve happens in the prefix so a blocked shot can suppress the whole update, which is what prevents
/// the request from being sent at all. Each <see cref="RequestShootEvent"/> is a single shot server-side, so
/// dropping it stops that shot without leaving the server firing.
/// </remarks>
[HarmonyPatch(typeof(GunSystem), nameof(GunSystem.Update))]
public class GunUpdatePatch
{
    private static IEntityManager? _entMan;
    private static IInputManager? _input;
    private static IPlayerManager? _player;
    private static IGameTiming? _timing;
    private static RobustaAimSystem? _aim;
    private static RobustaPredictionSystem? _prediction;

    private static ShotPlan? _plan;

    [HarmonyPrefix]
    private static bool Prefix()
    {
        _plan = null;

        if (!RobustaConfig.RangedAimbotEnabled)
        {
            _prediction?.ForgetPlan();
            return true;
        }

        _entMan ??= IoCManager.Resolve<IEntityManager>();
        _input ??= IoCManager.Resolve<IInputManager>();
        _player ??= IoCManager.Resolve<IPlayerManager>();
        _timing ??= IoCManager.Resolve<IGameTiming>();
        _aim ??= _entMan.System<RobustaAimSystem>();
        _prediction ??= _entMan.System<RobustaPredictionSystem>();

        // Upstream bails out of its own Update on this, so keep the previous predicted tick's plan for the overlay.
        if (!_timing.IsFirstTimePredicted) return true;

        // Cleared on each predicted tick, so the overlay's line-of-sight marker only reflects a target acquired now.
        _prediction.ForgetPlan();

        if (_player.LocalSession?.AttachedEntity is not { } localPlayer) return true;

        // Upstream returns early outside combat mode, so no shot can happen and there is nothing to solve.
        if (!_entMan.TryGetComponent<CombatModeComponent>(localPlayer, out var combat) || !combat.IsInCombatMode)
            return true;

        if (!_entMan.TryGetComponent<HandsComponent>(localPlayer, out var hands)) return true;

        var activeItem = _entMan.System<SharedHandsSystem>().GetActiveItem((localPlayer, hands));
        if (activeItem is not { } gun || !gun.IsValid()) return true;

        if (!_entMan.TryGetComponent<GunComponent>(gun, out var gunComp)) return true;

        if (!_aim.TryAcquireAim(_input.MouseScreenPosition, out var aimResult)) return true;

        var profile = _prediction.Profile(gun);
        if (!RobustaConfig.UsePrediction)
            profile = profile with { Speed = 0f };

        if (!_prediction.TrySolveGun(localPlayer, aimResult.Entity, profile, out var plan)) return true;

        _plan = plan;

        // The plan is solved every tick so the overlay can show it, but only suppress the update
        // when the gun would actually send a shoot request; letting idle ticks run keeps
        // battery/ballistic updates and RequestStopShootEvent intact.
        if (plan.Blocked && !RobustaConfig.AimFireWhenBlocked &&
            gunComp.NextFire <= _timing.CurTime)
        {
            var useKey = gunComp.UseKey ? EngineKeyFunctions.Use : EngineKeyFunctions.UseSecondary;
            if (_entMan.System<InputSystem>().CmdStates.GetState(useKey) == BoundKeyState.Down ||
                gunComp.BurstActivated)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Replaces the mouse coordinate on the shoot request with the solved aim point.</summary>
    public static NetCoordinates Patch(NetCoordinates originalCoords)
    {
        if (_plan is not { } plan) return originalCoords;

        _entMan ??= IoCManager.Resolve<IEntityManager>();
        return _entMan.GetNetCoordinates(new EntityCoordinates(plan.Target, plan.LocalOffset));
    }

    /// <summary>
    /// Names the aimed entity, which is what lets projectiles and rays reach a downed body: upstream cancels
    /// both against <c>RequireProjectileTargetComponent</c> unless the shot's target is that entity.
    /// </summary>
    public static void SetShootTarget(RequestShootEvent ev)
    {
        if (_plan is not { } plan) return;

        var entMan = _entMan ??= IoCManager.Resolve<IEntityManager>();
        ev.Target = entMan.GetNetEntity(plan.Target);
    }

    // A transpiler rewrites the method body's IL instead of wrapping the method: Harmony hands it the
    // instruction list and emits whatever comes back. GunSystem.Update builds its RequestShootEvent inline,
    // out of reach of the prefix, so this scans for the stfld that stores Coordinates and splices in two
    // calls: Patch swaps in the solved aim point, then SetShootTarget names the entity on the same event.
    [HarmonyTranspiler]
    private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
    {
        var requestShootCoordinatesField = AccessTools.Field(typeof(RequestShootEvent), nameof(RequestShootEvent.Coordinates));
        var patchMethod = AccessTools.Method(typeof(GunUpdatePatch), nameof(Patch));
        var targetMethod = AccessTools.Method(typeof(GunUpdatePatch), nameof(SetShootTarget));

        var codes = new List<CodeInstruction>(instructions);
        var index = codes.FindIndex(c => c.StoresField(requestShootCoordinatesField));
        if (index == -1) return codes;

        codes.Insert(index, new CodeInstruction(OpCodes.Call, patchMethod));

        // stfld leaves the event reference on the stack, so Dup lets us write Target without disturbing
        // the remaining object initializer.
        codes.Insert(index + 2, new CodeInstruction(OpCodes.Dup));
        codes.Insert(index + 3, new CodeInstruction(OpCodes.Call, targetMethod));

        return codes;
    }
}
