using Content.Shared.Construction.Prototypes;
using Content.Shared.Interaction;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;
using RobustoClient.Systems.Construction.Blueprints;

namespace RobustoClient.Systems.Construction;

public sealed partial class RobustaAutoConstructSystem
{
    public void EnqueuePhasedBlueprintBuild(
        RobustaBlueprint blueprint,
        EntityCoordinates origin,
        Direction rotation,
        BlueprintZPhase? onlyPhase = null)
        => StartPhasedBlueprintBuild(blueprint, origin, rotation, onlyPhase);

    public void StartPhasedBlueprintBuild(
        RobustaBlueprint blueprint,
        EntityCoordinates origin,
        Direction rotation,
        BlueprintZPhase? onlyPhase = null)
    {
        var gridUid = origin.EntityId;
        if (!TryComp<MapGridComponent>(gridUid, out var grid))
        {
            var foundGrid = _transform.GetGrid(origin);
            if (foundGrid == null || !TryComp(foundGrid.Value, out grid))
            {
                _log.Warning("Cannot start phased blueprint build: target coordinates are not on a valid grid.");
                SetHudBanner("[Blueprint Build Failed]", "Target coordinates are not on a valid grid.", isError: true, duration: 5f);
                return;
            }
            gridUid = foundGrid.Value;
        }

        var baseTile = _mapSystem.TileIndicesFor(gridUid, grid, origin);
        int rotSteps = BlueprintPlacementHijack.DirectionToSteps(rotation);

        var items = blueprint.Items
            .Where(i => onlyPhase == null || i.Phase == onlyPhase.Value || GetEffectiveBlueprintPhase(i) == onlyPhase.Value)
            .OrderBy(i => GetEffectiveBlueprintPhase(i))
            .ThenBy(i => GetEffectiveBlueprintSubPriority(i))
            .ThenBy(i => i.LayerOrder)
            .ThenBy(i => i.OffsetY)
            .ThenBy(i => i.OffsetX)
            .ToList();

        if (items.Count == 0)
            return;

        int added = 0;
        foreach (var item in items)
        {
            var (rx, ry) = BlueprintPlacementHijack.RotateOffset(
                item.OffsetX,
                item.OffsetY,
                blueprint.Width,
                blueprint.Height,
                rotSteps);
            var tile = baseTile + new Vector2i(rx, ry);
            var dir = BlueprintPlacementHijack.RotateDirection(
                BlueprintPlacementHijack.ParseDirection(item.Direction),
                rotSteps);

            _blueprintQueue.Add((gridUid, tile, dir, item));
            added++;
        }

        _blueprintQueue.Sort((a, b) =>
        {
            int c = GetEffectiveBlueprintPhase(a.Item).CompareTo(GetEffectiveBlueprintPhase(b.Item));
            if (c != 0) return c;
            c = GetEffectiveBlueprintSubPriority(a.Item).CompareTo(GetEffectiveBlueprintSubPriority(b.Item));
            if (c != 0) return c;
            return a.Item.LayerOrder.CompareTo(b.Item.LayerOrder);
        });

        _activeBlueprintName = blueprint.Name;
        _totalBlueprintTasksQueued = _blueprintQueue.Count;
        _completedBlueprintTasksCount = 0;
        _activeBlueprintTask = null;
        _currentTaskElapsed = 0f;
        _currentDiagnosticStatus = null;
        _hudBannerTimer = 0f;

        _log.Info($"Queued {added} phased blueprint build tasks for '{blueprint.Name}' (Phase filter: {onlyPhase?.ToString() ?? "All"}).");
    }

