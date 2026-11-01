using Content.Shared.Construction.Prototypes;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;
using RobustoClient.Systems.Construction.Blueprints;

namespace RobustoClient.Systems.Construction;

public sealed partial class RobustaAutoConstructSystem
{
    private bool IsBlueprintItemAlreadySatisfied(
        MapCoordinates tileMapCoords,
        BlueprintItemEntry item,
        Direction expectedDir,
        EntityUid? gridUid = null,
        MapGridComponent? grid = null,
        Vector2i? tileIndices = null)
    {
        if (string.IsNullOrEmpty(item.EntityPrototypeId))
            return false;

        var idLower = item.EntityPrototypeId.ToLowerInvariant();
        if ((idLower.Contains("floortile") || idLower == "tileitemsteel") &&
            gridUid.HasValue && grid != null && tileIndices.HasValue)
        {
            return _mapSystem.TryGetTileRef(gridUid.Value, grid, tileIndices.Value, out var tileRef) &&
                   !tileRef.Tile.IsEmpty;
        }

        var desiredBaseId = _solver.NormalizeBaseEntityPrototypeId(item.EntityPrototypeId);
        int desiredLayer = _solver.GetDesiredPipeLayer(item.EntityPrototypeId, item.ConstructionPrototypeId);
        bool isAtmosOrPipe = desiredLayer > 0 ||
                             IsAtmosPipeContraption(item) ||
                             idLower.Contains("pipe") ||
                             idLower.Contains("manifold");

        bool ignoreDirection = !isAtmosOrPipe &&
                               (desiredBaseId.Contains("Cable", StringComparison.OrdinalIgnoreCase) ||
                                desiredBaseId.Contains("Wall", StringComparison.OrdinalIgnoreCase) ||
                                desiredBaseId.Contains("Girder", StringComparison.OrdinalIgnoreCase) ||
                                desiredBaseId.Contains("Grille", StringComparison.OrdinalIgnoreCase) ||
                                desiredBaseId.Contains("Airlock", StringComparison.OrdinalIgnoreCase) ||
                                desiredBaseId.Contains("Firelock", StringComparison.OrdinalIgnoreCase) ||
                                desiredBaseId.Contains("Shutter", StringComparison.OrdinalIgnoreCase) ||
                                (desiredBaseId.Contains("Window", StringComparison.OrdinalIgnoreCase) && !desiredBaseId.Contains("Directional", StringComparison.OrdinalIgnoreCase)) ||
                                desiredBaseId.Contains("Table", StringComparison.OrdinalIgnoreCase));

        string? expectedGraphId = null;
        string? expectedTargetNode = null;
        var recipeId = item.ConstructionPrototypeId;
        if (string.IsNullOrEmpty(recipeId) || !_protoMan.HasIndex<ConstructionPrototype>(recipeId))
            recipeId = _solver.ResolveConstructionPrototypeForEntity(item.EntityPrototypeId);

        if (!string.IsNullOrEmpty(recipeId) &&
            _protoMan.TryIndex<ConstructionPrototype>(recipeId, out var cProto))
        {
            expectedGraphId = GetStringMember(cProto, "Graph");
            expectedTargetNode = GetStringMember(cProto, "TargetNode");
            if (GetMemberValue(cProto, "CanRotate") is false)
                ignoreDirection = true;
        }

        var entities = _lookup.GetEntitiesInRange(tileMapCoords, 0.35f, LookupFlags.Uncontained);
        foreach (var uid in entities)
        {
            if (!Exists(uid) || Deleted(uid) || HasGhostComponent(uid))
                continue;

            var xform = Transform(uid);
            if (!xform.Anchored)
                continue;

            if (GetBoolMember(xform, "NoRot"))
                ignoreDirection = true;

            var protoId = MetaData(uid).EntityPrototype?.ID;
            if (string.IsNullOrEmpty(protoId))
                continue;

            var entBaseId = _solver.NormalizeBaseEntityPrototypeId(protoId);
            bool protoMatched = string.Equals(protoId, item.EntityPrototypeId, StringComparison.OrdinalIgnoreCase) ||
                                string.Equals(entBaseId, desiredBaseId, StringComparison.OrdinalIgnoreCase);

            // Also match board-constructed Airlocks (e.g. Airlock built from any DoorElectronics) or completed graph target nodes
            if (!protoMatched &&
                !isAtmosOrPipe &&
                !IsGhostOrUnfinishedFrame(uid) &&
                !string.IsNullOrEmpty(expectedGraphId) &&
                _solver.TryResolveGraphAndNode(uid, out var uGraph, out var uNode, out _) &&
                uGraph != null && uNode != null &&
                string.Equals(uGraph.ID, expectedGraphId, StringComparison.OrdinalIgnoreCase))
            {
                if (string.Equals(expectedGraphId, "Airlock", StringComparison.OrdinalIgnoreCase) ||
                    (!string.IsNullOrEmpty(expectedTargetNode) &&
                     string.Equals(uNode.Name, expectedTargetNode, StringComparison.OrdinalIgnoreCase)))
                {
                    protoMatched = true;
                }
            }

            if (!protoMatched)
                continue;

            if (!ignoreDirection && xform.LocalRotation.GetCardinalDir() != expectedDir)
                continue;

            if (isAtmosOrPipe)
            {
                int actualLayer = _solver.DetectEntityPipeLayer(uid, out _);
                if (actualLayer != desiredLayer)
                    continue;
            }

            return true;
        }

        return false;
    }

