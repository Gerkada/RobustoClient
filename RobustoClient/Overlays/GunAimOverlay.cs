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

        if (!combatModeSystem.IsInCombatMode(playerEntity))
            return;

        var mouseScreenPos = input.MouseScreenPosition;
        var mouseWorldPos = eye.PixelToMap(mouseScreenPos);

        if (RobustaConfig.MeleeAimbotEnabled)
        {
            var handsSystem = entManager.System<Content.Shared.Hands.EntitySystems.SharedHandsSystem>();
            if (handsSystem.TryGetActiveItem(playerEntity.Value, out var heldItem))
            {
                if (entManager.TryGetComponent<MeleeWeaponComponent>(heldItem, out var melee))
                {
                    // Cyan, so the melee radius reads as distinct from the gun FOV.
                    worldHandle.DrawCircle(mapPos.Position, melee.Range, Color.Cyan.WithAlpha(0.15f), false);
                }
            }
        }

        if (RobustaConfig.RangedAimbotEnabled && mapPos.MapId == mouseWorldPos.MapId)
        {
            var aimSystem = entManager.System<RobustaAimSystem>();
            var exclude = new HashSet<EntityUid> { playerEntity.Value };

            var centerScreen = mouseScreenPos;
            var edgeScreen = new ScreenCoordinates(mouseScreenPos.Position + new Vector2(RobustaConfig.AimFovPixels, 0), mouseScreenPos.Window);
            var centerWorld = eye.PixelToMap(centerScreen).Position;
            var edgeWorld = eye.PixelToMap(edgeScreen).Position;
            var fovWorldRadius = (edgeWorld - centerWorld).Length();

            worldHandle.DrawCircle(mouseWorldPos.Position, fovWorldRadius, Color.White.WithAlpha(0.1f), false);

            AimOutput? drawTarget = null;
            bool isLocked = false;

            if (aimSystem.LockedTarget.HasValue && entManager.EntityExists(aimSystem.LockedTarget.Value))
            {
                var lockedEnt = aimSystem.LockedTarget.Value;
                var lockedXform = entManager.GetComponent<TransformComponent>(lockedEnt);
                var lockedMapPos = transform.GetMapCoordinates(lockedEnt, xform: lockedXform);
                
                drawTarget = new AimOutput { Entity = lockedEnt, Position = lockedMapPos };
                isLocked = true;
            }
            else
            {
                drawTarget = aimSystem.GetClosestInScreenFov(mouseScreenPos, RobustaConfig.AimFovPixels, exclude);
            }

            if (drawTarget != null)
            {
                var targetPos = drawTarget.Value.Position.Position;

                // Green while locked, red while merely inside the FOV.
                var targetColor = isLocked ? Color.LimeGreen : Color.Red;
                worldHandle.DrawLine(mouseWorldPos.Position, targetPos, targetColor.WithAlpha(0.5f));
                worldHandle.DrawCircle(targetPos, 0.2f, targetColor, false);
                worldHandle.DrawLine(targetPos - new Vector2(0.3f, 0), targetPos + new Vector2(0.3f, 0), targetColor);
                worldHandle.DrawLine(targetPos - new Vector2(0, 0.3f), targetPos + new Vector2(0, 0.3f), targetColor);
            }
        }
    }
}