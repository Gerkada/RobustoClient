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
            var hook = ResolveHook();
            if (hook == null)
            {
                Logger.GetSawmill("RobustaNet").Error($">>> [INIT] {HookTypeName} has no {HookMethodName}, throw aimbot disabled.");
                return true;
            }

            var prefix = new HarmonyMethod(typeof(RobustaThrowPatch).GetMethod(nameof(HandleInputCommandPrefix), BindingFlags.Static | BindingFlags.NonPublic));
            new Harmony("robusta.throw.hardcode").Patch(hook, prefix: prefix);
            Logger.GetSawmill("RobustaNet").Info($">>> [INIT] SUCCESS! Throw aimbot hooked on {HookTypeName}.{HookMethodName}.");
        }
        catch (Exception e) { Logger.GetSawmill("RobustaNet").Error($">>> [INIT] Prepare crashed: {e.Message}"); }
        return true;
    }

    /// <summary>
    /// The upstream input entry point the prefix hangs off, resolved by name so a fork that renames or moves
    /// it fails with a clear log line.
    /// </summary>
    internal static MethodBase? ResolveHook() =>
        Reflect.GetMethod(Reflect.FindType(HookTypeName), HookMethodName, ReflectOptions.Any);

    private const string HookTypeName = "Robust.Client.GameObjects.InputSystem";
    private const string HookMethodName = "HandleInputCommand";

    /// <summary>The bound function name upstream uses for the throw key.</summary>
    private const string ThrowFunctionName = "ThrowItemInHand";

    private static bool _isPatching;

    /// <summary>The last message already rewritten, so a fork that dispatches twice does not re-solve it.</summary>
    private static object? _lastRewritten;

    private static void HandleInputCommandPrefix(object[] __args)
    {
        if (_isPatching || __args is not { Length: >= 3 }) return;
        if (!RobustaConfig.ThrowAimbotEnabled) return;

        // Every gameplay key press lands here.
        if (__args[1] is not BoundKeyFunction function || function.FunctionName != ThrowFunctionName) return;
        if (__args.Length > 3 && __args[3] is bool replay && replay) return;
        if (__args[2] is not ClientFullInputCmdMessage clientMsg) return;

        if (Reflect.Get(clientMsg, "State", ReflectOptions.Any) is not BoundKeyState { } state ||
            state != BoundKeyState.Down)
        {
            return;
        }

        if (ReferenceEquals(clientMsg, _lastRewritten)) return;

        var input = IoCManager.Resolve<IInputManager>();
        if (!RobustaConfig.TargetLockKey.IsDown(input)) return;

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

                Logger.GetSawmill("RobustaNet").Info(planned
                    ? $"[!] Slip throw {slipStatus}: {worldPoint} for {target.Entity}"
                    : $"[!] Slip throw unavailable ({slipStatus}) for {target.Entity}, using the item intercept");
            }

            // A slip item that cannot be placed ahead still gets thrown at the target's intercept point,
            // which slips them on contact. Leaving it at the raw cursor lands it where they no longer are.
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

            var transform = entMan.System<SharedTransformSystem>();
            if (Reflect.Get(clientMsg, "Coordinates", ReflectOptions.Any) is not EntityCoordinates origCoords ||
                !origCoords.IsValid(entMan))
            {
                return;
            }

            var mapId = entMan.GetComponent<TransformComponent>(localPlayer.Value).MapID;
            var newCoords = transform.ToCoordinates(origCoords.EntityId, new MapCoordinates(worldPoint, mapId));

            // Upstream builds the networked message from this one after the prefix runs, so one write
            // reaches both the local handler and the server.
            if (!Reflect.TrySet(clientMsg, "Coordinates", newCoords, ReflectOptions.Any))
            {
                Logger.GetSawmill("RobustaNet").Warning("[!] Throw coordinates could not be rewritten.");
                return;
            }

            _lastRewritten = clientMsg;
            Logger.GetSawmill("RobustaNet").Info($"[!] Aimbot Throw: Target {target.Entity} at {worldPoint}");
        }
        catch (Exception e) { Logger.GetSawmill("RobustaNet").Error($"[CRASH] {e.Message}"); }
        finally { _isPatching = false; }
    }

    /// <summary>
    /// Whether the item can slip people once it is on the floor.
    /// </summary>
    internal static bool IsSlipItem(IEntityManager entMan, EntityUid item)
    {
        return entMan.HasComponent<SlipperyComponent>(item) &&
               entMan.TryGetComponent<StepTriggerComponent>(item, out var step) &&
               step.Active;
    }
}
