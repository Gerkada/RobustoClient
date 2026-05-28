using System;
using System.Collections.Generic;
using System.Numerics;
using Robust.Client.Graphics;
using Robust.Client.Player;
using Robust.Client.ResourceManagement;
using Robust.Client.UserInterface;
using Robust.Shared.Enums;
using Robust.Shared.GameObjects;
using Robust.Shared.IoC;
using Robust.Shared.Maths;
using Robust.Shared.Map;
using Content.Shared.Mobs.Components;

namespace RobustoClient.UI;

public enum RadarMarkerType { Player, Item, Syndicate, Admin }

public sealed class RobustaRadarControl : Control
{
    [Dependency] private readonly IEntityManager _entMan = default!;
    [Dependency] private readonly IPlayerManager _player = default!;
    [Dependency] private readonly IResourceCache _cache = default!;

    private readonly SharedTransformSystem _transform;
    private readonly Font _font;

    // Radar settings
    private const float RadarRangeMeters = 30f; 
    private int _frameCounter = 0;
    private const int UpdateInterval = 10; 

    private readonly List<(Vector2 WorldPos, Color Color, string Name, RadarMarkerType Type)> _markers = new();

    public RobustaRadarControl()
    {
        IoCManager.InjectDependencies(this);
        _transform = _entMan.System<SharedTransformSystem>();
        _font = new VectorFont(_cache.GetResource<FontResource>("/Fonts/NotoSans/NotoSans-Regular.ttf"), 10);
        MinSize = new Vector2(250, 250);
    }

    protected override void Draw(DrawingHandleScreen handle)
    {
        base.Draw(handle);

        var size = PixelSize;
        var center = new Vector2(size.X / 2f, size.Y / 2f);
        var radius = MathF.Min(size.X, size.Y) / 2f - 10f;

        handle.DrawCircle(center, radius + 2f, new Color(0.1f, 0.1f, 0.1f, 0.9f));
        handle.DrawCircle(center, radius, new Color(0.05f, 0.05f, 0.05f, 0.95f));
        handle.DrawCircle(center, radius, new Color(0.2f, 0.4f, 0.2f, 0.8f), filled: false); 

        var gridColor = new Color(0.1f, 0.3f, 0.1f, 0.8f);
        handle.DrawLine(center - new Vector2(radius, 0), center + new Vector2(radius, 0), gridColor);
        handle.DrawLine(center - new Vector2(0, radius), center + new Vector2(0, radius), gridColor);

        var localPlayer = _player.LocalSession?.AttachedEntity;
        if (localPlayer == null || !_entMan.TryGetComponent<TransformComponent>(localPlayer.Value, out var xform))
            return;

        var playerPos = _transform.GetWorldPosition(xform);
        var mapId = xform.MapID;

        _frameCounter++;
        if (_frameCounter >= UpdateInterval)
        {
            _frameCounter = 0;
            UpdateMarkers(playerPos, mapId);
        }

        handle.DrawCircle(center, 4f, Color.Cyan);

        var scale = radius / RadarRangeMeters;

        foreach (var marker in _markers)
        {
            var offsetMeters = marker.WorldPos - playerPos;
            var uiOffset = new Vector2(offsetMeters.X, -offsetMeters.Y) * scale;
            
            var distancePixels = uiOffset.Length();
            var drawPos = center + uiOffset;

            if (distancePixels > radius - 6f)
            {
                uiOffset = Vector2.Normalize(uiOffset) * (radius - 6f);
                drawPos = center + uiOffset;
            }

            if (marker.Type == RadarMarkerType.Item)
            {
                handle.DrawRect(new UIBox2(drawPos.X - 3, drawPos.Y - 3, drawPos.X + 3, drawPos.Y + 3), marker.Color);
            }
            else
            {
                handle.DrawCircle(drawPos, 4f, marker.Color);
            }

            if (distancePixels < radius - 20f && !string.IsNullOrEmpty(marker.Name))
            {
                handle.DrawString(_font, drawPos + new Vector2(6f, -6f), marker.Name, marker.Color);
            }
        }
    }

    private void UpdateMarkers(Vector2 playerPos, MapId mapId)
    {
        _markers.Clear();
        var localPlayer = _player.LocalSession?.AttachedEntity;
        var lookup = _entMan.System<EntityLookupSystem>();
        
        var entities = lookup.GetEntitiesInRange(mapId, playerPos, RadarRangeMeters);

        foreach (var uid in entities)
        {
            if (uid == localPlayer) continue;

            var targetPos = _transform.GetWorldPosition(uid);
            var name = _entMan.GetComponentOrNull<MetaDataComponent>(uid)?.EntityName ?? "Unknown";

            // 1. Search for players and mobs
            if (_entMan.HasComponent<MobStateComponent>(uid))
            {
                Color color = Color.LimeGreen;
                var type = RadarMarkerType.Player;

                // HEURISTICS INSTEAD OF COMPONENTS: Searching for Syndicate keywords in the name
                var nameLower = name.ToLowerInvariant();
                if (nameLower.Contains("syndicate") || nameLower.Contains("operative") || nameLower.Contains("nuke"))
                {
                    color = Color.Red;
                    type = RadarMarkerType.Syndicate;
                }

                _markers.Add((targetPos, color, name, type));
                continue;
            }

            // 2. Search for items
            if (RobustaConfig.ItemSearchEnabled && !string.IsNullOrWhiteSpace(RobustaConfig.ItemSearchQuery))
            {
                if (name.Contains(RobustaConfig.ItemSearchQuery, StringComparison.OrdinalIgnoreCase))
                {
                    _markers.Add((targetPos, Color.Yellow, name, RadarMarkerType.Item));
                }
            }
        }
    }
}