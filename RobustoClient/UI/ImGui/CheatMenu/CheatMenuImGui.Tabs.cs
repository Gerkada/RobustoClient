using System.Numerics;
using Hexa.NET.ImGui;
using static Hexa.NET.ImGui.ImGui;
using RobustoClient.ImGui;
using RobustoClient.ImGui.Theme;
using RobustoClient.Systems.ESP;

namespace RobustoClient.UI.ImGui;

public sealed partial class CheatMenuImGui
{
    private static float RowHeight() => RobustaImGuiScale.CardButton;

    private void DrawCombatTab() => DrawCardStack(
    [
        new WidgetCard("##CombatMelee", "MELEE", () =>
        {
            Toggle("Melee Aimbot", ref RobustaConfig.MeleeAimbotEnabled);
            Toggle("Triggerbot", ref RobustaConfig.MeleeTriggerbotEnabled);
        }),

        new WidgetCard("##CombatRanged", "RANGED", () =>
        {
            Toggle("Ranged Aimbot", ref RobustaConfig.RangedAimbotEnabled);
            Toggle("No Recoil", ref RobustaConfig.NoRecoilEnabled);
            Toggle("Throw Aimbot", ref RobustaConfig.ThrowAimbotEnabled);
            Toggle("Aim Prediction", ref RobustaConfig.UsePrediction);
        }),

        new WidgetCard("##CombatOptions", "OPTIONS", () =>
        {
            Slider("Aim FOV", "##AimFov", ref RobustaConfig.AimFovPixels, 10f, 500f, "%.0f px");
            Slider("Lock Distance", "##LockDist", ref RobustaConfig.MaxLockDistance, 1f, 40f, "%.1f m");

            RobustaImGuiWidgets.MutedText("Ping Compensation");
            DrawPingCompensationSelector();
        })
    ]);

    private void DrawVisualsTab() => DrawCardStack(
    [
        new WidgetCard("##VisualsOverlays", "OVERLAYS", () =>
        {
            Toggle("Fullbright", ref RobustaConfig.FullbrightEnabled);
            Toggle("FOV Disable", ref RobustaConfig.FOVDisable);

            if (RobustaImGuiWidgets.ToggleSwitch(
                    "Disable Visual Overlays",
                    ref RobustaConfig.DisableVisualOverlays,
                    "Hides flash, blindness, blurry vision, drunk and rainbow effects."))
            {
                MarkConfigDirty();
            }

            Spacing();

            // Both are upstream HUD overlays, so they live here rather than under ESP: they draw for
            // every valid entity, not only the ones ESP matches.
            bool health = RobustaConfig.Esp.Health.Enabled;
            if (RobustaImGuiWidgets.ToggleSwitch("Health Bars", ref health))
            {
                RobustaConfig.Esp.Health.Enabled = health;
                MarkConfigDirty();
                CheatMenuActions.SetHealthOverlay(health);
            }

            bool icons = RobustaConfig.Esp.Icons.Enabled;
            if (RobustaImGuiWidgets.ToggleSwitch("Job Icons", ref icons))
            {
                RobustaConfig.Esp.Icons.Enabled = icons;
                MarkConfigDirty();
                CheatMenuActions.SetJobIconsOverlay(icons);
            }
        }),

        new WidgetCard("##VisualsEsp", "ESP", DrawEspCard)
    ]);

    /// <summary>One colour row per ESP feature. The overlays toggles live in the OVERLAYS card.</summary>
    private void DrawEspCard()
    {
        EspColorRow("Players", EspFeature.Players, "#FFFFFF");
        EspColorRow("Mobs", EspFeature.Mobs, "#C8C8C8");
        EspColorRow("Held Items", EspFeature.HeldItems, "#FFA500");
        EspColorRow("Syndicate", EspFeature.Syndicate, "#FF0000");
        EspColorRow("Contraband", EspFeature.Contraband, "#FFA500");
    }

