using System.Collections;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;
using RobustoClient.Reflection;
using RobustoClient.Systems.Construction.Blueprints;

namespace RobustoClient.Systems.Construction;

/// <summary>Outcome of trying to lift the floor covering off a tile before building on it.</summary>
internal enum FloorPryResult
{
    /// <summary>Nothing to lift: the tile is bare, empty, or already uncovered.</summary>
    Clear,

    /// <summary>A pry was issued; the caller should wait for the DoAfter before building.</summary>
    Issued,

    /// <summary>The tile needs lifting but no crowbar could be equipped.</summary>
    NoTool
}

public sealed partial class RobustaAutoConstructSystem
{
    /// <summary>
    /// Whether a blueprint item is a pipe, the kind that is built on the subfloor layer.
    /// </summary>
    internal static bool IsPipeBlueprintItem(BlueprintItemEntry item)
    {
        var idLower = (item.EntityPrototypeId ?? string.Empty).ToLowerInvariant();
        return idLower.Contains("gaspipe") || idLower.Contains("disposalpipe");
    }

    /// <summary>
    /// Whether a blueprint item is built on the subfloor layer, so its floor covering has to come up first.
    /// </summary>
    private bool IsUnderfloorPipeItem(BlueprintItemEntry item)
        => IsPipeBlueprintItem(item) || IsAtmosPipeContraption(item);

    /// <summary>
    /// Whether a construction graph builds on the subfloor layer, so the floor covering has to come up first.
    /// </summary>
    /// <remarks>
    /// Covers the gas pipe graphs (GasPipe and its Alt1/Alt2 variants), the disposal pipe graph, and the
    /// binary/trinary/unary contraption graphs that anchor under the floor.
    /// </remarks>
    internal static bool IsUnderfloorPipeGraph(string? graphId)
    {
        if (string.IsNullOrEmpty(graphId))
            return false;

        return graphId.StartsWith("GasPipe", StringComparison.OrdinalIgnoreCase) ||
               graphId.StartsWith("DisposalPipe", StringComparison.OrdinalIgnoreCase) ||
               graphId.Equals("GasBinary", StringComparison.OrdinalIgnoreCase) ||
               graphId.Equals("GasTrinary", StringComparison.OrdinalIgnoreCase) ||
               graphId.Equals("GasUnary", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Lifts the floor covering under <paramref name="target"/> so a pipe can be built there.
    /// </summary>
    private FloorPryResult EnsureFloorCoveringPried(EntityUid target, EntityCoordinates targetCoords)
    {
        var gridUid = _transform.GetGrid(targetCoords);
        if (gridUid == null || !TryComp<MapGridComponent>(gridUid.Value, out var grid))
            return FloorPryResult.Clear;

        var tile = _mapSystem.TileIndicesFor(gridUid.Value, grid, targetCoords);
        return EnsureFloorCoveringPried(gridUid.Value, tile, targetCoords);
    }

    /// <summary>
    /// Lifts the floor covering off <paramref name="tile"/> so a pipe can be built underneath it.
    /// </summary>
    private FloorPryResult EnsureFloorCoveringPried(EntityUid gridUid, Vector2i tile, EntityCoordinates tileCoords)
    {
        if (!TryComp<MapGridComponent>(gridUid, out var grid))
            return FloorPryResult.Clear;

        if (!_mapSystem.TryGetTileRef(gridUid, grid, tile, out var tileRef) || tileRef.Tile.IsEmpty)
            return FloorPryResult.Clear;

        if (!IsCrowbarableFloorCovering(_tileDefMan[tileRef.Tile.TypeId]))
            return FloorPryResult.Clear;

        if (!_itemUtil.TryEquipByPredicate(
                uid => IsToolReadyForQuality(uid, "Prying") &&
                       FindComponentByName(uid, "ToolTileCompatibleComponent") != null,
                "Crowbar"))
        {
            return FloorPryResult.NoTool;
        }

        _itemUtil.EnqueueClickCoordinates(tileCoords);
        return FloorPryResult.Issued;
    }

    /// <summary>
    /// Whether a tile definition is a floor covering a crowbar can lift, rather than bare subfloor plating.
    /// </summary>
    internal static bool IsCrowbarableFloorCovering(ITileDefinition tileDef)
    {
        // Subfloor plating is the base layer rather than a covering, and has no base turf to fall back to.
        if (Reflect.Get(tileDef, "IsSubFloor", ReflectOptions.Any) is true)
            return false;

        if (Reflect.Get(tileDef, "BaseTurf", ReflectOptions.Any) == null)
            return false;

        if (Reflect.Get(tileDef, "DeconstructTools", ReflectOptions.Any) is not IEnumerable tools)
            return false;

        foreach (var tool in tools)
        {
            if (string.Equals(tool?.ToString(), "Prying", StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }
}