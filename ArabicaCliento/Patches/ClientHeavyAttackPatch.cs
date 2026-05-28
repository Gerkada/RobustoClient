using Content.Shared.Weapons.Melee;
using HarmonyLib;
using Robust.Shared.Map;
using ArabicaCliento.Systems;
using Content.Client.Weapons.Melee;
using Robust.Client.GameObjects;
using Robust.Shared.GameObjects;
using Robust.Shared.IoC;
using System.Numerics; // Для Vector2.Zero

namespace ArabicaCliento.Patches;

[HarmonyPatch(typeof(MeleeWeaponSystem), "ClientHeavyAttack")]
public class ClientHeavyAttackPatch
{
    private static IEntityManager? _entMan;
    private static ArabicaAimSystem? _aim;

    [HarmonyPrefix]
    private static void Prefix(ref EntityUid user,
        ref EntityCoordinates coordinates,
        ref EntityUid meleeUid,
        ref MeleeWeaponComponent component)
    {
        if (!ArabicaConfig.MeleeAimbotEnabled) return;
        
        _entMan ??= IoCManager.Resolve<IEntityManager>();
        _aim ??= _entMan.System<ArabicaAimSystem>();

        // Ищем цель строго в радиусе поражения нашего оружия
        var output = _aim.GetClosestToEntInRange(user, component.Range, new HashSet<EntityUid> { user });
        
        if (output == null) return;

        // GOD MODE ДЛЯ МИЛИ:
        // Полностью игнорируем то, куда мы кликнули мышкой (пол, стена и т.д.).
        // Привязываем тяжелую атаку ровно к центру сущности врага!
        coordinates = new EntityCoordinates(output.Value.Entity, Vector2.Zero);
    }
}