using HarmonyLib;

namespace RobustoClient.Patches;

[HarmonyPatch("Robust.Client.Graphics.Clyde.Clyde", "DrawOcclusionDepth")]
internal static class DrawOcclusionDepthPatch
{
    [HarmonyPrefix]
    static bool Prefix()
    {
        return !RobustaConfig.FOVDisable;
    }
}