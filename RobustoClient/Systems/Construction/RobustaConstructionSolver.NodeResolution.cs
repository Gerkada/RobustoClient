using Content.Shared.Construction.Prototypes;
using Robust.Shared.Prototypes;

namespace RobustoClient.Systems.Construction;

public sealed partial class RobustaConstructionSolver
{
    public string? ResolveConstructionPrototypeForEntity(string? entityProtoId)
    {
        if (string.IsNullOrEmpty(entityProtoId))
            return null;

        EnsureCacheInitialized();

        if (TryLookupDirectRecipeId(entityProtoId, out var directProtoId))
            return directProtoId;

        int desiredLayer = GetDesiredPipeLayer(entityProtoId, null);
        string? baseProtoId = null;
        if (_altEntityToBaseAndLayer.TryGetValue(entityProtoId, out var baseAndLayer))
        {
            baseProtoId = baseAndLayer.BaseProtoId;
            if (desiredLayer == 0)
                desiredLayer = baseAndLayer.PipeLayer;
        }
        else if (TryExtractPipeLayerSuffix(entityProtoId, out var extractedBase, out int extractedLayer) && extractedLayer > 0)
        {
            baseProtoId = extractedBase;
            if (desiredLayer == 0)
                desiredLayer = extractedLayer;
        }

        if (!string.IsNullOrEmpty(baseProtoId) && desiredLayer > 0)
        {
            var baseRecipeId = ResolveBaseConstructionPrototypeOnly(baseProtoId);
            if (!string.IsNullOrEmpty(baseRecipeId))
            {
                if (_constructionAltLayerProtos.TryGetValue((baseRecipeId, desiredLayer), out var altRecipeId) &&
                    (_recipesById.ContainsKey(altRecipeId) || (_protoMan != null && _protoMan.HasIndex<ConstructionPrototype>(altRecipeId))))
                {
                    return altRecipeId;
                }

                var suffix = desiredLayer == 1 ? "Alt1" : "Alt2";
                if (_recipesById.TryGetValue(baseRecipeId + suffix, out var suffixedRecipe))
                    return suffixedRecipe.Id;

                if (_protoMan != null && _protoMan.TryIndex<ConstructionPrototype>(baseRecipeId + suffix, out var suffixedCProto))
                    return suffixedCProto.ID;

                if (_recipesById.TryGetValue(baseRecipeId, out var baseRecipe) &&
                    !string.IsNullOrEmpty(baseRecipe.GraphId) &&
                    !string.IsNullOrEmpty(baseRecipe.TargetNode) &&
                    _graphToConstructionProtos.TryGetValue(baseRecipe.GraphId, out var graphRecipes))
                {
                    var desiredNodeName = baseRecipe.TargetNode + suffix;
                    foreach (var cp in graphRecipes)
                    {
                        if (string.Equals(cp.TargetNode, desiredNodeName, StringComparison.OrdinalIgnoreCase))
                            return cp.Id;
                    }
                }

                return baseRecipeId;
            }
        }

        return ResolveBaseConstructionPrototypeOnly(entityProtoId);
    }

    private string? ResolveBaseConstructionPrototypeOnly(string entityProtoId)
    {
        if (string.IsNullOrEmpty(entityProtoId))
            return null;

        if (TryLookupDirectRecipeId(entityProtoId, out var directProtoId))
            return directProtoId;

        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var queue = new Queue<string>();
        queue.Enqueue(entityProtoId);
        visited.Add(entityProtoId);

        string? firstGraphFallback = null;

        while (queue.Count > 0)
        {
            var curId = queue.Dequeue();
            if (TryLookupDirectRecipeId(curId, out var mappedId))
                return mappedId;

            if (_entityToGraphNodes.TryGetValue(curId, out var graphNodes))
            {
                foreach (var (g, n) in graphNodes)
                {
                    if (!_graphToConstructionProtos.TryGetValue(g.Id, out var cProtos) || cProtos.Count == 0)
                        continue;

                    var nodeName = n.Name;
                    foreach (var cp in cProtos)
                    {
                        if (string.Equals(cp.TargetNode, nodeName, StringComparison.OrdinalIgnoreCase))
                            return cp.Id;
                    }

                    if (TryExtractPipeLayerSuffix(nodeName, out var baseNodeName, out _) &&
                        !string.Equals(baseNodeName, nodeName, StringComparison.OrdinalIgnoreCase))
                    {
                        foreach (var cp in cProtos)
                        {
                            if (string.Equals(cp.TargetNode, baseNodeName, StringComparison.OrdinalIgnoreCase))
                                return cp.Id;
                        }
                    }

                    firstGraphFallback ??= cProtos[0].Id;
                }
            }

            if (TryExtractPipeLayerSuffix(curId, out var strippedId, out _) &&
                !string.Equals(strippedId, curId, StringComparison.OrdinalIgnoreCase) &&
                visited.Add(strippedId))
            {
                queue.Enqueue(strippedId);
            }

            if (_protoMan != null && _protoMan.TryIndex<EntityPrototype>(curId, out var eProto) && eProto.Parents != null)
            {
                foreach (var parentId in eProto.Parents)
                {
                    if (visited.Add(parentId))
                        queue.Enqueue(parentId);
                }
            }
        }

        return firstGraphFallback;
    }

