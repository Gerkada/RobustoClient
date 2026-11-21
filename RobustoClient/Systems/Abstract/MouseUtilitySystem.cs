using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using Content.Client.Gameplay;
using Content.Client.Viewport;
using Robust.Client.Graphics;
using Robust.Client.Input;
using Robust.Client.Player;
using Robust.Client.State;
using Robust.Client.UserInterface;
using Robust.Client.UserInterface.CustomControls;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;
using Robust.Shared.Maths;

namespace RobustoClient.Systems.Abstract;

/// <summary>
/// Shared cursor resolution: the entity under the mouse, the map position under the mouse, and
/// range searches around it.
/// </summary>
public sealed class MouseUtilitySystem : EntitySystem
{
    public const float DefaultRange = 1f;

    [Dependency] private readonly IInputManager _input = default!;
    [Dependency] private readonly IEyeManager _eye = default!;
    [Dependency] private readonly IPlayerManager _player = default!;
    [Dependency] private readonly IStateManager _state = default!;
    [Dependency] private readonly IUserInterfaceManager _ui = default!;
    [Dependency] private readonly EntityLookupSystem _lookup = default!;
    [Dependency] private readonly SharedTransformSystem _transform = default!;
    [Dependency] private readonly SharedMapSystem _mapSystem = default!;
    [Dependency] private readonly RobustaAimSystem _aim = default!;

    private bool _pointerCaptured;
    private ScreenCoordinates _capturedScreen;
    private MapCoordinates? _capturedMap;
    private EntityUid? _capturedHovered;

    public ScreenCoordinates MouseScreenPosition => _pointerCaptured ? _capturedScreen : _input.MouseScreenPosition;

    /// <summary>
    /// Freezes cursor queries at the current pointer. A radial menu opens over a target, the player
    /// moves the mouse to pick an action, and that action has to act on the target under the cursor
    /// when the menu opened. Pair with <see cref="ReleasePointer"/>.
    /// </summary>
    public void CapturePointer()
    {
        ReleasePointer();

        var screen = _input.MouseScreenPosition;
        if (!screen.IsValid)
            return;

        _capturedMap = TryGetMouseMapCoordinates(out var cursor) ? cursor : null;
        _capturedHovered = GetHoveredEntity(range: 0f);
        _capturedScreen = screen;
        _pointerCaptured = true;
    }

    public void ReleasePointer()
    {
        _pointerCaptured = false;
        _capturedScreen = default;
        _capturedMap = null;
        _capturedHovered = null;
    }

    /// <summary>Cursor position in world space, or false when the cursor is off map.</summary>
    public bool TryGetMouseMapCoordinates(out MapCoordinates coords)
    {
        if (_pointerCaptured)
        {
            coords = _capturedMap ?? default;
            return _capturedMap is { } captured && captured.MapId != MapId.Nullspace;
        }

        coords = default;
        if (!_input.MouseScreenPosition.IsValid)
            return false;

        coords = _ui.CurrentlyHovered is IViewportControl vp
            ? vp.PixelToMap(_input.MouseScreenPosition.Position)
            : _eye.PixelToMap(_input.MouseScreenPosition);

        return coords.MapId != MapId.Nullspace;
    }

    /// <summary>
    /// The entity the cursor is over, falls back to the closest
    /// entity within <paramref name="range"/> when nothing is directly hovered.
    /// </summary>
    public EntityUid? GetHoveredEntity(float range = DefaultRange, Type[]? whitelist = null, Type[]? exclude = null)
    {
        if (_pointerCaptured)
            return ResolveCapturedHover(range, whitelist, exclude);

        if (!_input.MouseScreenPosition.IsValid)
            return null;

        if (GetDirectlyHoveredEntity() is { } direct && PassesFilter(direct, whitelist, exclude))
            return direct;

        if (range <= 0f || !TryGetMouseMapCoordinates(out var cursor))
            return null;

        return GetClosestToCursor(cursor, range, whitelist, exclude);
    }

    private EntityUid? ResolveCapturedHover(float range, Type[]? whitelist, Type[]? exclude)
    {
        if (TryAcceptCapturedEntity(_capturedHovered, EntityManager.EntityExists, HasComp, whitelist, exclude, out var accepted))
            return accepted;

        if (range > 0f && _capturedMap is { } cursor && cursor.MapId != MapId.Nullspace)
            return GetClosestToCursor(cursor, range, whitelist, exclude);

        return null;
    }

    internal static bool TryAcceptCapturedEntity(
        EntityUid? captured,
        Func<EntityUid, bool> exists,
        Func<EntityUid, Type, bool> hasComp,
        Type[]? whitelist,
        Type[]? exclude,
        out EntityUid accepted)
    {
        if (captured is { } cap && exists(cap) && PassesFilter(cap, whitelist, exclude, hasComp))
        {
            accepted = cap;
            return true;
        }

        accepted = default;
        return false;
    }

    public bool TryGetHoveredEntity(
        [NotNullWhen(true)] out EntityUid entity,
        float range = DefaultRange,
        Type[]? whitelist = null,
        Type[]? exclude = null)
    {
        if (GetHoveredEntity(range, whitelist, exclude) is { } hovered)
        {
            entity = hovered;
            return true;
        }

        entity = default;
        return false;
    }