    private void ProcessNextQueuedBlueprintTask(EntityUid player, float dt)
    {
        var playerMapCoords = _transform.GetMapCoordinates(player);

        // 1. Prune invalid grids or verified-completed items in the current earliest effective phase
        if (_blueprintQueue.Count > 0)
        {
            var minPhase = _blueprintQueue.Min(t => GetEffectiveBlueprintPhase(t.Item));

            for (int i = _blueprintQueue.Count - 1; i >= 0; i--)
            {
                var candidate = _blueprintQueue[i];
                if (GetEffectiveBlueprintPhase(candidate.Item) != minPhase)
                    continue;

                if (!Exists(candidate.GridUid) || !TryComp<MapGridComponent>(candidate.GridUid, out var cGrid))
                {
                    _blueprintQueue.RemoveAt(i);
                    continue;
                }

                var cCoords = _mapSystem.GridTileToLocal(candidate.GridUid, cGrid, candidate.Tile);
                var cMap = _transform.ToMapCoordinates(cCoords);
                if (cMap.MapId != playerMapCoords.MapId)
                    continue;

                if (IsBlueprintItemAlreadySatisfied(cMap, candidate.Item, candidate.Dir, candidate.GridUid, cGrid, candidate.Tile))
                {
                    _blueprintQueue.RemoveAt(i);
                    _completedBlueprintTasksCount++;
                    if (_activeBlueprintTask is { } active &&
                        active.GridUid == candidate.GridUid &&
                        active.Tile == candidate.Tile &&
                        string.Equals(active.Item.EntityPrototypeId, candidate.Item.EntityPrototypeId, StringComparison.OrdinalIgnoreCase))
                    {
                        _activeBlueprintTask = null;
                        _currentTaskElapsed = 0f;
                        _currentDiagnosticStatus = null;
                    }
                }
            }
        }

        if (_blueprintQueue.Count == 0)
        {
            var bpName = _activeBlueprintName ?? "Blueprint";
            _log.Info($"Completed all blueprint build tasks for '{bpName}'.");
            SetHudBanner(
                "[Blueprint Build Complete]",
                $"Finished building '{bpName}' ({_completedBlueprintTasksCount}/{Math.Max(_completedBlueprintTasksCount, _totalBlueprintTasksQueued)} items).",
                isError: false,
                duration: 3.5f);
            _activeBlueprintName = null;
            _activeBlueprintTask = null;
            _currentTaskElapsed = 0f;
            _currentDiagnosticStatus = null;
            _itemUtil.StowActiveTrackedItem();
            return;
        }

        var currentPhase = _blueprintQueue.Min(t => GetEffectiveBlueprintPhase(t.Item));

        // Determine the earliest sub-priority still queued on each tile so a pipe (SubPriority 2)
        // is NEVER built on a tile while a filter/mixer/pump/port/vent (SubPriority 1) is still queued on that tile.
        var minSubPriorityPerTile = new Dictionary<(EntityUid GridUid, Vector2i Tile), int>();
        foreach (var entry in _blueprintQueue)
        {
            if (GetEffectiveBlueprintPhase(entry.Item) != currentPhase)
                continue;

            var key = (entry.GridUid, entry.Tile);
            int sub = GetEffectiveBlueprintSubPriority(entry.Item);
            if (!minSubPriorityPerTile.TryGetValue(key, out int curMin) || sub < curMin)
                minSubPriorityPerTile[key] = sub;
        }

        int bestIdx = -1;
        int bestSubPriority = int.MaxValue;
        float bestDist = float.MaxValue;
        int bestLayerOrder = int.MaxValue;

        int nearestAnyIdx = -1;
        float nearestAnyDist = float.MaxValue;

        for (int i = 0; i < _blueprintQueue.Count; i++)
        {
            var candidate = _blueprintQueue[i];
            if (GetEffectiveBlueprintPhase(candidate.Item) != currentPhase ||
                !Exists(candidate.GridUid) ||
                !TryComp<MapGridComponent>(candidate.GridUid, out var cGrid))
                continue;

            int subPriority = GetEffectiveBlueprintSubPriority(candidate.Item);
            if (minSubPriorityPerTile.TryGetValue((candidate.GridUid, candidate.Tile), out int tileMinSub) &&
                subPriority > tileMinSub)
            {
                // Hold off on pipes on this tile until the contraption(s) on this tile are built first
                continue;
            }

            var cCoords = _mapSystem.GridTileToLocal(candidate.GridUid, cGrid, candidate.Tile);
            var cMap = _transform.ToMapCoordinates(cCoords);
            if (cMap.MapId != playerMapCoords.MapId)
                continue;

            float d = (cMap.Position - playerMapCoords.Position).Length();
            if (d < nearestAnyDist)
            {
                nearestAnyDist = d;
                nearestAnyIdx = i;
            }

            if (d > SharedInteractionSystem.InteractionRange)
                continue;

            if (subPriority < bestSubPriority ||
                (subPriority == bestSubPriority && d < bestDist - 0.05f) ||
                (subPriority == bestSubPriority && MathF.Abs(d - bestDist) <= 0.05f && candidate.Item.LayerOrder < bestLayerOrder))
            {
                bestSubPriority = subPriority;
                bestDist = d;
                bestLayerOrder = candidate.Item.LayerOrder;
                bestIdx = i;
            }
        }

        // If no eligible task in this phase is within interaction range, inform the user on the HUD and keep rechecking up to 60s
        if (bestIdx < 0)
        {
            _currentTaskElapsed += dt;
            if (nearestAnyIdx >= 0)
            {
                var nearest = _blueprintQueue[nearestAnyIdx];
                var nGrid = Comp<MapGridComponent>(nearest.GridUid);
                _targetCoordinates = _mapSystem.GridTileToLocal(nearest.GridUid, nGrid, nearest.Tile);
                _targetMapCoords = _transform.ToMapCoordinates(_targetCoordinates);
                _currentDiagnosticStatus =
                    $"Paused: Step closer to '{nearest.Item.EntityPrototypeId}' at ({nearest.Tile.X}, {nearest.Tile.Y}) ({nearestAnyDist:0.1}m > {SharedInteractionSystem.InteractionRange:0.2}m) [{_currentTaskElapsed:0}s / {JobTimeoutSeconds:0}s]";

                if (_currentTaskElapsed >= JobTimeoutSeconds)
                {
                    FailBlueprintOrJob($"Timed out after {JobTimeoutSeconds:0}s waiting to get in range of '{nearest.Item.EntityPrototypeId}' at tile ({nearest.Tile.X}, {nearest.Tile.Y}).");
                }
            }
            return;
        }

        // Keep task in _blueprintQueue until IsBlueprintItemAlreadySatisfied confirms the structure is actually built!
        var task = _blueprintQueue[bestIdx];
        if (_activeBlueprintTask == null ||
            _activeBlueprintTask.Value.GridUid != task.GridUid ||
            _activeBlueprintTask.Value.Tile != task.Tile ||
            !string.Equals(_activeBlueprintTask.Value.Item.EntityPrototypeId, task.Item.EntityPrototypeId, StringComparison.OrdinalIgnoreCase))
        {
            _activeBlueprintTask = task;
            _currentTaskElapsed = 0f;
            _currentDiagnosticStatus = null;
        }
        else
        {
            _currentTaskElapsed += dt;
            if (_currentTaskElapsed >= JobTimeoutSeconds)
            {
                FailBlueprintOrJob($"Timed out after {JobTimeoutSeconds:0}s trying to build '{task.Item.EntityPrototypeId}' at tile ({task.Tile.X}, {task.Tile.Y}).");
                return;
            }
        }

        var grid = Comp<MapGridComponent>(task.GridUid);
        var tileCoords = _mapSystem.GridTileToLocal(task.GridUid, grid, task.Tile);
        var tileMapCoords = _transform.ToMapCoordinates(tileCoords);
        _targetCoordinates = tileCoords;
        _targetMapCoords = tileMapCoords;

        // 1. Direct stack placement (Cables, Floor Tiles) — stays in _blueprintQueue until verified placed on next recheck
        if (IsDirectStackBlueprintItem(task.Item, out var stackProtoOrType, out var matId))
        {
            if (TryExecuteDirectStackBlueprintItem(task.Item, tileCoords))
            {
                _currentDiagnosticStatus = $"Placing '{task.Item.EntityPrototypeId}' at ({task.Tile.X}, {task.Tile.Y}) [{_currentTaskElapsed:0.0}s / {JobTimeoutSeconds:0}s]";
                _waitTimer = 0.45f;
                return;
            }

            if (_currentTaskElapsed < 0.6f)
                TryOpenWornStorageSlots(player);

            _currentDiagnosticStatus = $"Waiting for required material '{matId}' ({stackProtoOrType}) [{_currentTaskElapsed:0.0}s / {JobTimeoutSeconds:0}s]";
            if (_currentTaskElapsed >= 4.0f)
            {
                FailBlueprintOrJob($"Missing required stack item '{stackProtoOrType}' ({matId}) to place '{task.Item.EntityPrototypeId}'.");
                return;
            }

            _waitTimer = 0.45f;
            return;
        }

        var recipeId = task.Item.ConstructionPrototypeId;
        var resolvedFromEntity = _solver.ResolveConstructionPrototypeForEntity(task.Item.EntityPrototypeId);
        if (string.IsNullOrEmpty(recipeId) || !_protoMan.HasIndex<ConstructionPrototype>(recipeId))
        {
            recipeId = resolvedFromEntity;
        }
        else if (!string.IsNullOrEmpty(resolvedFromEntity) &&
                 !string.Equals(recipeId, resolvedFromEntity, StringComparison.OrdinalIgnoreCase) &&
                 _solver.GetDesiredPipeLayer(task.Item.EntityPrototypeId, null) > 0 &&
                 RobustaConstructionSolver.GetPipeLayerForIdOrNode(resolvedFromEntity) > 0)
        {
            recipeId = resolvedFromEntity;
        }

        // 2. Existing unfinished frame, unanchored/unconfigured structure, or matching ghost on the tile
        var existingFrameOrGhost = FindMatchingFrameOrGhostOnTile(tileMapCoords, task.Item, task.Dir, recipeId);
        if (existingFrameOrGhost != null && Exists(existingFrameOrGhost.Value))
        {
            var existingPlan = _solver.SolveBuildPlan(existingFrameOrGhost.Value, task.Item.EntityPrototypeId);
            if (existingPlan != null && existingPlan.Steps.Count > 0)
            {
                StartJob(
                    existingFrameOrGhost.Value,
                    existingPlan,
                    resetTaskTimer: false,
                    desiredEntityProtoId: task.Item.EntityPrototypeId,
                    targetDir: task.Dir);
                return;
            }
        }

        // 3. Ensure no mismatched ghost is occupying the tile, then spawn this item's construction ghost on-demand
        ClearGhostsOnTile(tileMapCoords);

        if (!string.IsNullOrEmpty(recipeId) &&
            _protoMan.TryIndex<ConstructionPrototype>(recipeId, out var cProto) &&
            cProto.Type == ConstructionType.Structure)
        {
            if (_clientConstruction.TrySpawnGhost(cProto, tileCoords, task.Dir, out var ghostUid) &&
                ghostUid != null && Exists(ghostUid.Value))
            {
                var ghostPlan = _solver.SolveBuildPlan(cProto, includeGhostStart: true, desiredEntityProtoId: task.Item.EntityPrototypeId)
                                ?? _solver.SolveBuildPlan(ghostUid.Value, task.Item.EntityPrototypeId);
                if (ghostPlan != null && ghostPlan.Steps.Count > 0)
                {
                    StartJob(
                        ghostUid.Value,
                        ghostPlan,
                        resetTaskTimer: false,
                        desiredEntityProtoId: task.Item.EntityPrototypeId,
                        targetDir: task.Dir);
                    return;
                }
            }

            _currentDiagnosticStatus =
                $"Waiting to place ghost for '{task.Item.EntityPrototypeId}' at ({task.Tile.X}, {task.Tile.Y}) — rechecking tile [{_currentTaskElapsed:0.0}s / {JobTimeoutSeconds:0}s]";
            _waitTimer = 0.5f;
            return;
        }

        FailBlueprintOrJob($"Could not resolve a valid construction recipe for '{task.Item.EntityPrototypeId}'.");
    }

