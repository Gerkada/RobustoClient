using Content.Client.Wires.UI;
using Content.Shared.Wires;
using HarmonyLib;
using Robust.Shared.GameObjects;
using Robust.Shared.IoC;
using RobustoClient.Systems.Hacking;

namespace RobustoClient.Patches;

[HarmonyPatch(typeof(WiresBoundUserInterface))]
internal static class WiresBoundUserInterfacePatch
{
    [HarmonyPatch("UpdateState")]
    [HarmonyPostfix]
    private static void UpdateStatePostfix(WiresBoundUserInterface __instance, BoundUserInterfaceState state)
    {
        if (state is not WiresBoundUserInterfaceState wiresState)
            return;

        var entityManager = IoCManager.Resolve<IEntityManager>();
        if (entityManager.EntitySysManager.TryGetEntitySystem<RobustaWiresSolver>(out var solver))
            solver.ObserveState(__instance.Owner, wiresState);
    }

    [HarmonyPatch("PerformAction")]
    [HarmonyPrefix]
    private static void PerformActionPrefix(WiresBoundUserInterface __instance, int id, WiresAction action)
    {
        var entityManager = IoCManager.Resolve<IEntityManager>();
        if (entityManager.EntitySysManager.TryGetEntitySystem<RobustaWiresSolver>(out var solver))
            solver.ObserveAction(__instance.Owner, id, action);
    }
}