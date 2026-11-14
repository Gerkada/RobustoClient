using Robust.Client.Placement;
using Robust.Shared.Enums;
using Robust.Shared.GameObjects;
using RobustoClient.Systems;
using RobustoClient.Systems.Construction.Blueprints;

namespace RobustoClient.UI.ImGui;

/// <summary>
/// Every service lookup the cheat menu needs, in one place. Views call these methods and never
/// touch IoC directly, which keeps the tab bodies purely declarative.
/// </summary>
internal static class CheatMenuActions
{
    /// <summary>Resolves an entity system, or returns null when the game is not up yet.</summary>
    private static T? System<T>() where T : EntitySystem
    {
        if (!RobustaImGuiManager.TryResolve<IEntityManager>(out var entMan))
            return null;

        return entMan.EntitySysManager.TryGetEntitySystem<T>(out var system) ? system : null;
    }

    /// <summary>Saved blueprints, or null when the manager is not up yet.</summary>
    public static RobustaBlueprintManager? BlueprintManager => System<RobustaBlueprintManager>();

    public static void SetAntiSlip(bool enabled) =>
        System<RobustaAntiSlipSystem>()?.SetEnabled(enabled);

    public static void SetHealthOverlay(bool enabled) =>
        System<RobustaHealthBarSystem>()?.SetEnabled(enabled);

    public static void SetJobIconsOverlay(bool enabled) =>
        System<RobustaJobIconsSystem>()?.SetEnabled(enabled);

    /// <summary>
    /// Starts placing a saved blueprint so it is displayed in the world. Uses the same hijack the
    /// construction menu uses, with no phase filter and auto-build off, so it shows without building.
    /// </summary>
    public static void PlaceBlueprint(RobustaBlueprint blueprint)
    {
        if (!RobustaImGuiManager.TryResolve<IPlacementManager>(out var placement))
            return;

        var hijack = new BlueprintPlacementHijack(blueprint, activePhaseFilter: null, autoBuildOnPlace: false);
        placement.BeginPlacing(
            new PlacementInformation { IsTile = false, PlacementOption = "SnapgridCenter" },
            hijack);
    }

    public static void ToggleRadar()
    {
        var radar = RobustaImGuiManager.Radar;
        radar.IsOpen = !radar.IsOpen;
    }

    public static void ToggleAutoChem()
    {
        var autoChem = RobustaImGuiManager.AutoChem;
        autoChem.IsOpen = !autoChem.IsOpen;
    }
}
