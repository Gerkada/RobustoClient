using Content.Shared.Weapons.Melee;
using HarmonyLib;
using Robust.Shared.Map;
using RobustoClient.Systems;
using Content.Client.Weapons.Melee;
using Robust.Client.GameObjects;
using Robust.Shared.GameObjects;
using Robust.Shared.IoC;
using System.Numerics; // For Vector2.Zero

namespace RobustoClient.Patches;

[HarmonyPatch(typeof(MeleeWeaponSystem), "ClientHeavyAttack")]
public class ClientHeavyAttackPatch
{
    private static IEntityManager? _entMan;
    private static RobustaAimSystem? _aim;

    [HarmonyPrefix]
    private static void Prefix(ref EntityUid user,
        ref EntityCoordinates coordinates,
        ref EntityUid meleeUid,
        ref MeleeWeaponComponent component)
    {
        if (!RobustaConfig.MeleeAimbotEnabled) return;
        
        _entMan ??= IoCManager.Resolve<IEntityManager>();
        _aim ??= _entMan.System<RobustaAimSystem>();

        // Look for target strictly within weapon range
        var output = _aim.GetClosestToEntInRange(user, component.Range, new HashSet<EntityUid> { user });
        
        if (output == null) return;

        // MELEE GOD MODE:
        // Completely ignore where the mouse was clicked (floor, wall, etc.).
        // Bind the heavy attack exactly to the center of the enemy entity!
        coordinates = new EntityCoordinates(output.Value.Entity, Vector2.Zero);
    }
}