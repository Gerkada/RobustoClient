using System.Reflection;
using HarmonyLib;
using Robust.Client.Console;

namespace RobustoClient.Patches;

[HarmonyPatch(typeof(ClientConGroupController))]
internal static class ClientConGroupControllerPatch
{
    [HarmonyTargetMethods]
    private static IEnumerable<MethodBase> TargetMethods()
    {
        yield return AccessTools.Method(typeof(ClientConGroupController),
            nameof(ClientConGroupController.CanAdminMenu));
        yield return AccessTools.Method(typeof(ClientConGroupController),
            nameof(ClientConGroupController.CanAdminPlace));
        yield return AccessTools.Method(typeof(ClientConGroupController), nameof(ClientConGroupController.CanScript));
        yield return AccessTools.Method(typeof(ClientConGroupController), nameof(ClientConGroupController.CanCommand));
        yield return AccessTools.Method(typeof(ClientConGroupController), nameof(ClientConGroupController.CanViewVar));
    }

    [HarmonyPrefix]
    private static bool Prefix(ref bool __result)
    {
        __result = true;
        return false;
    }
}