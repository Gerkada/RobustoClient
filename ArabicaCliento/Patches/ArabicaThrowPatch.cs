using System;
using System.Reflection;
using HarmonyLib;
using Robust.Shared.IoC;
using Robust.Shared.Map;
using Robust.Client.Input;
using Robust.Client.Player;
using Robust.Shared.GameObjects;
using Content.Shared.CombatMode;
using ArabicaCliento.Systems;
using Robust.Shared.Log;
using Robust.Client.Graphics;

namespace ArabicaCliento.Patches;

[HarmonyPatch]
public static class ArabicaThrowPatch
{
    public static void Dummy() { }

    [HarmonyTargetMethod]
    public static MethodBase TargetMethod() => typeof(ArabicaThrowPatch).GetMethod("Dummy")!;

    [HarmonyPrefix]
    public static void DummyPrefix() { }

    [HarmonyPrepare]
    public static bool Prepare()
    {
        try
        {
            var harmony = new Harmony("arabica.throw.hardcode");
            var prefix = new HarmonyMethod(typeof(ArabicaThrowPatch).GetMethod(nameof(DispatchInputPrefix), BindingFlags.Static | BindingFlags.NonPublic));

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
                    Logger.GetSawmill("ArabicaNet").Info(">>> [INIT] SUCCESS! InputSystem Aimbot deployed.");
                }
            }
        }
        catch (Exception e) { Logger.GetSawmill("ArabicaNet").Error($">>> [INIT] Prepare crashed: {e.Message}"); }
        return true; 
    }

    private static bool _isPatching = false;

    private static void DispatchInputPrefix(object[] __args) 
    {
        if (_isPatching || __args == null || __args.Length < 2) return;
        if (!ArabicaConfig.ThrowAimbotEnabled) return;

        var input = IoCManager.Resolve<IInputManager>();
        if (!input.IsKeyDown(ArabicaConfig.TargetLockKey)) return;

        var clientMsg = __args[0]; 
        var fullMsg = __args[1];   
        if (clientMsg == null || fullMsg == null) return;

        // Запускаем взлом только в момент нажатия (клика)
        var stateObj = GetValue(clientMsg, "State");
        if (stateObj == null || stateObj.ToString() != "Down") return;

        var playerMan = IoCManager.Resolve<IPlayerManager>();
        var localPlayer = playerMan.LocalEntity;
        if (localPlayer == null) return;

        var entMan = IoCManager.Resolve<IEntityManager>();
        if (!entMan.TryGetComponent<CombatModeComponent>(localPlayer.Value, out var combat) || !combat.IsInCombatMode) return;

        _isPatching = true;
        try
        {
            var aim = entMan.System<ArabicaAimSystem>();
            var target = aim.GetThrowAimTarget(input.MouseScreenPosition);
            if (target == null) return;

            var transformSystem = entMan.System<SharedTransformSystem>();
            var eyeManager = IoCManager.Resolve<IEyeManager>();
            var targetMapPos = target.Value.Position;

            var origCoordsObj = GetValue(clientMsg, "Coordinates");
            if (origCoordsObj == null) return;

            var origEntityIdObj = GetValue(origCoordsObj, "EntityId") ?? GetValue(origCoordsObj, "NetEntity");
            if (origEntityIdObj == null) return;

            var origEntityId = (EntityUid)origEntityIdObj;

            var newEntityCoords = transformSystem.ToCoordinates(origEntityId, targetMapPos);
            var newScreenCoords = eyeManager.CoordinatesToScreen(newEntityCoords); 
            var newNetCoords = entMan.GetNetCoordinates(newEntityCoords);

            bool c1 = ForceSetValue(clientMsg, "Coordinates", newEntityCoords);
            bool c2 = ForceSetValue(clientMsg, "ScreenCoordinates", newScreenCoords);
            bool c3 = ForceSetValue(fullMsg, "Coordinates", newNetCoords);
            bool c4 = ForceSetValue(fullMsg, "ScreenCoordinates", newScreenCoords);

            if (c1 && c3)
            {
                __args[0] = clientMsg;
                __args[1] = fullMsg;
                Logger.GetSawmill("ArabicaNet").Info($"[!] Aimbot Throw: Target {target.Value.Entity}");
            }
        }
        catch (Exception e) { Logger.GetSawmill("ArabicaNet").Error($"[CRASH] {e.Message}"); }
        finally { _isPatching = false; }
    }

    static object? GetValue(object obj, string name)
    {
        var type = obj.GetType();
        var prop = type.GetProperty(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        if (prop != null) return prop.GetValue(obj);
        
        var field = type.GetField(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        if (field != null) return field.GetValue(obj);
        
        return null;
    }

    static bool ForceSetValue(object obj, string name, object value)
    {
        var type = obj.GetType();
        
        var prop = type.GetProperty(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        if (prop != null && prop.CanWrite) {
            prop.SetValue(obj, value); return true;
        }
        
        var field = type.GetField(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        if (field != null) {
            field.SetValue(obj, value); return true;
        }
        
        var backingField = type.GetField($"<{name}>k__BackingField", BindingFlags.NonPublic | BindingFlags.Instance);
        if (backingField != null) {
            backingField.SetValue(obj, value); return true;
        }
        
        return false;
    }
}