using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Robust.Client.Input;

namespace RobustoClient;

public static class RobustaConfig
{
    public static bool SpinBotEnabled;
    
    public static float SpinBotDegreesPerSecond = 1440f;
    
    public static bool AntiSlipEnabled = true;
    
    public static bool MeleeAimbotEnabled = true;

    public static bool MeleeTriggerbotEnabled = false;
    
    public static bool RangedAimbotEnabled = true;
    
    public static float RangedAimbotRadius = 2f;

    public static bool SyndicateDetector = true;
    
    public static bool FOVDisable = true;
    
    public static bool OverlaysDisable = true;

    public static bool NoRecoilEnabled = false;

    public static bool FullbrightEnabled = false;

    public static bool EspEnabled = false;

    public static bool UsePrediction = false; // Toggles prediction

    public static bool ItemSearchEnabled = false;
    public static string ItemSearchQuery = "";

    // Radius (FOV) where aimbot searches for target (in meters/tiles from cursor)
    public static float AimRadius = 5f; 

    // Maximum distance at which Target Lock continues to hold the target (so that lock resets if target goes off-screen)
    public static float MaxLockDistance = 15f;

    public static Keyboard.Key TargetLockKey = Keyboard.Key.Space;

    public static Keyboard.Key AutoConstructKey = Keyboard.Key.V;

    public static Keyboard.Key AutoDeconstructKey = Keyboard.Key.N;

    public static Keyboard.Key HackingKey = Keyboard.Key.H;

    public static bool ThrowAimbotEnabled = true;

    public static float AimFovPixels { get; set; } = 100f; // Aimbot capture radius in pixels (Screen FOV)
    
    // Average speed of a thrown item (needed for prediction)
    public static float DefaultThrowSpeed = 11f;

    // Default bullet speed for prediction (40.0 is standard for most firearms in current build)
    public static float DefaultProjectileSpeed = 40f;

    // Ping compensation
    public enum PingMode { Local, Stable, Laggy, Auto }
    public static PingMode CurrentPingMode = PingMode.Auto;
    public static int ManualPingMs = 0; 

    public static bool ContrabandDetector { get; set; } = true;


    public static HashSet<string> FriendsSet = [];

