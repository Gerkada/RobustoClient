using System.Reflection;
using HarmonyLib;
using RobustoClient.UI.ImGui;

namespace RobustoClient.Patches;

[HarmonyPatch]
internal static class ClydeImGuiRenderPatch
{
    private static MethodBase? TargetMethod()
    {
        var clydeType = AccessTools.TypeByName("Robust.Client.Graphics.Clyde.Clyde");
        return clydeType != null ? AccessTools.Method(clydeType, "SwapAllBuffers") : null;
    }

    [HarmonyPrefix]
    private static void SwapAllBuffersPrefix(bool ____drawingLoadingScreen)
    {
        if (____drawingLoadingScreen)
            return;

        RobustaImGuiManager.RenderFrame();
    }
}