    private EntityUid? FindMatchingFrameOrGhostOnTile(
        MapCoordinates tileMapCoords,
        BlueprintItemEntry item,
        Direction expectedDir,
        string? expectedRecipeId)
    {
        var entities = _lookup.GetEntitiesInRange(tileMapCoords, 0.35f, LookupFlags.Uncontained).ToList();
        EntityUid? matchedGhost = null;
        EntityUid? unanchoredOrWrongLayerMatch = null;

        var desiredBaseId = _solver.NormalizeBaseEntityPrototypeId(item.EntityPrototypeId ?? string.Empty);
        int desiredLayer = _solver.GetDesiredPipeLayer(item.EntityPrototypeId, item.ConstructionPrototypeId);

        foreach (var uid in entities)
        {
            if (!Exists(uid) || Deleted(uid))
                continue;

            if (HasGhostComponent(uid))
            {
                var ghostComp = FindComponentByName(uid, "ConstructionGhostComponent");
                var ghostProto = GetMemberValue(ghostComp, "Prototype") as ConstructionPrototype;
                var ghostProtoId = ghostProto?.ID ?? GetStringMember(ghostComp, "Prototype");
                var ghostDir = Transform(uid).LocalRotation.GetCardinalDir();

                if (!string.IsNullOrEmpty(expectedRecipeId) &&
                    string.Equals(ghostProtoId, expectedRecipeId, StringComparison.OrdinalIgnoreCase) &&
                    ghostDir == expectedDir &&
                    matchedGhost == null)
                {
                    matchedGhost = uid;
                }
                else
                {
                    // Clear mismatched ghost so it doesn't block multi-layer placement on this tile
                    ClearGhostEntity(uid);
                }
                continue;
            }

            // Only match an existing non-ghost pipe/contraption if it is UNANCHORED (still being built on this tile).
            // An already-anchored structure on another layer belongs to that other layer's blueprint entry and must NEVER be reused!
            var xform = Transform(uid);
            if (!xform.Anchored)
            {
                var protoId = MetaData(uid).EntityPrototype?.ID ?? string.Empty;
                if (!string.IsNullOrEmpty(protoId) && !string.IsNullOrEmpty(desiredBaseId))
                {
                    var entBaseId = _solver.NormalizeBaseEntityPrototypeId(protoId);
                    bool isContraption = IsAtmosPipeContraption(item);
                    bool protoMatches = isContraption
                        ? (string.Equals(protoId, item.EntityPrototypeId, StringComparison.OrdinalIgnoreCase) ||
                           string.Equals(entBaseId, desiredBaseId, StringComparison.OrdinalIgnoreCase))
                        : string.Equals(protoId, item.EntityPrototypeId, StringComparison.OrdinalIgnoreCase);

                    if (protoMatches && xform.LocalRotation.GetCardinalDir() == expectedDir)
                    {
                        int actualLayer = _solver.DetectEntityPipeLayer(uid, out _);
                        if (actualLayer == desiredLayer || isContraption)
                        {
                            unanchoredOrWrongLayerMatch = uid;
                        }
                    }
                }
            }

            if (IsGhostOrUnfinishedFrame(uid) &&
                _solver.TryResolveGraphAndNode(uid, out var frameGraph, out _, out _) &&
                frameGraph != null)
            {
                if (!string.IsNullOrEmpty(expectedRecipeId) &&
                    _protoMan.TryIndex<ConstructionPrototype>(expectedRecipeId, out var cProto))
                {
                    var expectedGraphId = GetStringMember(cProto, "Graph");
                    if (!string.Equals(frameGraph.ID, expectedGraphId, StringComparison.OrdinalIgnoreCase))
                        continue;
                }
                return uid;
            }
        }

        return unanchoredOrWrongLayerMatch ?? matchedGhost;
    }

