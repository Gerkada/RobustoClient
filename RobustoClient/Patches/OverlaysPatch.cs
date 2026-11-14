using System.Collections.Generic;
using System.Reflection;
using Content.Client.Drunk;
using Content.Client.Drugs;
using Content.Client.Eye.Blinding;
using Content.Client.Flash;
using Content.Client.Overlays;
using Content.Client.StatusIcon;
using Content.Shared.Eye.Blinding.Components;
using Content.Shared.Mobs.Components;
using Content.Shared.StatusIcon;
using HarmonyLib;
using Robust.Client.Graphics;
using Robust.Client.Player;

namespace RobustoClient.Patches;

/// <summary>
/// Skips the upstream full-screen effects when <see cref="RobustaConfig.DisableVisualOverlays"/> is set.
/// </summary>
/// <remarks>
/// Hooks <c>BeforeDraw(in OverlayDrawArgs)</c>, which Clyde calls as the per-overlay skip hook in both
/// draw loops immediately after resetting draw state. Returning false skips one overlay without leaking
/// its shader or transform into the next.
/// </remarks>
[HarmonyPatch]
internal static class OverlaysPatch
{
    private static readonly Type[] Suppressed =
    [
        typeof(FlashOverlay),
        typeof(BlindOverlay),
        typeof(BlurryVisionOverlay),
        typeof(DrunkOverlay),
        typeof(RainbowOverlay)
    ];

    [HarmonyTargetMethods]
    private static IEnumerable<MethodBase> TargetMethods()
    {
        var signature = new[] { typeof(OverlayDrawArgs).MakeByRefType() };
        int resolved = 0;

        foreach (var type in Suppressed)
        {
            var method = AccessTools.Method(type, "BeforeDraw", signature);
            if (method == null)
            {
                MarseyLogger.Warn(
                    $"[Overlays] BeforeDraw not found on {type.Name}; that overlay will not be suppressed.");
                continue;
            }

            resolved++;
            yield return method;
        }

        // A count below the array length means an upstream rename disabled part of this patch.
        MarseyLogger.Info($"[Overlays] Suppressing {resolved}/{Suppressed.Length} visual overlays.");
    }

    [HarmonyPrefix]
    private static bool Prefix(Overlay __instance, ref bool __result)
    {
        if (!RobustaConfig.DisableVisualOverlays)
            return true;

        // BlindOverlay clears _lightManager.Enabled in Draw and relies on BeforeDraw to restore it.
        if (__instance is BlindOverlay)
            RestoreBlindLightSetup();

        __result = false;
        return false;
    }

    private static void RestoreBlindLightSetup()
    {
        if (SelfOverlayFilter.LocalPlayer is not { } player)
            return;

        var entities = IoCManager.Resolve<IEntityManager>();
        if (!entities.TryGetComponent<BlindableComponent>(player, out var blind) || !blind.LightSetup)
            return;

        IoCManager.Resolve<ILightManager>().Enabled = true;
        blind.LightSetup = false;
        blind.GraceFrame = false;
    }
}

/// <summary>State the self-overlay patches share: which entity is the local player.</summary>
internal static class SelfOverlayFilter
{
    /// <summary>Local player entity, or null when no session is attached.</summary>
    internal static EntityUid? LocalPlayer =>
        IoCManager.Resolve<IPlayerManager>().LocalSession?.AttachedEntity;
}

/// <summary>Skips the health bar the local player would draw on itself while the Health ESP switch is on.</summary>
[HarmonyPatch]
internal static class SelfHealthBarPatch
{
    [HarmonyTargetMethods]
    private static IEnumerable<MethodBase> TargetMethods()
    {
        var method = AccessTools.Method(typeof(EntityHealthBarOverlay), "CalcProgress");
        if (method == null)
        {
            MarseyLogger.Warn(
                "[Overlays] EntityHealthBarOverlay.CalcProgress not found; the health bar will draw on the local player.");
            yield break;
        }

        yield return method;
    }

    [HarmonyPrefix]
    private static bool Prefix(EntityUid uid, ref (float ratio, bool inCrit)? __result)
    {
        if (!RobustaConfig.Esp.Health.Enabled || uid != SelfOverlayFilter.LocalPlayer)
            return true;

        // A null progress makes the upstream draw loop skip this entity.
        __result = null;
        return false;
    }
}

/// <summary>Clears the status icons the local player would draw above its own head.</summary>
/// <remarks>
/// Hooks the aggregation call rather than one of the per-component handlers, which each subscribe to
/// <c>GetStatusIconsEvent</c> separately and run in no guaranteed order.
/// </remarks>
[HarmonyPatch]
internal static class SelfStatusIconPatch
{
    [HarmonyTargetMethods]
    private static IEnumerable<MethodBase> TargetMethods()
    {
        var method = AccessTools.Method(typeof(StatusIconSystem), "GetStatusIcons");
        if (method == null)
        {
            MarseyLogger.Warn(
                "[Overlays] StatusIconSystem.GetStatusIcons not found; status icons will draw on the local player.");
            yield break;
        }

        yield return method;
    }

    [HarmonyPostfix]
    private static void Postfix(EntityUid uid, List<StatusIconData> __result)
    {
        if (!RobustaConfig.Esp.Icons.Enabled || uid != SelfOverlayFilter.LocalPlayer)
            return;

        __result.Clear();
    }
}
