using System.Numerics;
using Content.Shared.Mobs.Components;
using Hexa.NET.ImGui;
using Robust.Client.Graphics;
using Robust.Client.Player;
using Robust.Shared.GameObjects;
using Robust.Shared.IoC;
using Robust.Shared.Map;
using Robust.Shared.Maths;
using RobustoClient.ImGui;

namespace RobustoClient.UI.ImGui;

public enum RadarMarkerType { Player, Item, Syndicate }
public enum RadarOrientation { CameraUp, NorthUp, HeadingUp }

public sealed class RadarImGui
{
    public bool IsOpen;

    private float _rangeMeters = 30f;
    private bool _showPlayers = true;
    private bool _showItems = true;
    private RadarOrientation _orientation = RadarOrientation.CameraUp;
    private int _frameCounter;
    private const int UpdateInterval = 10;

    private readonly List<(Vector2 WorldPos, uint Color, string Name, RadarMarkerType Type)> _markers = new();

    public void Draw()
    {
        Hexa.NET.ImGui.ImGui.SetNextWindowSize(new Vector2(310f, 360f), ImGuiCond.FirstUseEver);
        Hexa.NET.ImGui.ImGui.SetNextWindowSizeConstraints(new Vector2(240f, 290f), new Vector2(700f, 750f));

        if (!Hexa.NET.ImGui.ImGui.Begin("Robusta Tactical Radar##RadarWin", ref IsOpen, ImGuiWindowFlags.NoCollapse))
        {
            Hexa.NET.ImGui.ImGui.End();
            return;
        }

        Hexa.NET.ImGui.ImGui.SetNextItemWidth(MathF.Max(75f, Hexa.NET.ImGui.ImGui.GetContentRegionAvail().X - 175f));
        Hexa.NET.ImGui.ImGui.SliderFloat("Range", ref _rangeMeters, 15f, 60f, "%.0fm");
        Hexa.NET.ImGui.ImGui.SameLine();
        Hexa.NET.ImGui.ImGui.Checkbox("Mobs", ref _showPlayers);
        Hexa.NET.ImGui.ImGui.SameLine();
        Hexa.NET.ImGui.ImGui.Checkbox("Items", ref _showItems);

        Hexa.NET.ImGui.ImGui.AlignTextToFramePadding();
        Hexa.NET.ImGui.ImGui.TextColored(RobustaImGuiTheme.TextMuted, "Align:");
        Hexa.NET.ImGui.ImGui.SameLine();
        if (RobustaImGuiWidgets.AccentButton("Camera", new Vector2(56f, 20f), _orientation == RadarOrientation.CameraUp))
            _orientation = RadarOrientation.CameraUp;
        if (Hexa.NET.ImGui.ImGui.IsItemHovered())
            Hexa.NET.ImGui.ImGui.SetTooltip("Camera-Up: aligns radar to viewport camera orientation");

        Hexa.NET.ImGui.ImGui.SameLine();
        if (RobustaImGuiWidgets.AccentButton("North", new Vector2(50f, 20f), _orientation == RadarOrientation.NorthUp))
            _orientation = RadarOrientation.NorthUp;
        if (Hexa.NET.ImGui.ImGui.IsItemHovered())
            Hexa.NET.ImGui.ImGui.SetTooltip("North-Up: Station North points straight up; center needle shows entity facing direction");

        Hexa.NET.ImGui.ImGui.SameLine();
        if (RobustaImGuiWidgets.AccentButton("Heading", new Vector2(58f, 20f), _orientation == RadarOrientation.HeadingUp))
            _orientation = RadarOrientation.HeadingUp;
        if (Hexa.NET.ImGui.ImGui.IsItemHovered())
            Hexa.NET.ImGui.ImGui.SetTooltip("Heading-Up: forward facing direction is straight up; 'N' rotates to show Station North");

        var avail = Hexa.NET.ImGui.ImGui.GetContentRegionAvail();
        float canvasSize = MathF.Max(120f, MathF.Min(avail.X, avail.Y));
        var cursor = Hexa.NET.ImGui.ImGui.GetCursorScreenPos();
        var center = cursor + new Vector2(avail.X * 0.5f, avail.Y * 0.5f);
        float radius = (canvasSize * 0.5f) - 8f;

        Hexa.NET.ImGui.ImGui.InvisibleButton("##RadarCanvas", avail);
        var mousePos = Hexa.NET.ImGui.ImGui.GetIO().MousePos;

        var dl = Hexa.NET.ImGui.ImGui.GetWindowDrawList();
        uint bgOuter = RobustaImGuiTheme.ColU32(new Vector4(0.06f, 0.08f, 0.12f, 0.95f));
        uint bgInner = RobustaImGuiTheme.ColU32(new Vector4(0.03f, 0.05f, 0.08f, 0.98f));
        uint ringCol = RobustaImGuiTheme.ColU32(new Vector4(0.18f, 0.42f, 0.30f, 0.65f));
        uint gridCol = RobustaImGuiTheme.ColU32(new Vector4(0.14f, 0.32f, 0.22f, 0.45f));

        dl.AddCircleFilled(center, radius + 2f, bgOuter, 64);
        dl.AddCircleFilled(center, radius, bgInner, 64);
        dl.AddCircle(center, radius, ringCol, 64, 1.5f);
        dl.AddCircle(center, radius * 0.5f, gridCol, 48, 1.0f);

        dl.AddLine(center - new Vector2(radius, 0f), center + new Vector2(radius, 0f), gridCol, 1f);
        dl.AddLine(center - new Vector2(0f, radius), center + new Vector2(0f, radius), gridCol, 1f);

        if (!RobustaImGuiManager.TryResolve<IEntityManager>(out var entMan) ||
            !RobustaImGuiManager.TryResolve<IPlayerManager>(out var playerMan) ||
            !RobustaImGuiManager.TryResolve<IEyeManager>(out var eyeMan))
        {
            Hexa.NET.ImGui.ImGui.End();
            return;
        }

        var localPlayer = playerMan.LocalSession?.AttachedEntity;
        if (localPlayer == null || !entMan.TryGetComponent<TransformComponent>(localPlayer.Value, out var xform))
        {
            Hexa.NET.ImGui.ImGui.End();
            return;
        }

        var transformSys = entMan.System<SharedTransformSystem>();
        var playerPos = transformSys.GetWorldPosition(xform);
        var mapId = xform.MapID;

        _frameCounter++;
        if (_frameCounter >= UpdateInterval)
        {
            _frameCounter = 0;
            UpdateMarkers(entMan, transformSys, localPlayer.Value, playerPos, mapId);
        }

        var playerRot = transformSys.GetWorldRotation(xform);
        var gridRot = xform.GridUid is { } gridUid
            ? transformSys.GetWorldRotation(gridUid)
            : Angle.Zero;

        var radarRot = _orientation switch
        {
            RadarOrientation.CameraUp => eyeMan.CurrentEye?.Rotation ?? Angle.Zero,
            RadarOrientation.NorthUp => -gridRot,
            RadarOrientation.HeadingUp => new Angle(Math.PI - playerRot.Theta),
            _ => eyeMan.CurrentEye?.Rotation ?? Angle.Zero
        };

        var pCol = RobustaImGuiTheme.ColU32(RobustaImGuiTheme.AccentCyan);
        dl.AddCircleFilled(center, 4f, pCol, 16);

        var relForward = radarRot.RotateVec(playerRot.ToWorldVec());
        var needleVec = new Vector2(relForward.X, -relForward.Y);
        dl.AddLine(center, center + needleVec * 9f, pCol, 2f);

        float scale = radius / _rangeMeters;

        var stationNorthWorld = gridRot.RotateVec(new Vector2(0f, 1f));
        var northDir = radarRot.RotateVec(stationNorthWorld);
        var northPos = center + new Vector2(northDir.X, -northDir.Y) * (radius - 12f);
        var nSize = Hexa.NET.ImGui.ImGui.CalcTextSize("N");
        dl.AddText(northPos - nSize * 0.5f, RobustaImGuiTheme.ColU32(RobustaImGuiTheme.AccentRed), "N");

        string? hoveredTooltip = null;

        foreach (var marker in _markers)
        {
            if (marker.Type == RadarMarkerType.Item && !_showItems)
                continue;
            if (marker.Type != RadarMarkerType.Item && !_showPlayers)
                continue;

            var offsetMeters = marker.WorldPos - playerPos;
            var relativeOffset = radarRot.RotateVec(offsetMeters);
            var uiOffset = new Vector2(relativeOffset.X, -relativeOffset.Y) * scale;

            float distancePixels = uiOffset.Length();
            var drawPos = center + uiOffset;

            if (distancePixels > radius - 6f)
            {
                uiOffset = Vector2.Normalize(uiOffset) * (radius - 6f);
                drawPos = center + uiOffset;
            }

            if (marker.Type == RadarMarkerType.Item)
            {
                dl.AddRectFilled(drawPos - new Vector2(3f, 3f), drawPos + new Vector2(3f, 3f), marker.Color);
            }
            else
            {
                dl.AddCircleFilled(drawPos, 4f, marker.Color, 12);
            }

            if (distancePixels < radius - 18f && !string.IsNullOrEmpty(marker.Name))
            {
                dl.AddText(drawPos + new Vector2(6f, -6f), marker.Color, marker.Name);
            }

            if (Vector2.DistanceSquared(mousePos, drawPos) <= 64f)
            {
                hoveredTooltip = $"{marker.Name} ({offsetMeters.Length():F1}m)";
            }
        }

        if (hoveredTooltip != null)
            Hexa.NET.ImGui.ImGui.SetTooltip(hoveredTooltip);

        Hexa.NET.ImGui.ImGui.End();
    }

