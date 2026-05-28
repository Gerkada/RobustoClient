using System.Collections.Generic;
using System.Numerics;
using ArabicaCliento.Systems;
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

namespace ArabicaCliento.Overlays;

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

        // Отрисовка только в боевом режиме
        if (!combatModeSystem.IsInCombatMode(playerEntity))
            return;

        var mouseScreenPos = input.MouseScreenPosition;
        var mouseWorldPos = eye.PixelToMap(mouseScreenPos);

        // --- ЛОГИКА РАДИУСА БЛИЖНЕГО БОЯ ---
        if (ArabicaConfig.MeleeAimbotEnabled)
        {
            // Получаем систему рук через EntityManager
            var handsSystem = entManager.System<Content.Shared.Hands.EntitySystems.SharedHandsSystem>();
            
            // TryGetActiveItem — самый стабильный метод в текущем API. 
            // Он сам найдет компоненты и вернет сущность в активной руке.
            if (handsSystem.TryGetActiveItem(playerEntity.Value, out var heldItem))
            {
                // Проверяем, является ли предмет оружием ближнего боя
                if (entManager.TryGetComponent<MeleeWeaponComponent>(heldItem, out var melee))
                {
                    // Рисуем круг радиуса атаки вокруг игрока (в мировых координатах)
                    // Используем Cyan, чтобы визуально отличать от радиуса аима пушек
                    worldHandle.DrawCircle(mapPos.Position, melee.Range, Color.Cyan.WithAlpha(0.15f), false);
                }
            }
        }

        // --- ЛОГИКА ДАЛЬНЕГО БОЯ ---
        if (ArabicaConfig.RangedAimbotEnabled && mapPos.MapId == mouseWorldPos.MapId)
        {
            var aimSystem = entManager.System<ArabicaAimSystem>();
            var exclude = new HashSet<EntityUid> { playerEntity.Value };

            // Отрисовка круга FOV (с учетом зума камеры)
            var centerScreen = mouseScreenPos;
            var edgeScreen = new ScreenCoordinates(mouseScreenPos.Position + new Vector2(ArabicaConfig.AimFovPixels, 0), mouseScreenPos.Window);
            var centerWorld = eye.PixelToMap(centerScreen).Position;
            var edgeWorld = eye.PixelToMap(edgeScreen).Position;
            var fovWorldRadius = (edgeWorld - centerWorld).Length();

            worldHandle.DrawCircle(mouseWorldPos.Position, fovWorldRadius, Color.White.WithAlpha(0.1f), false);

            AimOutput? drawTarget = null;
            bool isLocked = false;

            // 1. ПРОВЕРЯЕМ ЗАХВАТ: Если цель залочена, берем её
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
                // 2. ПОИСК ЦЕЛИ: Если лока нет, ищем ближайшего моба в FOV
                drawTarget = aimSystem.GetClosestInScreenFov(mouseScreenPos, ArabicaConfig.AimFovPixels, exclude);
            }

            // ОТРИСОВКА ВИЗУАЛА
            if (drawTarget != null)
            {
                var targetPos = drawTarget.Value.Position.Position;
                
                // Цвет: Зеленый, если цель в локе. Красный, если просто в зоне видимости.
                var targetColor = isLocked ? Color.LimeGreen : Color.Red;
                
                // Snapline от мышки до цели
                worldHandle.DrawLine(mouseWorldPos.Position, targetPos, targetColor.WithAlpha(0.5f));

                // Крестик на цели
                worldHandle.DrawCircle(targetPos, 0.2f, targetColor, false);
                worldHandle.DrawLine(targetPos - new Vector2(0.3f, 0), targetPos + new Vector2(0.3f, 0), targetColor);
                worldHandle.DrawLine(targetPos - new Vector2(0, 0.3f), targetPos + new Vector2(0, 0.3f), targetColor);
            }
        }
    }
}