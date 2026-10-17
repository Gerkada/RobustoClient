using Content.Client.Access.Systems;
using Content.Client.Overlays;
using Content.Shared.StatusIcon.Components;
using HarmonyLib;
using Robust.Shared.GameObjects;
using Robust.Shared.IoC;
using Robust.Shared.Prototypes;
using RobustoClient.Systems;

namespace RobustoClient.Patches;

[HarmonyPatch]
internal static class JobStatusPatch
{
    [HarmonyPatch(typeof(JobStatusSystem), "OnGetStatusIconsEvent")]
    [HarmonyPrefix]
    private static void JobStatusPrefix(Entity<JobStatusComponent> ent)
    {
        if (ent.Comp.JobStatusIcon is not { } iconId)
            return;

        var protoMan = IoCManager.Resolve<IPrototypeManager>();
        if (!protoMan.HasIndex(iconId))
        {
            // Prevent UnknownPrototypeException loop on missing CMU/RMC14 JobIconPrototype IDs
            ent.Comp.JobStatusIcon = null;
        }
    }

    [HarmonyPatch(typeof(ShowMindShieldIconsSystem), "OnGetStatusIconsEvent")]
    [HarmonyPostfix]
    private static void StatusIconPostfix(Entity<StatusIconComponent> ent, ref GetStatusIconsEvent args)
    {
        if (!RobustaConfig.SyndicateDetector)
            return;

        var entMan = IoCManager.Resolve<IEntityManager>();
        if (entMan.TrySystem<RobustaSyndicateDetectorSystem>(out var detector))
        {
            detector.AddAntagStatusIcon(ent.Owner, ref args);
        }
    }
}
