using Content.Client.Construction;
using Content.Shared.Interaction;
using Robust.Shared.Map;

namespace RobustoClient.Systems.Construction;

public sealed partial class RobustaAutoConstructSystem
{
    private EntityUid? FindTargetAtCursor(
        EntityUid player,
        MapCoordinates cursorMapCoords,
        out EntityUid? outOfRangeTarget,
        out float outOfRangeDist)
    {
        outOfRangeTarget = null;
        outOfRangeDist = 0f;

        var playerPos = _transform.GetMapCoordinates(player);
        var nearby = _lookup.GetEntitiesInRange(cursorMapCoords, 0.65f, LookupFlags.Uncontained);

        // 1. Check for a ConstructionGhostComponent first
        EntityUid? bestGhost = null;
        float bestGhostDist = float.MaxValue;
        foreach (var uid in nearby)
        {
            if (!Exists(uid) || !HasGhostComponent(uid))
                continue;

            var pos = _transform.GetMapCoordinates(uid);
            if (pos.MapId != playerPos.MapId)
                continue;

            float distToCursor = (pos.Position - cursorMapCoords.Position).LengthSquared();
            if (distToCursor < bestGhostDist)
            {
                bestGhostDist = distToCursor;
                bestGhost = uid;
            }
        }

        if (bestGhost.HasValue)
            return bestGhost.Value;

        // 2. Otherwise find the topmost constructible/deconstructible entity within interaction range
        EntityUid? bestEntity = null;
        float bestScore = float.MaxValue;
        float bestOutOfRangeScore = float.MaxValue;

        foreach (var uid in nearby)
        {
            if (uid == player || !Exists(uid) || IsHiddenSubfloorEntity(uid))
                continue;

            var pos = _transform.GetMapCoordinates(uid);
            if (pos.MapId != playerPos.MapId)
                continue;

            if (!_solver.TryResolveGraphAndNode(uid, out _, out _, out _))
                continue;

            float score = (pos.Position - cursorMapCoords.Position).LengthSquared();
            if (IsSubfloorEntity(uid))
                score += 0.02f;
            else if (IsGhostOrUnfinishedFrame(uid))
                score -= 0.01f;

            float playerDist = (pos.Position - playerPos.Position).Length();
            if (playerDist > SharedInteractionSystem.InteractionRange)
            {
                if (score < bestOutOfRangeScore)
                {
                    bestOutOfRangeScore = score;
                    outOfRangeTarget = uid;
                    outOfRangeDist = playerDist;
                }
                continue;
            }

            if (score < bestScore)
            {
                bestScore = score;
                bestEntity = uid;
            }
        }

        return bestEntity;
    }

    private bool IsSubfloorEntity(EntityUid uid)
    {
        foreach (var comp in EntityManager.GetComponents(uid))
        {
            if (string.Equals(comp.GetType().Name, "SubFloorHideComponent", StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    private bool IsHiddenSubfloorEntity(EntityUid uid)
    {
        foreach (var comp in EntityManager.GetComponents(uid))
        {
            if (string.Equals(comp.GetType().Name, "SubFloorHideComponent", StringComparison.OrdinalIgnoreCase))
            {
                if (GetBoolMember(comp, "IsUnderCover"))
                    return true;
            }
        }
        return false;
    }

    private bool TryRelocateTargetEntity(out EntityUid found)
        => TryRelocateReplacementEntity(EntityUid.Invalid, out found, ignorePreExisting: true);

    private bool TryRelocateReplacementEntity(EntityUid excludeEntity, out EntityUid found, bool ignorePreExisting = true)
    {
        found = EntityUid.Invalid;
        if (_targetMapCoords.MapId == MapId.Nullspace)
            return false;

        var candidates = _lookup.GetEntitiesInRange(_targetMapCoords, 0.45f, LookupFlags.Uncontained);
        float bestDist = float.MaxValue;

        foreach (var uid in candidates)
        {
            if (uid == excludeEntity || !Exists(uid) || Deleted(uid) || HasGhostComponent(uid))
                continue;

            if (ignorePreExisting && _preExistingTileEntities.Contains(uid))
                continue;

            if (!_solver.TryResolveGraphAndNode(uid, out var graph, out _, out _) || graph == null)
                continue;

            if (_activePlan != null &&
                !string.Equals(graph.ID, _activePlan.GraphId, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var pos = _transform.GetMapCoordinates(uid);
            float dist = (pos.Position - _targetMapCoords.Position).LengthSquared();
            if (dist < bestDist)
            {
                bestDist = dist;
                found = uid;
            }
        }

        return found.IsValid();
    }

    private bool TryRelocateNonGhostTarget(out EntityUid found)
        => TryRelocateReplacementEntity(_currentTarget, out found, ignorePreExisting: true);

    private bool IsGhostOrUnfinishedFrame(EntityUid uid)
    {
        if (HasGhostComponent(uid))
            return true;

        var meta = MetaData(uid);
        var protoId = meta.EntityPrototype?.ID ?? string.Empty;
        var name = meta.EntityName;

        foreach (var kw in UnfinishedFrameKeywords)
        {
            if (protoId.Contains(kw, StringComparison.OrdinalIgnoreCase) ||
                name.Contains(kw, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        if (_solver.TryResolveGraphAndNode(uid, out _, out var node, out _) && node != null)
        {
            var nodeName = node.Name;
            foreach (var kw in UnfinishedFrameKeywords)
            {
                if (nodeName.Contains(kw, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
        }

        return false;
    }

    private bool HasGhostComponent(EntityUid uid)
    {
        if (HasComp<ConstructionGhostComponent>(uid))
            return true;

        foreach (var comp in EntityManager.GetComponents(uid))
        {
            if (string.Equals(comp.GetType().Name, "ConstructionGhostComponent", StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }
}
