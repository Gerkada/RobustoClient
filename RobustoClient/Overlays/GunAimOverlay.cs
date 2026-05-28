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

        // Only draw in combat mode
        if (!combatModeSystem.IsInCombatMode(playerEntity))
            return;

        var mouseScreenPos = input.MouseScreenPosition;
        var mouseWorldPos = eye.PixelToMap(mouseScreenPos);

        // --- MELEE RADIUS LOGIC ---
        if (RobustaConfig.MeleeAimbotEnabled)
        {
            // Get hands system through EntityManager
            var handsSystem = entManager.System<Content.Shared.Hands.EntitySystems.SharedHandsSystem>();
            
            // TryGetActiveItem is the most stable method in the current API. 
            // It will find the components and return the entity in the active hand.
            if (handsSystem.TryGetActiveItem(playerEntity.Value, out var heldItem))
            {
                // Check if the item is a melee weapon
                if (entManager.TryGetComponent<MeleeWeaponComponent>(heldItem, out var melee))
                {
                    // Draw an attack radius circle around the player (in world coordinates)
                    // Use Cyan to visually distinguish from the gun aim radius
                    worldHandle.DrawCircle(mapPos.Position, melee.Range, Color.Cyan.WithAlpha(0.15f), false);
                }
            }
        }

        // --- RANGED LOGIC ---
        if (RobustaConfig.RangedAimbotEnabled && mapPos.MapId == mouseWorldPos.MapId)
        {
            var aimSystem = entManager.System<RobustaAimSystem>();
            var exclude = new HashSet<EntityUid> { playerEntity.Value };

            // Draw FOV circle (accounting for camera zoom)
            var centerScreen = mouseScreenPos;
            var edgeScreen = new ScreenCoordinates(mouseScreenPos.Position + new Vector2(RobustaConfig.AimFovPixels, 0), mouseScreenPos.Window);
            var centerWorld = eye.PixelToMap(centerScreen).Position;
            var edgeWorld = eye.PixelToMap(edgeScreen).Position;
            var fovWorldRadius = (edgeWorld - centerWorld).Length();

            worldHandle.DrawCircle(mouseWorldPos.Position, fovWorldRadius, Color.White.WithAlpha(0.1f), false);

            AimOutput? drawTarget = null;
            bool isLocked = false;

            // 1. CHECK LOCK: If the target is locked, use it
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
                // 2. TARGET SEARCH: If there is no lock, search for the closest mob in FOV
                drawTarget = aimSystem.GetClosestInScreenFov(mouseScreenPos, RobustaConfig.AimFovPixels, exclude);
            }

            // VISUAL DRAWING
            if (drawTarget != null)
            {
                var targetPos = drawTarget.Value.Position.Position;
                
                // Color: Green if target is locked. Red if just in visibility zone.
                var targetColor = isLocked ? Color.LimeGreen : Color.Red;
                
                // Snapline from mouse to target
                worldHandle.DrawLine(mouseWorldPos.Position, targetPos, targetColor.WithAlpha(0.5f));

                // Crosshair on target
                worldHandle.DrawCircle(targetPos, 0.2f, targetColor, false);
                worldHandle.DrawLine(targetPos - new Vector2(0.3f, 0), targetPos + new Vector2(0.3f, 0), targetColor);
                worldHandle.DrawLine(targetPos - new Vector2(0, 0.3f), targetPos + new Vector2(0, 0.3f), targetColor);
            }
        }
    }
}