    static RobustaConfig()
    {
        AppDomain.CurrentDomain.ProcessExit += (_, _) => Save();
    }

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private static string GetConfigPath()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        return Path.Combine(appData, "RobustoClient", "Configs", "config.json");
    }

    private sealed class ConfigData
    {
        public bool SpinBotEnabled { get; set; }
        public float SpinBotDegreesPerSecond { get; set; } = 1440f;
        public bool AntiSlipEnabled { get; set; } = true;
        public bool MeleeAimbotEnabled { get; set; } = true;
        public bool MeleeTriggerbotEnabled { get; set; }
        public bool RangedAimbotEnabled { get; set; } = true;
        public float RangedAimbotRadius { get; set; } = 2f;
        public bool SyndicateDetector { get; set; } = true;
        public bool FOVDisable { get; set; } = true;
        public bool OverlaysDisable { get; set; } = true;
        public bool NoRecoilEnabled { get; set; }
        public bool FullbrightEnabled { get; set; }
        public bool EspEnabled { get; set; }
        public bool UsePrediction { get; set; }
        public float AimRadius { get; set; } = 5f;
        public float MaxLockDistance { get; set; } = 15f;
        public Keyboard.Key AutoConstructKey { get; set; } = Keyboard.Key.V;
        public Keyboard.Key AutoDeconstructKey { get; set; } = Keyboard.Key.N;
        public Keyboard.Key HackingKey { get; set; } = Keyboard.Key.H;
        public bool ThrowAimbotEnabled { get; set; } = true;
        public float AimFovPixels { get; set; } = 100f;
        public float DefaultThrowSpeed { get; set; } = 11f;
        public float DefaultProjectileSpeed { get; set; } = 40f;
        public PingMode CurrentPingMode { get; set; } = PingMode.Auto;
        public bool ContrabandDetector { get; set; } = true;
        public HashSet<string> FriendsSet { get; set; } = [];
    }

    public static void Save()
    {
        try
        {
            var file = GetConfigPath();
            var dir = Path.GetDirectoryName(file);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            var data = new ConfigData
            {
                SpinBotEnabled = SpinBotEnabled,
                SpinBotDegreesPerSecond = SpinBotDegreesPerSecond,
                AntiSlipEnabled = AntiSlipEnabled,
                MeleeAimbotEnabled = MeleeAimbotEnabled,
                MeleeTriggerbotEnabled = MeleeTriggerbotEnabled,
                RangedAimbotEnabled = RangedAimbotEnabled,
                RangedAimbotRadius = RangedAimbotRadius,
                SyndicateDetector = SyndicateDetector,
                FOVDisable = FOVDisable,
                OverlaysDisable = OverlaysDisable,
                NoRecoilEnabled = NoRecoilEnabled,
                FullbrightEnabled = FullbrightEnabled,
                EspEnabled = EspEnabled,
                UsePrediction = UsePrediction,
                AimRadius = AimRadius,
                MaxLockDistance = MaxLockDistance,
                AutoConstructKey = AutoConstructKey,
                AutoDeconstructKey = AutoDeconstructKey,
                HackingKey = HackingKey,
                ThrowAimbotEnabled = ThrowAimbotEnabled,
                AimFovPixels = AimFovPixels,
                DefaultThrowSpeed = DefaultThrowSpeed,
                DefaultProjectileSpeed = DefaultProjectileSpeed,
                CurrentPingMode = CurrentPingMode,
                ContrabandDetector = ContrabandDetector,
                FriendsSet = new HashSet<string>(FriendsSet)
            };

            File.WriteAllText(file, JsonSerializer.Serialize(data, JsonOptions));
        }
        catch (Exception ex)
        {
            MarseyLogger.Warn($"Failed to save config.json: {ex.Message}");
        }
    }

    public static void Load()
    {
        try
        {
            var file = GetConfigPath();
            if (!File.Exists(file))
                return;

            var data = JsonSerializer.Deserialize<ConfigData>(File.ReadAllText(file));
            if (data == null)
                return;

            SpinBotEnabled = data.SpinBotEnabled;
            SpinBotDegreesPerSecond = data.SpinBotDegreesPerSecond;
            AntiSlipEnabled = data.AntiSlipEnabled;
            MeleeAimbotEnabled = data.MeleeAimbotEnabled;
            MeleeTriggerbotEnabled = data.MeleeTriggerbotEnabled;
            RangedAimbotEnabled = data.RangedAimbotEnabled;
            RangedAimbotRadius = data.RangedAimbotRadius;
            SyndicateDetector = data.SyndicateDetector;
            FOVDisable = data.FOVDisable;
            OverlaysDisable = data.OverlaysDisable;
            NoRecoilEnabled = data.NoRecoilEnabled;
            FullbrightEnabled = data.FullbrightEnabled;
            EspEnabled = data.EspEnabled;
            UsePrediction = data.UsePrediction;
            AimRadius = data.AimRadius;
            MaxLockDistance = data.MaxLockDistance;
            if (data.AutoConstructKey != Keyboard.Key.Unknown)
                AutoConstructKey = data.AutoConstructKey;
            if (data.AutoDeconstructKey != Keyboard.Key.Unknown)
                AutoDeconstructKey = data.AutoDeconstructKey;
            if (data.HackingKey != Keyboard.Key.Unknown)
                HackingKey = data.HackingKey;
            ThrowAimbotEnabled = data.ThrowAimbotEnabled;
            AimFovPixels = data.AimFovPixels;
            DefaultThrowSpeed = data.DefaultThrowSpeed;
            DefaultProjectileSpeed = data.DefaultProjectileSpeed;
            CurrentPingMode = data.CurrentPingMode;
            ContrabandDetector = data.ContrabandDetector;
            FriendsSet = data.FriendsSet ?? [];
        }
        catch (Exception ex)
        {
            MarseyLogger.Warn($"Failed to load config.json: {ex.Message}");
        }
    }
}