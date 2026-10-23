using System.Collections.Generic;
using System.Numerics;
using Content.Client.Weapons.Melee;
using Content.Client.MouseRotator;
using Content.Shared.MouseRotator;
using Content.Shared.Hands.EntitySystems;
using Content.Shared.Weapons.Melee;
using HarmonyLib;
using Robust.Client.GameObjects;
using Robust.Client.Input;
using Robust.Client.Player;
using Robust.Shared.GameObjects;
using Robust.Shared.IoC;
using Robust.Shared.Map;
using Robust.Shared.Maths;
using Robust.Shared.Timing;
using Robust.Shared.Utility;
using RobustoClient.Systems;

namespace RobustoClient.Patches;

/// <summary>
/// United melee patch: combines light-attack aimbot, heavy-attack aimbot,
/// and mouse-rotator rotation/triggerbot into a single consolidated file
/// so the melee code path is maintained in one place.
/// </summary>
public static class MeleePatch
{
    private static IEntityManager? _entMan;
    private static RobustaAimSystem? _aim;

    private static RobustaAimSystem Aim
    {
        get
        {
            _entMan ??= IoCManager.Resolve<IEntityManager>();
            _aim ??= _entMan.System<RobustaAimSystem>();
            return _aim;
        }
    }

    [HarmonyPatch(typeof(MeleeWeaponSystem), "ClientLightAttack")]
    public class ClientLightAttack
    {
        [HarmonyPrefix]
        private static void Prefix(
            EntityUid attacker,
            ref MapCoordinates mousePos,
            ref EntityCoordinates coordinates,
            MeleeWeaponComponent meleeComponent)
        {
            if (!RobustaConfig.MeleeAimbotEnabled) return;

            var aim = Aim;
            var output = aim.LockedTarget.HasValue && aim.IsInRange(attacker, aim.LockedTarget.Value, meleeComponent.Range)
                ? aim.GetAimOutputFromEnt(aim.LockedTarget.Value)
                : aim.GetClosestToEntInRange(attacker, meleeComponent.Range, [ attacker ]);

            if (output == null) return;

            mousePos = output.Value.Position;
            coordinates = new EntityCoordinates(output.Value.Entity, Vector2.Zero);
        }
    }

    [HarmonyPatch(typeof(MeleeWeaponSystem), "ClientHeavyAttack")]
    public class ClientHeavyAttack
    {
        [HarmonyPrefix]
        private static void Prefix(
            ref EntityUid user,
            ref EntityCoordinates coordinates,
            ref MeleeWeaponComponent component)
        {
            if (!RobustaConfig.MeleeAimbotEnabled) return;

            var aim = Aim;
            var output = aim.LockedTarget.HasValue && aim.IsInRange(user, aim.LockedTarget.Value, component.Range)
                ? aim.GetAimOutputFromEnt(aim.LockedTarget.Value)
                : aim.GetClosestToEntInRange(user, component.Range, [ user ]);

            if (output == null) return;

            coordinates = new EntityCoordinates(output.Value.Entity, Vector2.Zero);
        }
    }

    [HarmonyPatch(typeof(MouseRotatorSystem), nameof(MouseRotatorSystem.Update))]
    public class MouseRotatorUpdate
    {
        [HarmonyPrefix]
        private static bool Prefix()
        {
            if (!RobustaConfig.RangedAimbotEnabled && !RobustaConfig.MeleeAimbotEnabled && !RobustaConfig.MeleeTriggerbotEnabled)
                return true;

            var playerManager = IoCManager.Resolve<IPlayerManager>();
            var entityManager = IoCManager.Resolve<IEntityManager>();
            var timing = IoCManager.Resolve<IGameTiming>();

            var localPlayer = playerManager.LocalEntity;
            if (localPlayer == null) return true;

            if (!entityManager.HasComponent<MouseRotatorComponent>(localPlayer.Value))
                return true;

            var aimSystem = Aim;
            var xformSystem = entityManager.System<SharedTransformSystem>();

            var exclude = new HashSet<EntityUid> { localPlayer.Value };
            var mouseScreenPos = IoCManager.Resolve<IInputManager>().MouseScreenPosition;
            var target = aimSystem.GetClosestInRange(mouseScreenPos, RobustaConfig.RangedAimbotRadius, exclude);

            if (target == null) return true;

            if (!entityManager.TryGetComponent<TransformComponent>(localPlayer.Value, out var xform))
                return true;

            var playerWorldPos = xformSystem.GetWorldPosition(localPlayer.Value);
            var targetWorldPos = target.Value.Position.Position;
            var diff = targetWorldPos - playerWorldPos;

            if (diff.LengthSquared() <= 0.001f) return true;

            if (RobustaConfig.MeleeAimbotEnabled || RobustaConfig.MeleeTriggerbotEnabled)
            {
                var baseAngle = diff.ToAngle();
                var correctedAngle = new Angle(baseAngle.Theta + MathHelper.PiOver2);
                xformSystem.SetWorldRotation(localPlayer.Value, correctedAngle);
            }

            if (RobustaConfig.MeleeTriggerbotEnabled)
            {
                var handsSystem = entityManager.System<SharedHandsSystem>();
                var meleeSystem = entityManager.System<MeleeWeaponSystem>();

                if (handsSystem.TryGetActiveItem(localPlayer.Value, out var weapon) &&
                    entityManager.TryGetComponent<MeleeWeaponComponent>(weapon.Value, out var melee))
                {
                    if (diff.Length() <= melee.Range && timing.CurTime >= melee.NextAttack)
                    {
                        var targetCoords = new EntityCoordinates(target.Value.Entity, Vector2.Zero);

                        Traverse.Create(meleeSystem).Method("ClientLightAttack",
                            localPlayer.Value, target.Value.Position, targetCoords, weapon.Value, melee).GetValue();

                        Traverse.Create(meleeSystem).Method("PlayMeleeWeaponAnimation",
                            weapon.Value, melee, diff.ToAngle(), target.Value.Entity, false).GetValue();

                        melee.NextAttack = timing.CurTime + TimeSpan.FromSeconds(1.1 / melee.AttackRate);
                    }
                }
            }

            if (RobustaConfig.MeleeAimbotEnabled || RobustaConfig.MeleeTriggerbotEnabled)
                return false;

            return true;
        }
    }
}