    public HashSet<EntityUid> GetEntitiesNearCursor(
        float range = DefaultRange,
        Type[]? whitelist = null,
        Type[]? exclude = null)
    {
        if (!TryGetMouseMapCoordinates(out var cursor))
            return [];

        return GetEntitiesNearCursor(cursor, range, whitelist, exclude);
    }

    public HashSet<EntityUid> GetEntitiesNearCursor(
        MapCoordinates cursor,
        float range = DefaultRange,
        Type[]? whitelist = null,
        Type[]? exclude = null)
    {
        var result = new HashSet<EntityUid>();
        foreach (var uid in _lookup.GetEntitiesInRange(cursor, range, LookupFlags.Uncontained))
        {
            if (PassesFilter(uid, whitelist, exclude))
                result.Add(uid);
        }

        return result;
    }

    public EntityUid? GetClosestToCursor(
        float range = DefaultRange,
        Type[]? whitelist = null,
        Type[]? exclude = null)
    {
        return TryGetMouseMapCoordinates(out var cursor)
            ? GetClosestToCursor(cursor, range, whitelist, exclude)
            : null;
    }

    public EntityUid? GetClosestToCursor(
        MapCoordinates cursor,
        float range = DefaultRange,
        Type[]? whitelist = null,
        Type[]? exclude = null)
    {
        EntityUid? best = null;
        var bestDistance = float.MaxValue;

        foreach (var uid in _lookup.GetEntitiesInRange(cursor, range, LookupFlags.Uncontained))
        {
            if (!PassesFilter(uid, whitelist, exclude))
                continue;

            var pos = _transform.GetMapCoordinates(uid);
            if (pos.MapId != cursor.MapId)
                continue;

            var distance = (pos.Position - cursor.Position).LengthSquared();
            if (distance >= bestDistance)
                continue;

            bestDistance = distance;
            best = uid;
        }

        return best;
    }

    /// <summary>Locked aim target, then the entity under the cursor, then the local player.</summary>
    public bool TryResolveTarget(
        [NotNullWhen(true)] out EntityUid target,
        float range = DefaultRange,
        Type[]? whitelist = null,
        Type[]? exclude = null)
    {
        if (_aim.LockedTarget is { } locked && EntityManager.EntityExists(locked))
        {
            target = locked;
            return true;
        }

        if (GetHoveredEntity(range, whitelist, exclude) is { } hovered)
        {
            target = hovered;
            return true;
        }

        if (_player.LocalSession?.AttachedEntity is { } local && EntityManager.EntityExists(local))
        {
            target = local;
            return true;
        }

        target = default;
        return false;
    }

    public bool IsInRange(EntityUid target, float range)
    {
        if (_player.LocalSession?.AttachedEntity is not { } local || !EntityManager.EntityExists(local))
            return false;

        var localPos = _transform.GetMapCoordinates(local);
        var targetPos = _transform.GetMapCoordinates(target);
        return localPos.MapId == targetPos.MapId && (localPos.Position - targetPos.Position).Length() <= range;
    }

    /// <summary>Grid and tile under the cursor, preferring the local player's grid.</summary>
    public bool TryGetCursorGridTile(out EntityUid gridUid, out Vector2i tileIndices)
    {
        gridUid = EntityUid.Invalid;
        tileIndices = default;

        if (!TryGetMouseMapCoordinates(out var cursor))
            return false;

        if (_player.LocalEntity is { } player && EntityManager.EntityExists(player))
        {
            var playerXform = Transform(player);
            if (playerXform.GridUid is { } pGrid && EntityManager.EntityExists(pGrid) &&
                TryComp<MapGridComponent>(pGrid, out var gridComp))
            {
                gridUid = pGrid;
                tileIndices = _mapSystem.WorldToTile(pGrid, gridComp, cursor.Position);
                return true;
            }
        }

        if (_transform.GetGrid(_transform.ToCoordinates(cursor)) is { } cursorGrid &&
            EntityManager.EntityExists(cursorGrid) &&
            TryComp<MapGridComponent>(cursorGrid, out var cGridComp))
        {
            gridUid = cursorGrid;
            tileIndices = _mapSystem.WorldToTile(cursorGrid, cGridComp, cursor.Position);
            return true;
        }

        return false;
    }

    /// <summary>The top-most clickable entity under the cursor.</summary>
    private EntityUid? GetDirectlyHoveredEntity()
    {
        if (_state.CurrentState is not GameplayStateBase screen)
            return null;

        if (_ui.CurrentlyHovered is not IViewportControl vp)
            return null;

        var mouseMap = vp.PixelToMap(MouseScreenPosition.Position);
        return vp is ScalingViewport svp
            ? screen.GetClickedEntity(mouseMap, svp.Eye)
            : screen.GetClickedEntity(mouseMap);
    }

    private bool PassesFilter(EntityUid uid, Type[]? whitelist, Type[]? exclude) => PassesFilter(uid, whitelist, exclude, HasComp);

    internal static bool PassesFilter(
        EntityUid uid,
        Type[]? whitelist,
        Type[]? exclude,
        Func<EntityUid, Type, bool> hasComp)
    {
        if (exclude != null)
        {
            foreach (var type in exclude)
            {
                if (hasComp(uid, type))
                    return false;
            }
        }

        if (whitelist == null || whitelist.Length == 0)
            return true;

        foreach (var type in whitelist)
        {
            if (hasComp(uid, type))
                return true;
        }

        return false;
    }
}
