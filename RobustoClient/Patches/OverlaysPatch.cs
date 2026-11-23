using System.Reflection;
using Content.Shared.Eye.Blinding.Components;
using Content.Shared.StatusIcon;
using HarmonyLib;
using Robust.Client.Graphics;
using Robust.Client.Player;
using RobustoClient.Reflection;

namespace RobustoClient.Patches;

/// <summary>
/// Skips the upstream full-screen effects when <see cref="RobustaConfig.DisableVisualOverlays"/> is set.
/// </summary>
/// <remarks>
/// Filters the overlay list Clyde is about to draw rather than patching each effect type, so one hook
/// covers every space. <see cref="FilterNames"/> is the explicit list.
/// </remarks>
[HarmonyPatch]
internal static class OverlaysPatch
{
    /// <summary>
    /// Overlays to filter. Add a name here to hide a new effect.
    /// </summary>
    /// <remarks>
    /// Simple names, resolved through <see cref="Reflect.FindTypeByName"/>. A fork or an older upstream
    /// may not ship one of these, and a name that does not resolve is simply skipped.
    /// </remarks>
    private static readonly string[] FilterNames =
    [
        "FlashOverlay",
        "BlindOverlay",
        "BlurryVisionOverlay",
        "DrunkOverlay",
        "RainbowOverlay",
        "DrowsinessOverlay",
        "BlackAndWhiteOverlay",
        "NoirOverlay",
        "NightVisionOverlay",
        "ImageOverlay" // Paints a texture over the viewport.
    ];

    private static readonly Dictionary<Type, bool> Filtered = new();

    [HarmonyTargetMethods]
    private static IEnumerable<MethodBase> TargetMethods()
    {
        // Clyde is internal, so the type is resolved by name.
        var method = Reflect.GetMethod(
            Reflect.FindType("Robust.Client.Graphics.Clyde.Clyde"), 
            "GetOverlaysForSpace", ReflectOptions.Any);

        if (method == null)
        {
            MarseyLogger.Warn("[Overlays] Clyde.GetOverlaysForSpace not found; visual overlays will not be suppressed.");
            yield break;
        }

        yield return method;
    }

    [HarmonyPostfix]
    private static void Postfix(List<Overlay> __result)
    {
        if (!RobustaConfig.DisableVisualOverlays)
            return;

        for (var i = __result.Count - 1; i >= 0; i--)
        {
            var overlay = __result[i];
            if (!ShouldSuppress(overlay))
                continue;

            // BlindOverlay clears _lightManager.Enabled in Draw and relies on BeforeDraw to restore it.
            // Skipping the draw entirely means that restore never runs, so do it here.
            if (overlay.GetType().Name == "BlindOverlay")
                RestoreBlindLightSetup();

            __result.RemoveAt(i);
        }
    }

    /// <summary>Whether this overlay is on the filter list and should be skipped.</summary>
    internal static bool ShouldSuppress(Overlay overlay) => IsFiltered(overlay.GetType());

    /// <summary>Whether this overlay type is on the filter list.</summary>
    internal static bool IsFiltered(Type type)
    {
        if (Filtered.TryGetValue(type, out var cached))
            return cached;

        var filtered = false;
        foreach (var name in FilterNames)
        {
            if (Reflect.FindTypeByName(name) != type)
                continue;

            filtered = true;
            break;
        }

        Filtered[type] = filtered;
        return filtered;
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

    /// <summary>
    /// Whether the local player's own overlay should be hidden: the feature is on, the user has not
    /// asked to see their own, and the request is for the local player.
    /// </summary>
    internal static bool ShouldHideOwn(bool featureEnabled, EntityUid uid, EntityUid? localPlayer) =>
        featureEnabled &&
        !RobustaConfig.ShowOwnStatusIcons &&
        localPlayer != null &&
        uid == localPlayer;
}

/// <summary>
/// Skips the health bar the local player would draw on itself while the Health Bars switch is on and
/// Show Own Icons is off.
/// </summary>
[HarmonyPatch]
internal static class SelfHealthBarPatch
{
    [HarmonyTargetMethods]
    private static IEnumerable<MethodBase> TargetMethods()
    {
        var method = Reflect.GetMethod(
            Reflect.FindTypeByName("EntityHealthBarOverlay"), "CalcProgress", ReflectOptions.Any);
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
        if (!SelfOverlayFilter.ShouldHideOwn(RobustaConfig.Esp.Health.Enabled, uid, SelfOverlayFilter.LocalPlayer))
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
        var method = Reflect.GetMethod(
            Reflect.FindTypeByName("StatusIconSystem"), "GetStatusIcons", ReflectOptions.Any);
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
        if (!ShouldHideOwnIcons(uid, SelfOverlayFilter.LocalPlayer))
            return;

        __result.Clear();
    }

    /// <summary>
    /// Whether this icon request is the local player asking for its own icons, which the toggle hides.
    /// </summary>
    internal static bool ShouldHideOwnIcons(EntityUid uid, EntityUid? localPlayer) =>
        SelfOverlayFilter.ShouldHideOwn(RobustaConfig.Esp.Icons.Enabled, uid, localPlayer);
}
