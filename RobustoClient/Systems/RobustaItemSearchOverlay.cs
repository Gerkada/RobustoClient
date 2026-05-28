using System;
using System.Collections.Generic;
using System.Numerics;
using Robust.Client.Graphics;
using Robust.Client.Player;
using Robust.Client.ResourceManagement;
using Robust.Shared.Enums;
using Robust.Shared.GameObjects;
using Robust.Shared.IoC;
using Robust.Shared.Map;

namespace RobustoClient.Systems;

public sealed class RobustaItemSearchOverlay : Overlay
{
    private readonly IEntityManager _entMan;
    private readonly IEyeManager _eyeManager;
    private readonly IPlayerManager _playerManager;
    private readonly SharedTransformSystem _transform;
    private readonly Font _font;
    private EntityLookupSystem? _entityLookup;

    // Кэш для производительности
    private readonly List<(EntityUid Uid, Vector2 ScreenPos, string Name)> _cachedItems = new();
    private MapId _lastMapId = MapId.Nullspace;
    private int _frameCounter = 0;
    private const int CacheUpdateInterval = 15; 
    private Vector2 _lastPlayerPos = Vector2.Zero;

    public override OverlaySpace Space => OverlaySpace.ScreenSpace; // Исправили магическую цифру

    public RobustaItemSearchOverlay()
    {
        _entMan = IoCManager.Resolve<IEntityManager>();
        _eyeManager = IoCManager.Resolve<IEyeManager>();
        _playerManager = IoCManager.Resolve<IPlayerManager>();
        _transform = _entMan.System<SharedTransformSystem>();
        
        // Берем надежный дефолтный шрифт
        var cache = IoCManager.Resolve<IResourceCache>();
        _font = new VectorFont(cache.GetResource<FontResource>("/Fonts/NotoSans/NotoSans-Regular.ttf"), 10);
    }

    protected override void Draw(in OverlayDrawArgs args)
    {
        if (!RobustaConfig.ItemSearchEnabled || string.IsNullOrWhiteSpace(RobustaConfig.ItemSearchQuery))
            return;

        // ИСПРАВЛЕНИЕ: Правильный локальный игрок
        var local = _playerManager.LocalSession?.AttachedEntity;
        if (local == null)
            return;

        _entityLookup ??= _entMan.System<EntityLookupSystem>();

        if (!_entMan.TryGetComponent(local.Value, out TransformComponent? localXform))
            return;

        var mapId = localXform.MapID;
        var worldViewport = _eyeManager.GetWorldViewport();
        var playerWorldPos = _transform.GetWorldPosition(localXform); // ИСПРАВЛЕНИЕ: Безопасные координаты

        var movedDistSq = (playerWorldPos - _lastPlayerPos).LengthSquared();
        bool playerMoved = movedDistSq > 4f;

        _frameCounter++;
        if (_frameCounter >= CacheUpdateInterval || _lastMapId != mapId || playerMoved)
        {
            _frameCounter = 0;
            _lastMapId = mapId;
            _lastPlayerPos = playerWorldPos;
            UpdateCache(mapId, worldViewport);
        }

        var localScreen = args.ViewportControl?.WorldToScreen(playerWorldPos);
        if (localScreen == null) return;

        // Цвет линий и текста (например, голубой)
        var color = Color.Cyan;

        foreach (var (uid, screenPos, name) in _cachedItems)
        {
            // Рисуем линию от нас к предмету
            args.ScreenHandle.DrawLine(localScreen.Value, screenPos, color);
            // Рисуем название предмета
            args.ScreenHandle.DrawString(_font, screenPos - new Vector2(0f, 10f), name, color);
        }
    }

    private void UpdateCache(MapId mapId, Box2 worldViewport)
    {
        _cachedItems.Clear();
        if (mapId == MapId.Nullspace || _entityLookup == null) return;

        var queryLower = RobustaConfig.ItemSearchQuery.ToLowerInvariant();

        try
        {
            var entities = _entityLookup.GetEntitiesIntersecting(mapId, worldViewport);
            foreach (var uid in entities)
            {
                if (!_entMan.TryGetComponent(uid, out MetaDataComponent? meta) ||
                    !_entMan.TryGetComponent(uid, out TransformComponent? xform))
                    continue;

                var name = meta.EntityName;
                if (!name.ToLowerInvariant().Contains(queryLower))
                    continue;

                // Безопасное получение позиции даже в контейнерах
                var worldPos = _transform.GetWorldPosition(xform);
                var screenPos = _eyeManager.WorldToScreen(worldPos);
                
                _cachedItems.Add((uid, screenPos, name));
            }
        }
        catch { /* Игнорируем ошибки при поиске */ }
    }
}