using System;
using System.Numerics;
using System.Reflection;
using HarmonyLib;
using Robust.Shared.IoC;
using Robust.Shared.Map;
using Robust.Client.Input;
using Robust.Client.Player;
using Robust.Shared.GameObjects;
using Robust.Shared.Input;
using Content.Shared.Hands.Components;
using Content.Shared.Hands.EntitySystems;
using Content.Shared.Slippery;
using Content.Shared.StepTrigger.Components;
using RobustoClient.Systems;
using RobustoClient.Reflection;
using Robust.Shared.Log;
using Robust.Client.Graphics;

namespace RobustoClient.Patches;

[HarmonyPatch]
public static class RobustaThrowPatch
{
    public static void Dummy() { }

    [HarmonyTargetMethod]
    public static MethodBase TargetMethod() => typeof(RobustaThrowPatch).GetMethod("Dummy")!;

    [HarmonyPrefix]
    public static void DummyPrefix() { }

    [HarmonyPrepare]
    public static bool Prepare()
    {
        try
        {
            var harmony = new Harmony("robusta.throw.hardcode");
            var prefix = new HarmonyMethod(typeof(RobustaThrowPatch).GetMethod(nameof(DispatchInputPrefix), BindingFlags.Static | BindingFlags.NonPublic));

            var inputSystemType = Type.GetType("Robust.Client.GameObjects.InputSystem, RobustClient");
            if (inputSystemType == null)
            {
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    inputSystemType = asm.GetType("Robust.Client.GameObjects.InputSystem");
                    if (inputSystemType != null) break;
                }
            }

            if (inputSystemType != null)
            {
                var dispatchMethod = inputSystemType.GetMethod("DispatchInputCommand", BindingFlags.Instance | BindingFlags.NonPublic);
                if (dispatchMethod != null)
                {
                    harmony.Patch(dispatchMethod, prefix: prefix);
                    Logger.GetSawmill("RobustaNet").Info(">>> [INIT] SUCCESS! InputSystem Aimbot deployed.");
                }
            }
        }
        catch (Exception e) { Logger.GetSawmill("RobustaNet").Error($">>> [INIT] Prepare crashed: {e.Message}"); }
        return true; 
    }

    private static bool _isPatching = false;

    /// <summary>The bound function name upstream uses for the throw key.</summary>
    private const string ThrowFunctionName = "ThrowItemInHand";

    private static void DispatchInputPrefix(object[] __args) 
    {
        if (_isPatching || __args == null || __args.Length < 2) return;
        if (!RobustaConfig.ThrowAimbotEnabled) return;

        var input = IoCManager.Resolve<IInputManager>();
        if (!RobustaConfig.TargetLockKey.IsDown(input)) return;

        var clientMsg = __args[0]; 
        var fullMsg = __args[1];   
        if (clientMsg == null || fullMsg == null) return;

        // Start the exploit only at the moment of click (press)
        var stateObj = Reflect.Get(clientMsg, "State", ReflectOptions.Any);
        if (stateObj == null || stateObj.ToString() != "Down") return;

        // Only redirect an actual throw.
        if (!IsThrowCommand(clientMsg)) return;

        var playerMan = IoCManager.Resolve<IPlayerManager>();
        var localPlayer = playerMan.LocalEntity;
        if (localPlayer == null) return;

        var entMan = IoCManager.Resolve<IEntityManager>();

        _isPatching = true;
        try
        {
            var aim = entMan.System<RobustaAimSystem>();
            if (!aim.TryAcquireAim(input.MouseScreenPosition, out var target)) return;

            var prediction = entMan.System<RobustaPredictionSystem>();
            var throwSpeed = RobustaConfig.DefaultThrowSpeed;

            EntityUid? heldItem = null;
            if (entMan.TryGetComponent<HandsComponent>(localPlayer.Value, out var hands))
            {
                throwSpeed = hands.BaseThrowspeed;

                if (entMan.System<SharedHandsSystem>().TryGetActiveItem((localPlayer.Value, hands), out var active))
                    heldItem = active;
            }

            var worldPoint = Vector2.Zero;
            EntityUid? blocker = null;
            var planned = false;

            if (RobustaConfig.SlipThrowEnabled && heldItem is { } item && IsSlipItem(entMan, item))
            {
                planned = prediction.TrySolveSlipThrow(localPlayer.Value, target.Entity, item, out worldPoint, out blocker, out var slipStatus);

                if (!planned)
                {
                    Logger.GetSawmill("RobustaNet").Debug($"[!] Slip throw aborted: {slipStatus}");
                    if (!ShouldContinueBlockedSlip(slipStatus, RobustaConfig.AimFireWhenBlocked))
                        return;
                    planned = true;
                }
                else
                {
                    Logger.GetSawmill("RobustaNet").Debug($"[!] Slip throw placed at {worldPoint} for {target.Entity}");
                }
            }

            if (!planned &&
                !prediction.TrySolveThrow(localPlayer.Value, target.Entity, throwSpeed, out worldPoint, out blocker))
            {
                return;
            }

            // A blocked throw is left at the untouched mouse coordinate rather than dropping the item.
            if (blocker != null && !RobustaConfig.AimFireWhenBlocked)
            {
                Logger.GetSawmill("RobustaNet").Info($"[!] Throw blocked by {blocker}");
                return;
            }

            var transformSystem = entMan.System<SharedTransformSystem>();
            var eyeManager = IoCManager.Resolve<IEyeManager>();

            var origCoordsObj = Reflect.Get(clientMsg, "Coordinates", ReflectOptions.Any);
            if (origCoordsObj == null) return;

            var origEntityIdObj = Reflect.GetFirst(origCoordsObj, "EntityId", "NetEntity");
            if (origEntityIdObj == null) return;

            var origEntityId = (EntityUid)origEntityIdObj;

            var newEntityCoords = transformSystem.ToCoordinates(origEntityId, new MapCoordinates(worldPoint, entMan.GetComponent<TransformComponent>(localPlayer.Value).MapID));
            var newScreenCoords = eyeManager.CoordinatesToScreen(newEntityCoords); 
            var newNetCoords = entMan.GetNetCoordinates(newEntityCoords);

            bool c1 = Reflect.TrySet(clientMsg, "Coordinates", newEntityCoords, ReflectOptions.Any);
            bool c2 = Reflect.TrySet(clientMsg, "ScreenCoordinates", newScreenCoords, ReflectOptions.Any);
            bool c3 = Reflect.TrySet(fullMsg, "Coordinates", newNetCoords, ReflectOptions.Any);
            bool c4 = Reflect.TrySet(fullMsg, "ScreenCoordinates", newScreenCoords, ReflectOptions.Any);

            if (c1 && c3)
            {
                __args[0] = clientMsg;
                __args[1] = fullMsg;
                Logger.GetSawmill("RobustaNet").Info($"[!] Aimbot Throw: Target {target.Entity}");
            }
        }
        catch (Exception e) { Logger.GetSawmill("RobustaNet").Error($"[CRASH] {e.Message}"); }
        finally { _isPatching = false; }
    }

    internal static bool ShouldContinueBlockedSlip(RobustoClient.Systems.Aim.SlipPlanStatus status, bool fireWhenBlocked) =>
        status == RobustoClient.Systems.Aim.SlipPlanStatus.Blocked && fireWhenBlocked;

    /// <summary>
    /// Whether this input command is the throw key, resolved through the input manager's bind map so the
    /// check follows the player's own keybind rather than a hardcoded function name.
    /// </summary>
    private static bool IsThrowCommand(object clientMsg)
    {
        if (clientMsg is not InputCmdMessage msg)
            return false;

        var input = IoCManager.Resolve<IInputManager>();
        return input.NetworkBindMap.TryGetKeyFunction(msg.InputFunctionId, out var function) &&
               function.FunctionName == ThrowFunctionName;
    }

    /// <summary>
    /// Whether the item can slip people once it is on the floor.
    /// </summary>
    private static bool IsSlipItem(IEntityManager entMan, EntityUid item)
    {
        return entMan.HasComponent<SlipperyComponent>(item) &&
               entMan.TryGetComponent<StepTriggerComponent>(item, out var step) &&
               step.Active;
    }
}
