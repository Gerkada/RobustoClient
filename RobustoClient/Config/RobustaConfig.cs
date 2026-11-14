using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;
using Robust.Client.Input;

namespace RobustoClient;

/// <summary>
/// Metadata attribute used for dynamic keybind discovery and UI generation.
/// </summary>
[AttributeUsage(AttributeTargets.Field)]
public sealed class KeybindConfigAttribute : Attribute
{
    public string DisplayName { get; }
    public string? Command { get; }
    public Keyboard.Key DefaultKey { get; }
    public Keyboard.Key DefaultMod1 { get; }

    public KeybindConfigAttribute(
        string displayName,
        string? command = null,
        Keyboard.Key defaultKey = Keyboard.Key.Unknown,
        Keyboard.Key defaultMod1 = Keyboard.Key.Unknown)
    {
        DisplayName = displayName;
        Command = command;
        DefaultKey = defaultKey;
        DefaultMod1 = defaultMod1;
    }
}

/// <summary>
/// Holds all configuration settings for the RobustoClient.
/// Uses "RobustaKeybind" to support multi-bounded keys.
/// </summary>
public static partial class RobustaConfig
{
    #region Keybinds

    [KeybindConfig("Open Menu", command: "robusta.toggle_menu", defaultKey: Keyboard.Key.F4)]
    public static RobustaKeybind ToggleMenuKey = new(Keyboard.Key.F4);

    [KeybindConfig("Target Lock", defaultKey: Keyboard.Key.Space)]
    public static RobustaKeybind TargetLockKey = new(Keyboard.Key.Space);

    [KeybindConfig("Auto-Construct", command: "robusta.auto_construct", defaultKey: Keyboard.Key.V)]
    public static RobustaKeybind AutoConstructKey = new(Keyboard.Key.V);

    [KeybindConfig("Auto-Deconstruct", command: "robusta.auto_deconstruct", defaultKey: Keyboard.Key.N)]
    public static RobustaKeybind AutoDeconstructKey = new(Keyboard.Key.N);

    [KeybindConfig("Auto-Hack", command: "robusta.hack", defaultKey: Keyboard.Key.H, defaultMod1: Keyboard.Key.Shift)]
    public static RobustaKeybind HackingKey = new(Keyboard.Key.H, Keyboard.Key.Shift);

    [KeybindConfig("Smart Auto-Reload", command: "robusta.autoreload")]
    public static RobustaKeybind AutoReloadKey = new(Keyboard.Key.Unknown);

    [KeybindConfig("View Inventory", command: "robusta.view_inv")]
    public static RobustaKeybind ViewInvKey = new(Keyboard.Key.Unknown);

    [KeybindConfig("Toggle Forced Zoom", command: "robusta.auto_zoom.toggle")]
    public static RobustaKeybind ToggleZoomKey = new(Keyboard.Key.Unknown);

    #endregion

    #region Toggles

    public static bool SpinBotEnabled;

    public static bool MeleeAimbotEnabled = true;

    public static bool MeleeTriggerbotEnabled = false;

    public static bool RangedAimbotEnabled = true;

    public static bool ThrowAimbotEnabled = true;

    public static bool NoRecoilEnabled = false;

    public static bool UsePrediction = false; // Toggles prediction

    public static bool FOVDisable = true;

    /// <summary>
    /// Hides the upstream visual effects (flash, blindness, blurry vision, drunk, rainbow)
    /// </summary>
    public static bool DisableVisualOverlays = false;

    public static bool FullbrightEnabled = false;

    public static bool AntiSlipEnabled = true;

    #endregion

    #region Combat

    public static float SpinBotDegreesPerSecond = 1440f;

    // Radius (FOV) where aimbot searches for target (in meters/tiles from cursor)
    public static float AimRadius = 5f;

    // Maximum distance at which Target Lock continues to hold the target (so that lock resets if target goes off-screen)
    public static float MaxLockDistance = 15f;

    public static float RangedAimbotRadius = 2f;

    public static float AimFovPixels = 100f; // Aimbot capture radius in pixels (Screen FOV)

    // Average speed of a thrown item (needed for prediction)
    public static float DefaultThrowSpeed = 11f;

    // Default bullet speed for prediction (40.0 is standard for most firearms in current build)
    public static float DefaultProjectileSpeed = 40f;

    #endregion

    #region Ping compensation

    public enum PingMode { Local, Stable, Laggy, Auto }

    public static PingMode CurrentPingMode = PingMode.Auto;

    #endregion

    #region ESP

    /// <summary>
    /// Per-feature ESP switch. Colours are hex overrides: an empty string means "use the colour
    /// declared in esp.yml", so the YAML stays authoritative and the picker is an override.
    /// </summary>
    public sealed class EspFeatureConfig
    {
        public bool Enabled { get; set; } = true;

        public string Color { get; set; } = "";
    }

    /// <summary>
    /// One switch per user-facing ESP feature. Properties rather than fields because
    /// <see cref="RobustaConfig"/> serializes each static field with System.Text.Json, which ignores
    /// public fields by default; a field-based type would persist as an empty object.
    /// </summary>
    public sealed class EspSettings
    {
        public EspFeatureConfig Players { get; set; } = new();
        public EspFeatureConfig Mobs { get; set; } = new();
        public EspFeatureConfig HeldItems { get; set; } = new();
        public EspFeatureConfig Syndicate { get; set; } = new();
        public EspFeatureConfig Contraband { get; set; } = new();
        public EspFeatureConfig Health { get; set; } = new();
        public EspFeatureConfig Icons { get; set; } = new();

        /// <summary>
        /// Settings for a feature, or null for a value outside <see cref="Systems.ESP.EspFeature"/>.
        /// Every declared member must map here; <c>EspSettingsTests</c> fails when one does not.
        /// </summary>
        public EspFeatureConfig? For(Systems.ESP.EspFeature feature) => feature switch
        {
            Systems.ESP.EspFeature.Players => Players,
            Systems.ESP.EspFeature.Mobs => Mobs,
            Systems.ESP.EspFeature.HeldItems => HeldItems,
            Systems.ESP.EspFeature.Syndicate => Syndicate,
            Systems.ESP.EspFeature.Contraband => Contraband,
            Systems.ESP.EspFeature.Health => Health,
            Systems.ESP.EspFeature.Icons => Icons,
            _ => null
        };

        public bool IsEnabled(Systems.ESP.EspFeature feature) => For(feature)?.Enabled ?? false;

        /// <summary>Cheap early-out for the overlay: nothing to draw when every feature is off.</summary>
        [JsonIgnore] public bool AnyEnabled =>
            Players.Enabled || Mobs.Enabled || HeldItems.Enabled || Syndicate.Enabled ||
            Contraband.Enabled || Health.Enabled || Icons.Enabled;

        /// <summary>
        /// Colour for a feature: the hex override when one is set and parseable, otherwise the
        /// category's own colour from esp.yml.
        /// </summary>
        public Robust.Shared.Maths.Color ResolveColor(
            Systems.ESP.EspFeature feature,
            Robust.Shared.Maths.Color fallback)
        {
            var config = For(feature);
            if (config == null || string.IsNullOrWhiteSpace(config.Color))
                return fallback;

            return Robust.Shared.Maths.Color.TryFromHex(config.Color, out var parsed) ? parsed : fallback;
        }
    }

    public static EspSettings Esp = new();

    #endregion

    #region Appearance

    /// <summary>Theme restored at startup. Built-in is "Dark"; user themes live in the Themes folder.</summary>
    public static string DefaultTheme = "Dark";

    #endregion

    #region Friends

    public static HashSet<string> FriendsSet = [];

    #endregion
}