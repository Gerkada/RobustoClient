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

    public static bool UsePrediction = false; // Включает/выключает упреждение

    public static bool ItemSearchEnabled = false;
    public static string ItemSearchQuery = "";

    // Радиус (FOV) в котором аимбот ищет цель (в метрах/тайлах от курсора)
    public static float AimRadius = 5f; 

    // Максимальная дистанция, на которой Target Lock продолжает держать цель (чтобы лок сбрасывался, если цель ушла за экран)
    public static float MaxLockDistance = 15f;

    public static Keyboard.Key TargetLockKey = Keyboard.Key.Space;

    public static bool ThrowAimbotEnabled = true;

    public static float AimFovPixels { get; set; } = 100f; // Радиус захвата аимбота в пикселях (Screen FOV)
    
    // Средняя скорость полета брошенного предмета (нужно для предикта)
    public static float DefaultThrowSpeed = 11f;

    // Дефолтная скорость пули для предикта (40.0 - стандарт для большинства огнестрела в актуальном билде)
    public static float DefaultProjectileSpeed = 40f;

    // Компенсация пинга
    public enum PingMode { Local, Stable, Laggy, Auto }
    public static PingMode CurrentPingMode = PingMode.Auto;
    public static int ManualPingMs = 0; 

    public static bool ContrabandDetector { get; set; } = true;


    public static HashSet<string> FriendsSet = [];

    //public static bool LogPlayers = true;
}