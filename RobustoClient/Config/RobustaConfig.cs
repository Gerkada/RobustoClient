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

/// <summary>All configuration settings for RobustoClient.</summary>
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

    [KeybindConfig("View Player Info", command: "robusta.player_info")]
    public static RobustaKeybind ViewPlayerInfoKey = new(Keyboard.Key.Unknown);

    [KeybindConfig("Toggle Forced Zoom", command: "robusta.auto_zoom.toggle")]
    public static RobustaKeybind ToggleZoomKey = new(Keyboard.Key.Unknown);

    [KeybindConfig("Toggle Melee Aimbot", command: "robusta.melee_aimbot.toggle")]
    public static RobustaKeybind ToggleMeleeAimbotKey = new(Keyboard.Key.Unknown);

    [KeybindConfig("Toggle Ranged Aimbot", command: "robusta.ranged_aimbot.toggle")]
    public static RobustaKeybind ToggleRangedAimbotKey = new(Keyboard.Key.Unknown);

    [KeybindConfig("Toggle ESP", command: "robusta.esp.toggle")]
    public static RobustaKeybind ToggleEspKey = new(Keyboard.Key.Unknown);

    #endregion

    #region Toggles

    public static bool SpinBotEnabled;

    public static bool MeleeAimbotEnabled = true;

    public static bool MeleeTriggerbotEnabled = false;

    public static bool RangedAimbotEnabled = true;

    public static bool ThrowAimbotEnabled = true;

    public static bool NoRecoilEnabled = false;

    public static bool UsePrediction = false;

    /// <summary>Adds a Line of Sight mechanic.</summary>
    public static bool AimLineOfSight = true;

    /// <summary>Prevents firing if the target is not within LOS.</summary>
    public static bool AimFireWhenBlocked = false;

    /// <summary> Allows to lock onto players outside of combat mode, intended for throwing slippery items/pre locking.</summary>
    public static bool NoCombatLock = false;

    /// <summary> Place thrown slip items (soap, banana peels) where the target will walk onto them. </summary>
    public static bool SlipThrowEnabled = true;

    /// <summary> How far ahead of a target's movement vector a thrown slip item is placed. </summary>
    public static float SlipThrowMinLead = 1f;

    public static bool FOVDisable = true;
    public static bool DisableVisualOverlays = false;
    public static bool ShowOwnStatusIcons = false;

    public static bool FullbrightEnabled = false;

    public static bool AntiSlipEnabled = true;

    /// <summary>
    /// Global ESP switch. Off hides every category without forgetting which ones were on.
    /// </summary>
    /// <remarks>
    /// Not named <c>EspEnabled</c>: that was a retired config key, and reusing the name would make an
    /// old file silently drive this switch on upgrade.
    /// </remarks>
    public static bool EspOverlayEnabled = true;

    #endregion

    #region Combat

    public static float SpinBotDegreesPerSecond = 1440f;

    public static float AimRadius = 5f;

    /// <summary>Distance past which Target Lock drops the target.</summary>
    public static float MaxLockDistance = 15f;

    public static float RangedAimbotRadius = 2f;

    /// <summary>Aimbot capture radius, in screen pixels.</summary>
    public static float AimFovPixels = 100f;

    /// <summary>Average thrown-item speed, used for prediction.</summary>
    public static float DefaultThrowSpeed = 11f;

    /// <summary>Bullet speed used for prediction, 40 is the standard for most firearms.</summary>
    public static float DefaultProjectileSpeed = 40f;

    #endregion

    #region Ping compensation

    public enum PingMode { Local, Stable, Laggy, Auto }

    public static PingMode CurrentPingMode = PingMode.Auto;

    /// <summary> Ceiling applied to any measured network lead, in seconds. </summary>
    public static float MaxAimLeadSeconds = 0.5f;

    #endregion

    #region Radar

    /// <summary>Backdrop opacity of the radar window, 0 to 1.</summary>
    public static float RadarWindowOpacity = 1f;

    #endregion

    #region ESP

    /// <summary>Per-feature ESP switch, an empty Color means use the color from esp.yml.</summary>
    public sealed class EspFeatureConfig
    {
        public bool Enabled { get; set; } = true;

        public string Color { get; set; } = "";
    }

    public sealed class EspSettings
    {
        public EspFeatureConfig Players { get; set; } = new();
        public EspFeatureConfig Mobs { get; set; } = new();
        public EspFeatureConfig HeldItems { get; set; } = new();
        public EspFeatureConfig Syndicate { get; set; } = new();
        public EspFeatureConfig Contraband { get; set; } = new();
        public EspFeatureConfig Health { get; set; } = new();
        public EspFeatureConfig Icons { get; set; } = new();

        /// <summary>Parsed color per feature, keyed on the raw hex.</summary>
        private readonly Dictionary<Systems.ESP.EspFeature, (string Raw, Robust.Shared.Maths.Color Parsed)> _colorCache = new();

        /// <summary>Blip color per command role, keyed by <see cref="CommandRoles"/>.</summary>
        public Dictionary<string, string> RoleColors { get; set; } = new();

        /// <summary>Command roles the radar can color, with label and default color.</summary>
        public static readonly (string Key, string Label, string DefaultHex)[] CommandRoles =
        [
            ("RD", "Research Director", "#800080"),
            ("HOS", "Head of Security", "#8B0000"),
            ("QM", "Quartermaster", "#8B4513"),
            ("CMO", "Chief Medical Officer", "#00008B"),
            ("CAP", "Captain", "#FFD700"),
            ("HOP", "Head of Personnel", "#00FFFF"),
            ("CE", "Chief Engineer", "#FFA500")
        ];

        /// <summary>Effective color for a role key, or null when the key names no declared role.</summary>
        public Robust.Shared.Maths.Color? ResolveRoleColor(string role)
        {
            foreach (var (key, _, defaultHex) in CommandRoles)
            {
                if (!string.Equals(key, role, StringComparison.OrdinalIgnoreCase))
                    continue;

                if (RoleColors.TryGetValue(key, out var hex) &&
                    Robust.Shared.Maths.Color.TryFromHex(hex, out var parsed))
                {
                    return parsed;
                }

                return Robust.Shared.Maths.Color.TryFromHex(defaultHex, out var fallback) ? fallback : null;
            }

            return null;
        }

        /// <summary>Settings for a feature, or null for a value outside <see cref="Systems.ESP.EspFeature"/>.</summary>
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
            (Players?.Enabled ?? false) || (Mobs?.Enabled ?? false) || (HeldItems?.Enabled ?? false) ||
            (Syndicate?.Enabled ?? false) || (Contraband?.Enabled ?? false) || (Health?.Enabled ?? false) ||
            (Icons?.Enabled ?? false);

        internal void EnsureDefaults()
        {
            Players ??= new();
            Mobs ??= new();
            HeldItems ??= new();
            Syndicate ??= new();
            Contraband ??= new();
            Health ??= new();
            Icons ??= new();
            RoleColors ??= new();
        }

        /// <summary>
        /// color for a feature: the hex override when one is set and parseable, otherwise the
        /// category's own color from esp.yml.
        /// </summary>
        public Robust.Shared.Maths.Color ResolveColor(
            Systems.ESP.EspFeature feature,
            Robust.Shared.Maths.Color fallback)
        {
            var config = For(feature);
            if (config == null || string.IsNullOrWhiteSpace(config.Color))
                return fallback;

            if (_colorCache.TryGetValue(feature, out var cached) &&
                string.Equals(cached.Raw, config.Color, StringComparison.Ordinal))
            {
                return cached.Parsed;
            }

            var parsed = Robust.Shared.Maths.Color.TryFromHex(config.Color, out var value) ? value : fallback;
            _colorCache[feature] = (config.Color, parsed);
            return parsed;
        }
    }

    public static EspSettings Esp = new();

    #endregion

    #region Appearance

    /// <summary>Theme restored at startup. Built-in is "Dark", user themes live in the Themes folder.</summary>
    public static string DefaultTheme = "Dark";

    #endregion

    #region Radial

    /// <summary>
    /// One hold-to-open wheel: its own key plus up to four ordered sector actions. The number of
    /// non-empty actions selects the layout.
    /// </summary>
    public sealed class RadialMenuSettings
    {
        public string Name { get; set; } = "Radial";

        public RobustaKeybind Key { get; set; } = new(Keyboard.Key.Unknown);

        public List<string> Actions { get; set; } = new();

        /// <summary>Number of non-empty actions.</summary>
        [JsonIgnore]
        public int ActiveCount
        {
            get
            {
                int count = 0;
                foreach (var action in Actions)
                {
                    if (!string.IsNullOrWhiteSpace(action))
                        count++;
                }

                return count;
            }
        }

        /// <summary>The <paramref name="index"/>-th non-empty action, or an empty string.</summary>
        public string ActiveAt(int index)
        {
            int seen = 0;
            foreach (var action in Actions)
            {
                if (string.IsNullOrWhiteSpace(action))
                    continue;

                if (seen++ == index)
                    return action;
            }

            return "";
        }

        /// <summary>Four editable rows, empty rows are ignored, never reordered.</summary>
        internal void EnsureDefaults()
        {
            Actions ??= new();
            while (Actions.Count < 4)
                Actions.Add("");
            if (Actions.Count > 4)
                Actions.RemoveRange(4, Actions.Count - 4);
            for (int i = 0; i < Actions.Count; i++)
                Actions[i] ??= "";
        }
    }

    /// <summary>User-created wheels, in menu order.</summary>
    public static List<RadialMenuSettings> Radials = new();

    #endregion

    #region Window layout

    /// <summary>
    /// Where each window was last left, keyed by window id. Restored on the first frame of the next
    /// session, since ImGui's own persistence is disabled.
    /// </summary>
    public static Dictionary<string, WindowPlacement> WindowPlacements = new();

    /// <summary>Position and size of one window, in display pixels.</summary>
    public sealed class WindowPlacement
    {
        public float X { get; set; }
        public float Y { get; set; }
        public float Width { get; set; }
        public float Height { get; set; }
    }

    #endregion

    #region Friends

    public static HashSet<string> FriendsSet = [];

    #endregion
}
