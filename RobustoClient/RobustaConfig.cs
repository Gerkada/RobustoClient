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
}