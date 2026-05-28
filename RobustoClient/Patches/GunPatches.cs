using Content.Client.Camera; 
using HarmonyLib;
using RobustoClient.Systems;

namespace RobustoClient.Patches;

// 1. ПАТЧ НА ОТДАЧУ
[HarmonyPatch(typeof(CameraRecoilSystem), "KickCamera")]
public class NoRecoilPatch
{
    [HarmonyPrefix]
    private static bool Prefix()
    {
        if (RobustaConfig.NoRecoilEnabled)
            return false; 
        
        return true;
    }
}