    private BlueprintZPhase GetEffectiveBlueprintPhase(BlueprintItemEntry item)
    {
        if (IsAtmosPipeContraption(item))
            return BlueprintZPhase.Phase1_UnderfloorUtilities;

        return item.Phase;
    }

    private int GetEffectiveBlueprintSubPriority(BlueprintItemEntry item)
    {
        var idLower = (item.EntityPrototypeId ?? string.Empty).ToLowerInvariant();
        if (idLower.Contains("cablehv") || idLower.Contains("cablemv") || idLower.Contains("cableapc"))
            return 0;

        // Filters, mixers, pumps, connector ports, valves, vents, scrubbers, injectors, and disposal trunks
        // MUST be built and anchored on the tile BEFORE overlapping pipes (GasPipe / DisposalPipe).
        if (IsAtmosPipeContraption(item))
            return 1;

        if (idLower.Contains("gaspipe") || idLower.Contains("disposalpipe"))
            return 2;

        return 3 + item.LayerOrder;
    }

    private bool IsAtmosPipeContraption(BlueprintItemEntry item)
    {
        var idLower = (item.EntityPrototypeId ?? string.Empty).ToLowerInvariant();
        var recipeLower = (item.ConstructionPrototypeId ?? string.Empty).ToLowerInvariant();

        if (idLower.Contains("gaspipe") || idLower.Contains("disposalpipe"))
            return false;

        string[] contraptionKeywords =
        {
            "gasfilter",
            "gasmixer",
            "gasport",
            "connectorport",
            "volumepump",
            "pressurepump",
            "passivegate",
            "gasvalve",
            "signalvalve",
            "pneumaticvalve",
            "passivevent",
            "ventpump",
            "ventscrubber",
            "outletinjector",
            "thermomachine",
            "heatexchanger",
            "gasradiator",
            "gasrecycler",
            "canisterport",
            "gastagger",
            "disposaltrunk",
            "disposalrouter",
            "disposaljunction",
            "disposaltagger"
        };

        foreach (var kw in contraptionKeywords)
        {
            if (idLower.Contains(kw, StringComparison.Ordinal) ||
                recipeLower.Contains(kw, StringComparison.Ordinal))
            {
                return true;
            }
        }

        var recipeId = item.ConstructionPrototypeId;
        if (string.IsNullOrEmpty(recipeId) || !_protoMan.HasIndex<ConstructionPrototype>(recipeId))
            recipeId = _solver.ResolveConstructionPrototypeForEntity(item.EntityPrototypeId);

        if (!string.IsNullOrEmpty(recipeId) &&
            _protoMan.TryIndex<ConstructionPrototype>(recipeId, out var cProto))
        {
            var graphId = GetStringMember(cProto, "Graph") ?? string.Empty;
            if (graphId.Equals("GasBinary", StringComparison.OrdinalIgnoreCase) ||
                graphId.Equals("GasTrinary", StringComparison.OrdinalIgnoreCase) ||
                graphId.Equals("GasUnary", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
