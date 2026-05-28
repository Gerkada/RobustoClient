using Content.Client.Camera; 
using HarmonyLib;
using ArabicaCliento.Systems;

namespace ArabicaCliento.Patches;

// 1. ПАТЧ НА ОТДАЧУ
[HarmonyPatch(typeof(CameraRecoilSystem), "KickCamera")]
public class NoRecoilPatch
{
    [HarmonyPrefix]
    private static bool Prefix()
    {
        if (ArabicaConfig.NoRecoilEnabled)
            return false; 
        
        return true;
    }
}