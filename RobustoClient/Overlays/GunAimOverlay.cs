using System.Collections.Generic;
using System.Numerics;
using RobustoClient.Systems;
using Content.Client.CombatMode;
using Content.Client.Weapons.Ranged.Systems;
using Content.Shared.Weapons.Melee;
using Robust.Client.Graphics;
using Robust.Client.Input;
using Robust.Client.Player;
using Robust.Shared.Enums;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Maths;

namespace RobustoClient.Overlays;

public class GunAimOverlay(
    IEntityManager entManager,
    IEyeManager eye,
    IInputManager input,
    IPlayerManager player,
    SharedTransformSystem transform,
    CombatModeSystem combatModeSystem)
    : Overlay
{
    public override OverlaySpace Space => OverlaySpace.WorldSpace;

    protected override void Draw(in OverlayDrawArgs args)
    {
        var worldHandle = args.WorldHandle;
        var playerEntity = player.LocalEntity;

        if (playerEntity == null ||
            !entManager.TryGetComponent<TransformComponent>(playerEntity, out var xform))
        {
            return;
        }

        var mapPos = transform.GetMapCoordinates(playerEntity.Value, xform: xform);
        if (mapPos.MapId == MapId.Nullspace)
            return;

        var aimSystem = entManager.System<RobustaAimSystem>();
        var lockedUid = aimSystem.LockedTarget is { } candidate && entManager.EntityExists(candidate)
            ? candidate
            : (EntityUid?) null;

        if (!combatModeSystem.IsInCombatMode(playerEntity))
        {
            // Allows locking outside of combat mode.
            if (RobustaConfig.NoCombatLock && lockedUid is { } preLock)
            {
                var preLockPos = transform.GetMapCoordinates(
                    preLock, xform: entManager.GetComponent<TransformComponent>(preLock)).Position;
                DrawTargetMarker(worldHandle, mapPos.Position, preLockPos, Color.LimeGreen);
            }

            return;
        }

        var mouseScreenPos = input.MouseScreenPosition;
        var mouseWorldPos = eye.PixelToMap(mouseScreenPos);
        var sameMap = mapPos.MapId == mouseWorldPos.MapId;
        var exclude = new HashSet<EntityUid> { playerEntity.Value };

        // The FOV circle bounds both aimbots' search, so draw it once whenever either is on.
        if (sameMap && (RobustaConfig.RangedAimbotEnabled || RobustaConfig.MeleeAimbotEnabled))
        {
            var edgeScreen = new ScreenCoordinates(mouseScreenPos.Position + new Vector2(RobustaConfig.AimFovPixels, 0), mouseScreenPos.Window);
            var centerWorld = eye.PixelToMap(mouseScreenPos).Position;
            var edgeWorld = eye.PixelToMap(edgeScreen).Position;

            worldHandle.DrawCircle(mouseWorldPos.Position, (edgeWorld - centerWorld).Length(), Color.White.WithAlpha(0.1f), false);
        }

        if (RobustaConfig.MeleeAimbotEnabled)
        {
            var handsSystem = entManager.System<Content.Shared.Hands.EntitySystems.SharedHandsSystem>();
            if (handsSystem.TryGetActiveItem(playerEntity.Value, out var heldItem) &&
                entManager.TryGetComponent<MeleeWeaponComponent>(heldItem, out var melee))
            {
                // Cyan, so the melee radius reads as distinct from the gun FOV.
                worldHandle.DrawCircle(mapPos.Position, melee.Range, Color.Cyan.WithAlpha(0.15f), false);

                // With the ranged aimbot off nothing else marks the melee pick, so show it here.
                if (!RobustaConfig.RangedAimbotEnabled)
                {
                    var meleeTarget = lockedUid is { } lockEnt
                        ? new AimOutput { Entity = lockEnt, Position = transform.GetMapCoordinates(lockEnt) }
                        : aimSystem.GetClosestInScreenFov(mouseScreenPos, RobustaConfig.AimFovPixels, exclude, playerEntity, melee.Range);

                    if (meleeTarget is { } picked)
                    {
                        var color = lockedUid != null ? Color.LimeGreen : Color.Red;
                        DrawTargetMarker(worldHandle, mouseWorldPos.Position, picked.Position.Position, color);
                    }
                }
            }
        }

        if (RobustaConfig.RangedAimbotEnabled && sameMap)
        {
            AimOutput? drawTarget;
            if (lockedUid is { } lockedEnt)
            {
                drawTarget = new AimOutput
                {
                    Entity = lockedEnt,
                    Position = transform.GetMapCoordinates(lockedEnt),
                };
            }
            else
            {
                drawTarget = aimSystem.GetClosestInScreenFov(mouseScreenPos, RobustaConfig.AimFovPixels, exclude);
            }

            // Only set while a target is acquired this tick.
            var plan = RobustaConfig.UsePrediction
                ? entManager.System<RobustaPredictionSystem>().LastPlan
                : null;
            var blocked = plan is { Blocked: true };

            if (drawTarget != null)
            {
                // Green while locked, red inside the FOV, orange when the shot cannot reach it.
                var targetColor = blocked ? Color.OrangeRed : lockedUid != null ? Color.LimeGreen : Color.Red;
                DrawTargetMarker(worldHandle, mouseWorldPos.Position, drawTarget.Value.Position.Position, targetColor);
            }

            // Lead marker: where the shot was actually pointed, and whether geometry blocked it.
            if (plan is { } solved)
            {
                var planColor = solved.Blocked ? Color.OrangeRed : Color.Cyan;
                worldHandle.DrawLine(mapPos.Position, solved.WorldPoint, planColor.WithAlpha(0.35f));
                worldHandle.DrawCircle(solved.WorldPoint, 0.15f, planColor, false);
            }
        }
    }

    private static void DrawTargetMarker(DrawingHandleWorld handle, Vector2 from, Vector2 to, Color color)
    {
        handle.DrawLine(from, to, color.WithAlpha(0.5f));
        handle.DrawCircle(to, 0.2f, color, false);
        handle.DrawLine(to - new Vector2(0.3f, 0), to + new Vector2(0.3f, 0), color);
        handle.DrawLine(to - new Vector2(0, 0.3f), to + new Vector2(0, 0.3f), color);
    }
}