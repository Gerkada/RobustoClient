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

    [KeybindConfig("Toggle Cheat Menu", command: "robusta.toggle_menu", defaultKey: Keyboard.Key.F4)]
    public static RobustaKeybind ToggleMenuKey = new(Keyboard.Key.F4);

    [KeybindConfig("Target Lock (Aim/Throw)", defaultKey: Keyboard.Key.Space)]
    public static RobustaKeybind TargetLockKey = new(Keyboard.Key.Space);

    [KeybindConfig("Auto-Construct (Build)", command: "robusta.auto_construct", defaultKey: Keyboard.Key.V)]
    public static RobustaKeybind AutoConstructKey = new(Keyboard.Key.V);

    [KeybindConfig("Auto-Deconstruct", command: "robusta.auto_deconstruct", defaultKey: Keyboard.Key.N)]
    public static RobustaKeybind AutoDeconstructKey = new(Keyboard.Key.N);

    [KeybindConfig("Auto-Hack Wire Panel", command: "robusta.hack", defaultKey: Keyboard.Key.H, defaultMod1: Keyboard.Key.Shift)]
    public static RobustaKeybind HackingKey = new(Keyboard.Key.H, Keyboard.Key.Shift);

    [KeybindConfig("Smart Auto-Reload", command: "robusta.autoreload")]
    public static RobustaKeybind AutoReloadKey = new(Keyboard.Key.Unknown);

    [KeybindConfig("View Target Inventory", command: "robusta.view_inv")]
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

    public static bool SyndicateDetector = true;

    public static bool ContrabandDetector = true;

    public static bool FOVDisable = true;

    public static bool OverlaysDisable = true;

    public static bool FullbrightEnabled = false;

    public static bool EspEnabled = false;

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

    #region Friends

    public static HashSet<string> FriendsSet = [];

    #endregion

    #region Session only (never persisted)

    [JsonIgnore] public static bool ItemSearchEnabled = false;

    [JsonIgnore] public static string ItemSearchQuery = "";

    [JsonIgnore] public static int ManualPingMs = 0;

    #endregion
}