    /// <summary>
    /// Colour row for one ESP feature, bound to the persisted override. The config members are
    /// properties, so they cannot be passed by ref directly; edit locals and copy back on change.
    /// </summary>
    private void EspColorRow(string label, EspFeature feature, string fallbackHex)
    {
        var config = RobustaConfig.Esp.For(feature);
        if (config == null)
            return;

        ThemeHex.TryParse(fallbackHex, out var fallback);

        bool enabled = config.Enabled;
        string color = config.Color;

        if (!RobustaImGuiWidgets.ToggleColorRow(label, ref enabled, ref color, fallback))
            return;

        config.Enabled = enabled;
        config.Color = color;
        MarkConfigDirty();
    }

    private void DrawUtilityTab() => DrawCardStack(
    [
        new WidgetCard("##UtilityMove", "MOVEMENT", () =>
        {
            bool antiSlip = RobustaConfig.AntiSlipEnabled;
            if (RobustaImGuiWidgets.ToggleSwitch("No Slip", ref antiSlip))
            {
                CheatMenuActions.SetAntiSlip(antiSlip);
                MarkConfigDirty();
            }

            Toggle("Auto Spin", ref RobustaConfig.SpinBotEnabled);
            Slider("Spin Speed", "##SpinSpeed", ref RobustaConfig.SpinBotDegreesPerSecond, 90f, 3600f, "%.0f deg/s");
        }),

        new WidgetCard("##UtilityWindows", "WINDOWS", DrawWindowButtons),

        new WidgetCard("##UtilityAutomation", "BLUEPRINTS", DrawBlueprintPicker)
    ]);

    /// <summary>
    /// Dropdown of saved blueprints. Selecting one starts placement so it is displayed in the world;
    /// nothing is built. The list is read live each frame, so a blueprint saved this session appears
    /// without a refresh control.
    /// </summary>
    private void DrawBlueprintPicker()
    {
        var manager = CheatMenuActions.BlueprintManager;
        if (manager == null)
        {
            RobustaImGuiWidgets.MutedText("Blueprint manager unavailable.");
            return;
        }

        var blueprints = manager.Blueprints;
        if (blueprints.Count == 0)
        {
            RobustaImGuiWidgets.MutedText("No saved blueprints.");
            return;
        }

        const string preview = "Select a saved build...";

        if (!BeginCombo("##BlueprintPicker", preview))
            return;

        foreach (var blueprint in blueprints)
        {
            string entry = $"{blueprint.Name} ({blueprint.Width}x{blueprint.Height}, {blueprint.Items.Count} items)";
            if (!Selectable(entry))
                continue;

            CheatMenuActions.PlaceBlueprint(blueprint);
            CloseCurrentPopup();
            IsOpen = false;
        }

        EndCombo();
    }

    private static void DrawWindowButtons()
    {
        if (RobustaImGuiWidgets.AccentButton(
                "Tactical Radar", new Vector2(-1f, RowHeight()), RobustaImGuiManager.Radar.IsOpen))
        {
            CheatMenuActions.ToggleRadar();
        }

        if (RobustaImGuiWidgets.AccentButton(
                "AutoChem Bot", new Vector2(-1f, RowHeight()), RobustaImGuiManager.AutoChem.IsOpen))
        {
            CheatMenuActions.ToggleAutoChem();
        }

        if (RobustaImGuiWidgets.AccentButton("Station Map", new Vector2(-1f, RowHeight())))
            RobustaMapOpener.Show();
    }

    private void DrawPingCompensationSelector()
    {
        float width = (GetContentRegionAvail().X - RobustaImGuiTheme.Metrics.ItemInnerSpacingX * 4f) / 4f;
        var size = new Vector2(width, RowHeight());

        for (int i = 0; i < PingModes.Length; i++)
        {
            if (i > 0)
                SameLine();

            var (label, mode) = PingModes[i];
            if (!RobustaImGuiWidgets.AccentButton(label, size, RobustaConfig.CurrentPingMode == mode))
                continue;

            RobustaConfig.CurrentPingMode = mode;
            MarkConfigDirty();
        }
    }

    private static readonly (string Label, RobustaConfig.PingMode Mode)[] PingModes =
    [
        ("Local", RobustaConfig.PingMode.Local),
        ("Stable", RobustaConfig.PingMode.Stable),
        ("Laggy", RobustaConfig.PingMode.Laggy),
        ("Auto", RobustaConfig.PingMode.Auto)
    ];

}