    public bool TryResolveGraphAndNode(
        in ConstructionTargetSnapshot snapshot,
        out IndexedConstructionGraph? graph,
        out IndexedGraphNode? node,
        out string? explicitTargetNode)
    {
        graph = null;
        node = null;
        explicitTargetNode = snapshot.ExplicitTargetNode;

        EnsureCacheInitialized();

        if (!string.IsNullOrEmpty(snapshot.GraphId) &&
            TryGetGraph(snapshot.GraphId, out graph) &&
            !string.IsNullOrEmpty(snapshot.CurrentNode) &&
            TryGetNodeByName(graph, snapshot.CurrentNode, out node))
        {
            ApplyAirlockSecurityNodeOverride(snapshot, graph, ref node);
            return true;
        }

        if (!string.IsNullOrEmpty(snapshot.PrototypeId) &&
            _entityToGraphNodes.TryGetValue(snapshot.PrototypeId, out var candidates) &&
            candidates.Count > 0)
        {
            var chosen = SelectBestSnapshotNodeCandidate(snapshot, candidates);
            graph = chosen.Graph;
            node = chosen.Node;
            ApplyAirlockSecurityNodeOverride(snapshot, graph, ref node);
            return true;
        }

        if (!snapshot.IsSecurityWiresAccessible &&
            TryFindAirlockGraphAndNode(out var airlockGraph, out var airlockNode) &&
            airlockGraph != null && airlockNode != null)
        {
            graph = airlockGraph;
            node = airlockNode;
            ApplyAirlockSecurityNodeOverride(snapshot, graph, ref node);
            return true;
        }

        return false;
    }