    private void UpdateMarkers(
        IEntityManager entMan,
        SharedTransformSystem transformSys,
        EntityUid localPlayer,
        Vector2 playerPos,
        MapId mapId)
    {
        _markers.Clear();
        var lookup = entMan.System<EntityLookupSystem>();
        var entities = lookup.GetEntitiesInRange(mapId, playerPos, _rangeMeters);

        uint colPlayer = RobustaImGuiTheme.ColU32(RobustaImGuiTheme.AccentGreen);
        uint colSyndi = RobustaImGuiTheme.ColU32(RobustaImGuiTheme.AccentRed);
        uint colItem = RobustaImGuiTheme.ColU32(RobustaImGuiTheme.AccentGold);

        foreach (var uid in entities)
        {
            if (uid == localPlayer)
                continue;

            var targetPos = transformSys.GetWorldPosition(uid);
            var name = entMan.GetComponentOrNull<MetaDataComponent>(uid)?.EntityName ?? "Unknown";

            if (entMan.HasComponent<MobStateComponent>(uid))
            {
                uint color = colPlayer;
                var type = RadarMarkerType.Player;

                if (name.Contains("syndicate", StringComparison.OrdinalIgnoreCase) ||
                    name.Contains("operative", StringComparison.OrdinalIgnoreCase) ||
                    name.Contains("nuke", StringComparison.OrdinalIgnoreCase))
                {
                    color = colSyndi;
                    type = RadarMarkerType.Syndicate;
                }

                _markers.Add((targetPos, color, name, type));
                continue;
            }

            if (RobustaConfig.ItemSearchEnabled && !string.IsNullOrWhiteSpace(RobustaConfig.ItemSearchQuery))
            {
                if (name.Contains(RobustaConfig.ItemSearchQuery, StringComparison.OrdinalIgnoreCase))
                    _markers.Add((targetPos, colItem, name, RadarMarkerType.Item));
            }
        }
    }
}
