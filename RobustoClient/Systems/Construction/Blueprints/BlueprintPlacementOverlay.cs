using System.Numerics;
using System.Reflection;
using Robust.Client.Graphics;
using Robust.Client.Input;
using Robust.Client.Placement;
using Robust.Client.ResourceManagement;
using Robust.Shared.Enums;
using Robust.Shared.Map.Components;

namespace RobustoClient.Systems.Construction.Blueprints;

public sealed class BlueprintPlacementOverlay : Overlay
{
    private readonly IEntityManager _entMan;
    private readonly IPlacementManager _placement;
    private readonly IInputManager _input;
    private readonly IEyeManager _eye;
    private readonly SharedTransformSystem _transform;
    private readonly SharedMapSystem _mapSystem;
    private readonly Font _font;

    private readonly FieldInfo? _currentHijackField;
    private readonly PropertyInfo? _currentHijackProp;

    public override OverlaySpace Space => OverlaySpace.WorldSpace | OverlaySpace.ScreenSpace;

    public BlueprintPlacementOverlay()
    {
        _entMan = IoCManager.Resolve<IEntityManager>();
        _placement = IoCManager.Resolve<IPlacementManager>();
        _input = IoCManager.Resolve<IInputManager>();
        _eye = IoCManager.Resolve<IEyeManager>();
        _transform = _entMan.System<SharedTransformSystem>();
        _mapSystem = _entMan.System<SharedMapSystem>();

        var cache = IoCManager.Resolve<IResourceCache>();
        _font = new VectorFont(cache.GetResource<FontResource>("/Fonts/NotoSans/NotoSans-Regular.ttf"), 11);

        var pmType = _placement.GetType();
        _currentHijackField = pmType.GetField("_hijack", BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance)
                              ?? pmType.GetField("_currentHijack", BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance)
                              ?? pmType.GetField("CurrentHijack", BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance);
        _currentHijackProp = pmType.GetProperty("Hijack", BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance)
                             ?? pmType.GetProperty("CurrentHijack", BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance);
    }

    protected override void Draw(in OverlayDrawArgs args)
    {
        if (!_entMan.EntitySysManager.TryGetEntitySystem<RobustaBlueprintManager>(out var bpManager) ||
            !_entMan.EntitySysManager.TryGetEntitySystem<RobustaAutoConstructSystem>(out var autoConstruct))
        {
            return;
        }

        var activeHijack = GetActiveBlueprintHijack();

        if ((args.Space & OverlaySpace.WorldSpace) != 0)
        {
            DrawWorldOverlay(args.WorldHandle, bpManager, autoConstruct, activeHijack);
        }

        if ((args.Space & OverlaySpace.ScreenSpace) != 0)
        {
            DrawScreenOverlay(args.ScreenHandle, bpManager, autoConstruct, activeHijack);
        }
    }

    private BlueprintPlacementHijack? GetActiveBlueprintHijack()
    {
        if (!_placement.IsActive)
            return null;

        if (_placement is PlacementManager pm)
            return pm.Hijack as BlueprintPlacementHijack;

        return (_currentHijackProp?.GetValue(_placement) ?? _currentHijackField?.GetValue(_placement)) as BlueprintPlacementHijack;
    }

    private void DrawWorldOverlay(
        DrawingHandleWorld worldHandle,
        RobustaBlueprintManager bpManager,
        RobustaAutoConstructSystem autoConstruct,
        BlueprintPlacementHijack? activeHijack)
    {
        // 1. Draw 2-click area export selection preview if active
        if (bpManager.IsSelectingArea &&
            bpManager.TryGetCursorGridTile(out var curGridUid, out var c2) &&
            _entMan.TryGetComponent<MapGridComponent>(curGridUid, out var selGrid))
        {
            var c1 = (bpManager.FirstSelectionCorner is { } first && bpManager.SelectionGridUid == curGridUid)
                ? first
                : c2;

            var worldMatrix = _transform.GetWorldMatrix(curGridUid);
            worldHandle.SetTransform(worldMatrix);

            int minX = Math.Min(c1.X, c2.X);
            int maxX = Math.Max(c1.X, c2.X);
            int minY = Math.Min(c1.Y, c2.Y);
            int maxY = Math.Max(c1.Y, c2.Y);
            float ts = selGrid.TileSize;

            var rect = new Box2(minX * ts, minY * ts, (maxX + 1) * ts, (maxY + 1) * ts);
            worldHandle.DrawRect(rect, Color.Cyan.WithAlpha(0.18f), filled: true);
            worldHandle.DrawRect(rect, Color.Cyan.WithAlpha(0.85f), filled: false);
            worldHandle.SetTransform(Matrix3x2.Identity);
        }

        // 2. Draw active BlueprintPlacementHijack preview around cursor
        if (activeHijack != null &&
            bpManager.TryGetCursorGridTile(out var gridUid, out var baseTile) &&
            _entMan.TryGetComponent<MapGridComponent>(gridUid, out var grid))
        {
            var worldMatrix = _transform.GetWorldMatrix(gridUid);
            worldHandle.SetTransform(worldMatrix);

            int rotSteps = activeHijack.RotationSteps;
            float ts = grid.TileSize;

            foreach (var item in activeHijack.GetFilteredAndOrderedItems())
            {
                var (rx, ry) = BlueprintPlacementHijack.RotateOffset(
                    item.OffsetX,
                    item.OffsetY,
                    activeHijack.Blueprint.Width,
                    activeHijack.Blueprint.Height,
                    rotSteps);

                var tile = baseTile + new Vector2i(rx, ry);
                var itemDir = BlueprintPlacementHijack.RotateDirection(
                    BlueprintPlacementHijack.ParseDirection(item.Direction),
                    rotSteps);

                var color = GetPhaseColor(item.Phase, item.EntityPrototypeId);
                DrawTilePreviewBox(worldHandle, tile, ts, itemDir, color);
            }

            worldHandle.SetTransform(Matrix3x2.Identity);
        }

        // 3. Draw queued phased blueprint build tasks in world
        var pendingTasks = autoConstruct.PendingBlueprintTasks;
        if (pendingTasks.Count > 0)
        {
            var currentPhase = autoConstruct.CurrentActiveBlueprintPhase;
            foreach (var task in pendingTasks)
            {
                if (!_entMan.EntityExists(task.GridUid) ||
                    !_entMan.TryGetComponent<MapGridComponent>(task.GridUid, out var tGrid))
                {
                    continue;
                }

                var worldMatrix = _transform.GetWorldMatrix(task.GridUid);
                worldHandle.SetTransform(worldMatrix);

                var color = GetPhaseColor(task.Item.Phase, task.Item.EntityPrototypeId);
                float alphaScale = currentPhase == task.Item.Phase ? 1.0f : 0.4f;
                DrawTilePreviewBox(worldHandle, task.Tile, tGrid.TileSize, task.Dir, color.WithAlpha(0.65f * alphaScale));

                worldHandle.SetTransform(Matrix3x2.Identity);
            }
        }
    }