    public bool TryResolveGraphAndNode(
        EntityUid target,
        out IndexedConstructionGraph? graph,
        out IndexedGraphNode? node,
        out string? explicitTargetNode)
    {
        graph = null;
        node = null;
        explicitTargetNode = null;

        if (_entMan == null || !_entMan.EntityExists(target))
            return false;

        EnsureCacheInitialized();

        var constructionComp = FindComponentByName(target, "ConstructionComponent");
        if (constructionComp != null)
        {
            var graphId = GetStringMember(constructionComp, "Graph");
            var nodeName = GetStringMember(constructionComp, "Node");
            explicitTargetNode = GetStringMember(constructionComp, "TargetNode");

            if (!string.IsNullOrEmpty(graphId) &&
                TryGetGraph(graphId, out graph) &&
                !string.IsNullOrEmpty(nodeName) &&
                TryGetNodeByName(graph, nodeName, out node))
            {
                ApplyAirlockSecurityNodeOverride(target, graph, ref node);
                return true;
            }
        }

        var meta = _entMan.GetComponent<MetaDataComponent>(target);
        var proto = meta.EntityPrototype;
        if (proto != null)
        {
            var visitedProtos = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var protoQueue = new Queue<EntityPrototype>();
            protoQueue.Enqueue(proto);
            visitedProtos.Add(proto.ID);

            while (protoQueue.Count > 0)
            {
                var curProto = protoQueue.Dequeue();
                if (_entityToGraphNodes.TryGetValue(curProto.ID, out var candidates) && candidates.Count > 0)
                {
                    var chosen = SelectBestNodeCandidate(target, candidates);
                    graph = chosen.Graph;
                    node = chosen.Node;
                    ApplyAirlockSecurityNodeOverride(target, graph, ref node);
                    return true;
                }

                if (curProto.Parents != null && _protoMan != null)
                {
                    foreach (var parentId in curProto.Parents)
                    {
                        if (visitedProtos.Add(parentId) && _protoMan.TryIndex<EntityPrototype>(parentId, out var parentProto))
                            protoQueue.Enqueue(parentProto);
                    }
                }
            }
        }

        var protoIdStr = proto?.ID ?? string.Empty;
        if (!string.Equals(protoIdStr, "AirlockAssembly", StringComparison.OrdinalIgnoreCase) &&
            !protoIdStr.Contains("Windoor", StringComparison.OrdinalIgnoreCase) &&
            (FindComponentByName(target, "AirlockComponent") != null ||
             FindComponentByName(target, "WiresPanelSecurityComponent") != null) &&
            TryFindAirlockGraphAndNode(out var airlockGraph, out var airlockNode) &&
            airlockGraph != null && airlockNode != null)
        {
            graph = airlockGraph;
            node = airlockNode;
            ApplyAirlockSecurityNodeOverride(target, graph, ref node);
            return true;
        }

        if (!string.Equals(protoIdStr, "ComputerFrame", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(protoIdStr, "ComputerBroken", StringComparison.OrdinalIgnoreCase) &&
            (FindComponentByName(target, "ComputerComponent") != null ||
             FindComponentByName(target, "SharedComputerComponent") != null) &&
            TryFindComputerGraphAndNode(out var compGraph, out var compNode) &&
            compGraph != null && compNode != null)
        {
            graph = compGraph;
            node = compNode;
            return true;
        }

        if (!string.Equals(protoIdStr, "MachineFrame", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(protoIdStr, "ComputerFrame", StringComparison.OrdinalIgnoreCase) &&
            (FindComponentByName(target, "MachineComponent") != null ||
             FindComponentByName(target, "LatheComponent") != null ||
             FindComponentByName(target, "SharedLatheComponent") != null ||
             (_container != null && _container.TryGetContainer(target, "machine_board", out _)) ||
             (_container != null && _container.TryGetContainer(target, "machine_parts", out _))) &&
            TryFindMachineGraphAndNode(out var machineGraph, out var machineNode) &&
            machineGraph != null && machineNode != null)
        {
            graph = machineGraph;
            node = machineNode;
            return true;
        }

        return false;
    }

    private bool TryLookupDirectRecipeId(string protoId, out string? recipeId)
    {
        if (_entityToConstructionProto.TryGetValue(protoId, out recipeId))
            return true;

        if (_recipesById.TryGetValue(protoId, out var sameIdRecipe))
        {
            recipeId = sameIdRecipe.Id;
            return true;
        }

        if (_protoMan != null && _protoMan.TryIndex<ConstructionPrototype>(protoId, out var sameIdProto))
        {
            recipeId = sameIdProto.ID;
            return true;
        }

        recipeId = null;
        return false;
    }

    private void ApplyAirlockSecurityNodeOverride(
        in ConstructionTargetSnapshot snapshot,
        IndexedConstructionGraph graph,
        ref IndexedGraphNode? node)
    {
        if (!string.Equals(graph.Id, "Airlock", StringComparison.OrdinalIgnoreCase))
            return;

        var secNodeName = !snapshot.IsSecurityWiresAccessible ? ResolveCurrentSecurityNode(snapshot, graph) : null;
        ApplyAirlockSecurityOverride(graph, snapshot.IsSecurityWiresAccessible, secNodeName, ref node);
    }

    private void ApplyAirlockSecurityNodeOverride(
        EntityUid target,
        IndexedConstructionGraph graph,
        ref IndexedGraphNode? node)
    {
        if (!string.Equals(graph.Id, "Airlock", StringComparison.OrdinalIgnoreCase))
            return;

        var secComp = FindComponentByName(target, "WiresPanelSecurityComponent");
        if (secComp == null)
            return;

        bool wiresAccessible = GetMemberValue(secComp, "WiresAccessible") is not false;
        var secNodeName = !wiresAccessible ? ResolveCurrentSecurityNode(target, secComp, graph) : null;
        ApplyAirlockSecurityOverride(graph, wiresAccessible, secNodeName, ref node);
    }

    private void ApplyAirlockSecurityOverride(
        IndexedConstructionGraph graph,
        bool wiresAccessible,
        string? secNodeName,
        ref IndexedGraphNode? node)
    {
        if (!wiresAccessible)
        {
            if (!string.IsNullOrEmpty(secNodeName) && TryGetNodeByName(graph, secNodeName, out var secNode))
                node = secNode;
        }
        else if (node != null && node.Name.Contains("Security", StringComparison.OrdinalIgnoreCase))
        {
            if (TryGetNodeByName(graph, "airlock", out var airlockNode))
                node = airlockNode;
        }
    }

    public bool CanVerifyInPlaceNodeTransition(EntityUid uid, string graphId)
    {
        if (_entMan == null || !_entMan.EntityExists(uid))
            return false;

        var protoId = _entMan.GetComponent<MetaDataComponent>(uid).EntityPrototype?.ID ?? string.Empty;
        if (string.Equals(graphId, "Machine", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(protoId, "MachineFrame", StringComparison.OrdinalIgnoreCase))
        {
            return TryGetSpriteLayerZeroState(uid, out _) || TryGetMachineFrameVisualState(uid, out _);
        }

        if (string.Equals(graphId, "Computer", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(protoId, "ComputerFrame", StringComparison.OrdinalIgnoreCase))
        {
            return TryGetSpriteLayerZeroState(uid, out _);
        }

        if (string.Equals(graphId, "Airlock", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(protoId, "AirlockAssembly", StringComparison.OrdinalIgnoreCase) &&
            FindComponentByName(uid, "WiresPanelSecurityComponent") != null)
        {
            return true;
        }

        return false;
    }

    private static (IndexedConstructionGraph Graph, IndexedGraphNode Node) SelectBestSnapshotNodeCandidate(
        in ConstructionTargetSnapshot snapshot,
        List<(IndexedConstructionGraph Graph, IndexedGraphNode Node)> candidates)
    {
        if (candidates.Count == 1)
            return candidates[0];

        if (snapshot.HasMachineFrameBoard)
        {
            foreach (var candidate in candidates)
            {
                if (string.Equals(candidate.Node.Name, "machineFrame", StringComparison.OrdinalIgnoreCase))
                    return candidate;
            }
        }
        else
        {
            foreach (var candidate in candidates)
            {
                var name = candidate.Node.Name;
                if (name.Equals("frameUnsecured", StringComparison.OrdinalIgnoreCase) ||
                    name.Equals("assembly", StringComparison.OrdinalIgnoreCase) ||
                    name.Equals("missingWires", StringComparison.OrdinalIgnoreCase) ||
                    name.Equals("girder", StringComparison.OrdinalIgnoreCase))
                {
                    return candidate;
                }
            }
        }

        return candidates[0];
    }

    private (IndexedConstructionGraph Graph, IndexedGraphNode Node) SelectBestNodeCandidate(
        EntityUid target,
        List<(IndexedConstructionGraph Graph, IndexedGraphNode Node)> candidates)
    {
        if (candidates.Count == 1)
            return candidates[0];

        bool hasBoard = _container != null &&
                        _container.TryGetContainer(target, "board", out var boardCont) &&
                        boardCont.ContainedEntities.Count > 0;
        if (!hasBoard && HasMachineFrameBoard(target))
            hasBoard = true;

        TryGetSpriteLayerZeroState(target, out var spriteState);

        if (candidates.Any(c => string.Equals(c.Graph.Id, "Computer", StringComparison.OrdinalIgnoreCase)))
        {
            string? desiredCompFrameNode = spriteState switch
            {
                "4" => "monitorUnsecured",
                "3" => "monitorMissing",
                "2" => "missingWires",
                "1" => "boardUnsecured",
                "0" => "frameUnsecured",
                _ => null
            };

            if (desiredCompFrameNode != null)
            {
                foreach (var candidate in candidates)
                {
                    if (string.Equals(candidate.Node.Name, desiredCompFrameNode, StringComparison.OrdinalIgnoreCase))
                        return candidate;
                }
            }

            if (hasBoard)
            {
                foreach (var candidate in candidates)
                {
                    var name = candidate.Node.Name;
                    if (name.Equals("monitorUnsecured", StringComparison.OrdinalIgnoreCase) ||
                        name.Equals("boardUnsecured", StringComparison.OrdinalIgnoreCase))
                    {
                        return candidate;
                    }
                }
            }
        }

        if (candidates.Any(c => string.Equals(c.Graph.Id, "Machine", StringComparison.OrdinalIgnoreCase)))
        {
            bool isMissingWires = false;
            bool isWiredFrame = false;

            if (hasBoard)
            {
                isWiredFrame = true;
            }
            else if (!string.IsNullOrEmpty(spriteState))
            {
                if (spriteState.EndsWith("_0", StringComparison.OrdinalIgnoreCase))
                    isMissingWires = true;
                else
                    isWiredFrame = true;
            }
            else if (TryGetMachineFrameVisualState(target, out int vState))
            {
                if (vState <= 1)
                    isMissingWires = true;
                else
                    isWiredFrame = true;
            }

            if (isWiredFrame)
            {
                foreach (var candidate in candidates)
                {
                    if (string.Equals(candidate.Node.Name, "machineFrame", StringComparison.OrdinalIgnoreCase))
                        return candidate;
                }
            }
            else if (isMissingWires)
            {
                foreach (var candidate in candidates)
                {
                    if (string.Equals(candidate.Node.Name, "missingWires", StringComparison.OrdinalIgnoreCase))
                        return candidate;
                }
            }
        }

        if (!hasBoard)
        {
            foreach (var candidate in candidates)
            {
                var name = candidate.Node.Name;
                if (name.Equals("frameUnsecured", StringComparison.OrdinalIgnoreCase) ||
                    name.Equals("assembly", StringComparison.OrdinalIgnoreCase) ||
                    name.Equals("missingWires", StringComparison.OrdinalIgnoreCase) ||
                    name.Equals("girder", StringComparison.OrdinalIgnoreCase))
                {
                    return candidate;
                }
            }
        }

        return candidates[0];
    }
}
