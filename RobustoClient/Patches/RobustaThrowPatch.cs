using System;
using System.Reflection;
using HarmonyLib;
using Robust.Shared.IoC;
using Robust.Shared.Map;
using Robust.Client.Input;
using Robust.Client.Player;
using Robust.Shared.GameObjects;
using Content.Shared.CombatMode;
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

        var playerMan = IoCManager.Resolve<IPlayerManager>();
        var localPlayer = playerMan.LocalEntity;
        if (localPlayer == null) return;

        var entMan = IoCManager.Resolve<IEntityManager>();
        if (!entMan.TryGetComponent<CombatModeComponent>(localPlayer.Value, out var combat) || !combat.IsInCombatMode) return;

        _isPatching = true;
        try
        {
            var aim = entMan.System<RobustaAimSystem>();
            var target = aim.GetThrowAimTarget(input.MouseScreenPosition);
            if (target == null) return;

            var transformSystem = entMan.System<SharedTransformSystem>();
            var eyeManager = IoCManager.Resolve<IEyeManager>();
            var targetMapPos = target.Value.Position;

            var origCoordsObj = Reflect.Get(clientMsg, "Coordinates", ReflectOptions.Any);
            if (origCoordsObj == null) return;

            var origEntityIdObj = Reflect.GetFirst(origCoordsObj, "EntityId", "NetEntity");
            if (origEntityIdObj == null) return;

            var origEntityId = (EntityUid)origEntityIdObj;

            var newEntityCoords = transformSystem.ToCoordinates(origEntityId, targetMapPos);
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
                Logger.GetSawmill("RobustaNet").Info($"[!] Aimbot Throw: Target {target.Value.Entity}");
            }
        }
        catch (Exception e) { Logger.GetSawmill("RobustaNet").Error($"[CRASH] {e.Message}"); }
        finally { _isPatching = false; }
    }
}