    private void DrawScreenOverlay(
        DrawingHandleScreen screenHandle,
        RobustaBlueprintManager bpManager,
        RobustaAutoConstructSystem autoConstruct,
        BlueprintPlacementHijack? activeHijack)
    {
        var mousePos = _input.MouseScreenPosition.Position;

        if (bpManager.IsSelectingArea)
        {
            var msg = bpManager.FirstSelectionCorner == null
                ? "[Blueprint Export] Click 1st corner tile (ESC to cancel)"
                : "[Blueprint Export] Click 2nd corner tile to save";
            screenHandle.DrawString(_font, mousePos + new Vector2(16f, -20f), msg, Color.Cyan);
            return;
        }

        if (activeHijack != null)
        {
            var phaseText = activeHijack.ActivePhaseFilter?.ToString() ?? "All Phases (0->4)";
            var dirText = (_placement.Direction).ToString();
            var modeText = activeHijack.AutoBuildOnPlace ? "Auto-Build" : "Ghost Only";
            var label = $"{activeHijack.Blueprint.Name} [{dirText}] | {phaseText} | {modeText}";
            screenHandle.DrawString(_font, mousePos + new Vector2(16f, 20f), label, Color.LimeGreen);

            int yOffset = 34;
            foreach (var item in activeHijack.GetFilteredAndOrderedItems())
            {
                if (!bpManager.TryGetCursorGridTile(out var gridUid, out var baseTile) ||
                    !_entMan.TryGetComponent<MapGridComponent>(gridUid, out var grid))
                    break;

                var (rx, ry) = BlueprintPlacementHijack.RotateOffset(
                    item.OffsetX,
                    item.OffsetY,
                    activeHijack.Blueprint.Width,
                    activeHijack.Blueprint.Height,
                    activeHijack.RotationSteps);
                var tile = baseTile + new Vector2i(rx, ry);
                var localCoords = _mapSystem.GridTileToLocal(gridUid, grid, tile);
                var screenCoords = _eye.CoordinatesToScreen(localCoords);
                var shortName = item.EntityPrototypeId;
                if (shortName.Length > 10)
                    shortName = shortName.Substring(0, 10);
                var color = GetPhaseColor(item.Phase, item.EntityPrototypeId);
                screenHandle.DrawString(_font, screenCoords.Position - new Vector2(18f, 6f), shortName, color);
                yOffset += 12;
                if (yOffset > 300)
                    break;
            }
        }
        else if (autoConstruct.PendingBlueprintTasks.Count > 0)
        {
            var phase = autoConstruct.CurrentActiveBlueprintPhase;
            var status = $"[Blueprint Builder] Phase {(int)(phase ?? 0)} ({phase}) — {autoConstruct.PendingBlueprintTasks.Count} remaining";
            screenHandle.DrawString(_font, mousePos + new Vector2(16f, 20f), status, Color.Yellow);
        }
    }

    private static void DrawTilePreviewBox(
        DrawingHandleWorld handle,
        Vector2i tile,
        float tileSize,
        Direction dir,
        Color color)
    {
        float pad = 0.06f * tileSize;
        var box = new Box2(
            tile.X * tileSize + pad,
            tile.Y * tileSize + pad,
            (tile.X + 1) * tileSize - pad,
            (tile.Y + 1) * tileSize - pad);

        handle.DrawRect(box, color.WithAlpha(0.22f), filled: true);
        handle.DrawRect(box, color.WithAlpha(0.85f), filled: false);

        var center = box.Center;
        var dirVec = dir.ToVec() * (tileSize * 0.35f);
        handle.DrawLine(center, center + dirVec, color.WithAlpha(0.95f));
    }

    public static Color GetPhaseColor(BlueprintZPhase phase, string? protoId = null)
    {
        return phase switch
        {
            BlueprintZPhase.Phase0_SubfloorAndLattice => Color.LightGray,
            BlueprintZPhase.Phase1_UnderfloorUtilities =>
                protoId != null && protoId.Contains("cable", StringComparison.OrdinalIgnoreCase)
                    ? Color.Yellow
                    : Color.Cyan,
            BlueprintZPhase.Phase2_FlooringAndFrames => Color.DodgerBlue,
            BlueprintZPhase.Phase3_WallsWindowsDoors => Color.Orange,
            BlueprintZPhase.Phase4_MachinesAndFixtures => Color.LimeGreen,
            _ => Color.White
        };
    }
}
