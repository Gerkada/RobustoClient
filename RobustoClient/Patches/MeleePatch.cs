using System;
using System.Collections.Generic;
using System.Numerics;
using System.Reflection;
using Content.Client.Weapons.Melee;
using Content.Client.MouseRotator;
using Content.Shared.MouseRotator;
using Content.Shared.Hands.EntitySystems;
using Content.Shared.Weapons.Melee;
using HarmonyLib;
using Robust.Client.Input;
using Robust.Client.Player;
using Robust.Shared.GameObjects;
using Robust.Shared.IoC;
using Robust.Shared.Map;
using Robust.Shared.Maths;
using Robust.Shared.Timing;
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

    private static readonly MethodInfo? ClientLightAttackMethod = AccessTools.Method(typeof(MeleeWeaponSystem), "ClientLightAttack");
    private static readonly MethodInfo? PlayMeleeWeaponAnimationMethod = AccessTools.Method(typeof(MeleeWeaponSystem), "PlayMeleeWeaponAnimation");

    private static IEntityManager EntMan => _entMan ??= IoCManager.Resolve<IEntityManager>();
    private static RobustaAimSystem Aim => _aim ??= EntMan.System<RobustaAimSystem>();

    /// <summary>
    /// Melee target for this attack, LOCKED -> FOV.
    /// </summary>
    private static AimOutput? FindMeleeTarget(EntityUid origin, float range)
    {
        var aim = Aim;
        if (aim.GetLockedAimOutput() is { } locked) return locked;

        var mouse = IoCManager.Resolve<IInputManager>().MouseScreenPosition;
        return aim.GetClosestInScreenFov(mouse, RobustaConfig.AimFovPixels, [origin], origin, range);
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

            // Locked targets get prioritized.
            var output = FindMeleeTarget(attacker, meleeComponent.Range);

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

            var output = FindMeleeTarget(user, component.Range);

            if (output == null) return;

            coordinates = new EntityCoordinates(output.Value.Entity, Vector2.Zero);
        }
    }

    [HarmonyPatch(typeof(MeleeWeaponSystem), "ClientDisarm")]
    public class ClientDisarm
    {
        [HarmonyPrefix]
        private static void Prefix(
            EntityUid attacker,
            ref MapCoordinates mousePos,
            ref EntityCoordinates coordinates)
        {
            if (!RobustaConfig.MeleeAimbotEnabled) return;

            // The same weapon the server validates DoDisarm against, so the reach gate matches the range
            // it will check rather than guessing one.
            if (!EntMan.System<MeleeWeaponSystem>().TryGetWeapon(attacker, out _, out var melee))
                return;

            var output = FindMeleeTarget(attacker, melee.Range);

            if (output == null) return;

            // Upstream resolves the disarm target from the click position, so the position is the retarget.
            mousePos = output.Value.Position;
            coordinates = new EntityCoordinates(output.Value.Entity, Vector2.Zero);
        }
    }

    [HarmonyPatch(typeof(MouseRotatorSystem), nameof(MouseRotatorSystem.Update))]
    public class MouseRotatorUpdate
    {
        [HarmonyPrefix]
        private static bool Prefix()
        {
            if (!RobustaConfig.MeleeAimbotEnabled && !RobustaConfig.MeleeTriggerbotEnabled)
                return true;

            var playerManager = IoCManager.Resolve<IPlayerManager>();
            var entityManager = IoCManager.Resolve<IEntityManager>();
            var timing = IoCManager.Resolve<IGameTiming>();

            var localPlayer = playerManager.LocalEntity;
            if (localPlayer == null) return true;

            if (!entityManager.HasComponent<MouseRotatorComponent>(localPlayer.Value))
                return true;

            var xformSystem = entityManager.System<SharedTransformSystem>();
            var handsSystem = entityManager.System<SharedHandsSystem>();

            EntityUid? weapon = null;
            MeleeWeaponComponent? melee = null;
            if (handsSystem.TryGetActiveItem(localPlayer.Value, out var activeWeapon) &&
                entityManager.TryGetComponent(activeWeapon.Value, out MeleeWeaponComponent? meleeComp))
            {
                weapon = activeWeapon;
                melee = meleeComp;
            }

            float searchRange = melee != null ? melee.Range : RobustaConfig.RangedAimbotRadius;

            var target = FindMeleeTarget(localPlayer.Value, searchRange);

            if (target == null) return true;

            if (!entityManager.TryGetComponent<TransformComponent>(localPlayer.Value, out _))
                return true;

            var playerWorldPos = xformSystem.GetWorldPosition(localPlayer.Value);
            var targetWorldPos = target.Value.Position.Position;
            var diff = targetWorldPos - playerWorldPos;

            if (diff.LengthSquared() <= 0.001f) return true;

            var baseAngle = diff.ToAngle();
            var correctedAngle = new Angle(baseAngle.Theta + MathHelper.PiOver2);
            xformSystem.SetWorldRotation(localPlayer.Value, correctedAngle);

            if (RobustaConfig.MeleeTriggerbotEnabled && weapon.HasValue && melee != null)
            {
                if (diff.Length() <= melee.Range && timing.CurTime >= melee.NextAttack)
                {
                    var meleeSystem = entityManager.System<MeleeWeaponSystem>();
                    var targetCoords = new EntityCoordinates(target.Value.Entity, Vector2.Zero);

                    ClientLightAttackMethod?.Invoke(meleeSystem, [localPlayer.Value, target.Value.Position, targetCoords, weapon.Value, melee]);
                    PlayMeleeWeaponAnimationMethod?.Invoke(meleeSystem, [weapon.Value, melee, diff.ToAngle(), target.Value.Entity, false]);

                    melee.NextAttack = timing.CurTime + TimeSpan.FromSeconds(1.1 / melee.AttackRate);
                }
            }

            return false;
        }
    }
}