    private void ClearGhostsOnTile(MapCoordinates tileMapCoords)
    {
        var entities = _lookup.GetEntitiesInRange(tileMapCoords, 0.35f, LookupFlags.Uncontained).ToList();
        foreach (var uid in entities)
        {
            if (Exists(uid) && HasGhostComponent(uid))
                ClearGhostEntity(uid);
        }
    }

    private void ClearGhostEntity(EntityUid ghostUid)
    {
        if (!Exists(ghostUid) || !HasGhostComponent(ghostUid))
            return;

        try
        {
            var ghostComp = FindComponentByName(ghostUid, "ConstructionGhostComponent");
            int ghostId = ghostComp != null && GetMemberValue(ghostComp, "GhostId") is int gid
                ? gid
                : ghostUid.GetHashCode();
            _clientConstruction.ClearGhost(ghostId);
        }
        catch
        {
            // Ignore if ClearGhost fails
        }

        if (Exists(ghostUid) && IsClientSide(ghostUid))
        {
            EntityManager.QueueDeleteEntity(ghostUid);
        }
    }

    private static bool IsDirectStackBlueprintItem(
        BlueprintItemEntry item,
        out string? stackProtoOrType,
        out string? matId)
    {
        stackProtoOrType = null;
        matId = null;
        var idLower = (item.EntityPrototypeId ?? string.Empty).ToLowerInvariant();

        if (idLower.Contains("cablehv"))
        {
            stackProtoOrType = "CableHVStack";
            matId = "CableHV";
            return true;
        }
        if (idLower.Contains("cablemv"))
        {
            stackProtoOrType = "CableMVStack";
            matId = "CableMV";
            return true;
        }
        if (idLower.Contains("cableapc"))
        {
            stackProtoOrType = "CableApcExtensionStack";
            matId = "Cable";
            return true;
        }
        if (idLower.Contains("floortile") || idLower == "tileitemsteel")
        {
            stackProtoOrType = "FloorTileItemSteel";
            matId = "FloorTile";
            return true;
        }

        return false;
    }

    private bool TryExecuteDirectStackBlueprintItem(BlueprintItemEntry item, EntityCoordinates tileCoords)
    {
        if (!IsDirectStackBlueprintItem(item, out var stackProtoOrType, out var matId) ||
            string.IsNullOrEmpty(stackProtoOrType) || string.IsNullOrEmpty(matId))
        {
            return false;
        }

        bool equipped = _itemUtil.TryEquipItem(stackProtoOrType, 1) ||
                        _itemUtil.TryEquipByPredicate(
                            uid => MatchesMaterialOrStack(uid, matId),
                            $"DirectStack:{stackProtoOrType}",
                            1) ||
                        (_player.LocalEntity is { } player &&
                         TryEquipFromDeepInventoryOrVicinity(player, uid => MatchesMaterialOrStack(uid, matId), 1));

        if (!equipped)
            return false;

        _itemUtil.EnqueueClickCoordinates(tileCoords);
        return true;
    }
}
