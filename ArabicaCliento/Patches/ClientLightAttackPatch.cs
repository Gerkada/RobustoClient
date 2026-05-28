using Content.Shared.Weapons.Melee;
using HarmonyLib;
using Robust.Shared.Map;
using ArabicaCliento.Systems;
using Content.Client.Weapons.Melee;
using Robust.Client.GameObjects;
using Robust.Shared.GameObjects;
using Robust.Shared.IoC;
using System.Numerics; 

namespace ArabicaCliento.Patches;

[HarmonyPatch(typeof(MeleeWeaponSystem), "ClientLightAttack")] 
public class ClientLightAttackPatch
{
    private static IEntityManager? _entMan;
    private static ArabicaAimSystem? _aim;

    [HarmonyPrefix]
    private static void Prefix(
        EntityUid attacker, 
        ref MapCoordinates mousePos, // ФИКС: Перехватываем позицию мыши!
        ref EntityCoordinates coordinates, 
        MeleeWeaponComponent meleeComponent)
    {
        if (!ArabicaConfig.MeleeAimbotEnabled) return;
        
        _entMan ??= IoCManager.Resolve<IEntityManager>();
        _aim ??= _entMan.System<ArabicaAimSystem>();

        // Ищем цель в радиусе поражения
        var output = _aim.GetClosestToEntInRange(attacker, meleeComponent.Range, new HashSet<EntityUid> { attacker });
        
        if (output == null) return;

        // 1. Подменяем глобальную позицию мыши на идеальные координаты врага.
        mousePos = output.Value.Position;

        // 2. Привязываем координаты самой атаки к сущности врага
        coordinates = new EntityCoordinates(output.Value.Entity, Vector2.Zero);
    }
}