using System.Collections.Generic;
using ArabicaCliento.Systems;
using Content.Client.MouseRotator;
using Content.Shared.MouseRotator;
using Content.Shared.Weapons.Melee;
using HarmonyLib;
using Robust.Client.Input;
using Robust.Client.Player;
using Robust.Shared.GameObjects;
using Robust.Shared.IoC;
using Robust.Shared.Map;
using Robust.Shared.Maths;
using Robust.Shared.Timing;
using System.Numerics;

namespace ArabicaCliento.Patches;

[HarmonyPatch(typeof(MouseRotatorSystem), nameof(MouseRotatorSystem.Update))]
internal class MouseRotatorPatch
{
    [HarmonyPrefix]
    private static bool Prefix()
    {
        if (!ArabicaConfig.RangedAimbotEnabled && !ArabicaConfig.MeleeAimbotEnabled)
            return true;

        var playerManager = IoCManager.Resolve<IPlayerManager>();
        var entityManager = IoCManager.Resolve<IEntityManager>();
        var timing = IoCManager.Resolve<IGameTiming>();

        var localPlayer = playerManager.LocalEntity;
        if (localPlayer == null) return true;

        if (!entityManager.HasComponent<MouseRotatorComponent>(localPlayer.Value))
            return true;

        var aimSystem = entityManager.System<ArabicaAimSystem>();
        var xformSystem = entityManager.System<SharedTransformSystem>();

        var exclude = new HashSet<EntityUid> { localPlayer.Value };
        var mouseScreenPos = IoCManager.Resolve<IInputManager>().MouseScreenPosition;
        var target = aimSystem.GetClosestInRange(mouseScreenPos, ArabicaConfig.RangedAimbotRadius, exclude);

        if (target != null)
        {
            if (entityManager.TryGetComponent<TransformComponent>(localPlayer.Value, out var xform))
            {
                var playerWorldPos = xformSystem.GetWorldPosition(localPlayer.Value);
                var targetWorldPos = target.Value.Position.Position;
                var diff = targetWorldPos - playerWorldPos;

                if (diff.LengthSquared() > 0.001f)
                {
                    // 1. ПОВОРОТ
                    // Работает, если включена ЛИБО наводка, ЛИБО авто-удар (т.к. без поворота удар не пройдет)
                    if (ArabicaConfig.MeleeAimbotEnabled || ArabicaConfig.MeleeTriggerbotEnabled)
                    {
                        var baseAngle = diff.ToAngle();
                        var correctedAngle = new Angle(baseAngle.Theta + MathHelper.PiOver2);
                        xformSystem.SetWorldRotation(localPlayer.Value, correctedAngle);
                    }

                    // 2. АВТО-АТАКА (Triggerbot)
                    // Теперь зависит от СВОЕЙ собственной настройки
                    if (ArabicaConfig.MeleeTriggerbotEnabled)
                    {
                        var handsSystem = entityManager.System<Content.Shared.Hands.EntitySystems.SharedHandsSystem>();
                        var meleeSystem = entityManager.System<Content.Client.Weapons.Melee.MeleeWeaponSystem>();

                        if (handsSystem.TryGetActiveItem(localPlayer.Value, out var weapon) &&
                            entityManager.TryGetComponent<MeleeWeaponComponent>(weapon.Value, out var melee))
                        {
                            if (diff.Length() <= melee.Range && timing.CurTime >= melee.NextAttack)
                            {
                                var targetCoords = new EntityCoordinates(target.Value.Entity, Vector2.Zero);

                                // Логика удара
                                Traverse.Create(meleeSystem).Method("ClientLightAttack", 
                                    localPlayer.Value, target.Value.Position, targetCoords, weapon.Value, melee).GetValue();

                                // Визуал
                                Traverse.Create(meleeSystem).Method("PlayMeleeWeaponAnimation", 
                                    weapon.Value, melee, diff.ToAngle(), target.Value.Entity, false).GetValue();

                                melee.NextAttack = timing.CurTime + TimeSpan.FromSeconds(1.1 / melee.AttackRate);
                            }
                        }
                    }
                    
                    // Блокируем стандартный поворот мыши, если хоть что-то из нашего активно
                    if (ArabicaConfig.MeleeAimbotEnabled || ArabicaConfig.MeleeTriggerbotEnabled)
                        return false; 
                }
            }
        }
        return true; 
    }
}