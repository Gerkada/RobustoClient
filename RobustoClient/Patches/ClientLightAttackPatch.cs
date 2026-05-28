using Content.Shared.Weapons.Melee;
using HarmonyLib;
using Robust.Shared.Map;
using RobustoClient.Systems;
using Content.Client.Weapons.Melee;
using Robust.Client.GameObjects;
using Robust.Shared.GameObjects;
using Robust.Shared.IoC;
using System.Numerics; 

namespace RobustoClient.Patches;

[HarmonyPatch(typeof(MeleeWeaponSystem), "ClientLightAttack")] 
public class ClientLightAttackPatch
{
    private static IEntityManager? _entMan;
    private static RobustaAimSystem? _aim;

    [HarmonyPrefix]
    private static void Prefix(
        EntityUid attacker, 
        ref MapCoordinates mousePos, // Intercept mouse position
        ref EntityCoordinates coordinates, 
        MeleeWeaponComponent meleeComponent)
    {
        if (!RobustaConfig.MeleeAimbotEnabled) return;
        
        _entMan ??= IoCManager.Resolve<IEntityManager>();
        _aim ??= _entMan.System<RobustaAimSystem>();

        // Search for target in range
        var output = _aim.GetClosestToEntInRange(attacker, meleeComponent.Range, new HashSet<EntityUid> { attacker });
        
        if (output == null) return;

        // 1. Replace global mouse position with ideal enemy coordinates.
        mousePos = output.Value.Position;

        // 2. Bind the attack coordinates to the enemy entity
        coordinates = new EntityCoordinates(output.Value.Entity, Vector2.Zero);
    }
}