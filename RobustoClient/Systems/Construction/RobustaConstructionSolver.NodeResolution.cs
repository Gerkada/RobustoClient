using Content.Shared.Construction;
using Content.Shared.Construction.Prototypes;
using Robust.Shared.Prototypes;

namespace RobustoClient.Systems.Construction;

public sealed partial class RobustaConstructionSolver
{
    public string? ResolveConstructionPrototypeForEntity(string entityProtoId)
    {
        if (string.IsNullOrEmpty(entityProtoId))
            return null;

        EnsureCacheInitialized();

        if (_entityToConstructionProto.TryGetValue(entityProtoId, out var directProtoId))
            return directProtoId;

        if (_protoMan.TryIndex<ConstructionPrototype>(entityProtoId, out var sameIdProto))
            return sameIdProto.ID;

        // Check if this is an Alt1 / Alt2 / Secondary / Tertiary pipe-layer entity prototype
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
                    _protoMan.HasIndex<ConstructionPrototype>(altRecipeId))
                {
                    return altRecipeId;
                }

                var suffix = desiredLayer == 1 ? "Alt1" : "Alt2";
                if (_protoMan.TryIndex<ConstructionPrototype>(baseRecipeId + suffix, out var suffixedCProto))
                    return suffixedCProto.ID;

                if (_protoMan.TryIndex<ConstructionPrototype>(baseRecipeId, out var baseCProto))
                {
                    var baseGraphId = GetStringMember(baseCProto, "Graph");
                    var baseTargetNode = GetStringMember(baseCProto, "TargetNode");
                    if (!string.IsNullOrEmpty(baseGraphId) &&
                        !string.IsNullOrEmpty(baseTargetNode) &&
                        _graphToConstructionProtos.TryGetValue(baseGraphId, out var graphCProtos))
                    {
                        var desiredNodeName = baseTargetNode + suffix;
                        foreach (var cp in graphCProtos)
                        {
                            if (string.Equals(GetStringMember(cp, "TargetNode"), desiredNodeName, StringComparison.OrdinalIgnoreCase))
                                return cp.ID;
                        }
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

        if (_entityToConstructionProto.TryGetValue(entityProtoId, out var directProtoId))
            return directProtoId;

        if (_protoMan.TryIndex<ConstructionPrototype>(entityProtoId, out var sameIdProto))
            return sameIdProto.ID;

        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var queue = new Queue<string>();
        queue.Enqueue(entityProtoId);
        visited.Add(entityProtoId);

        string? firstGraphFallback = null;

        while (queue.Count > 0)
        {
            var curId = queue.Dequeue();
            if (_entityToConstructionProto.TryGetValue(curId, out var mappedId))
                return mappedId;

            if (_protoMan.TryIndex<ConstructionPrototype>(curId, out var curSameIdProto))
                return curSameIdProto.ID;

            if (_entityToGraphNodes.TryGetValue(curId, out var graphNodes))
            {
                foreach (var (g, n) in graphNodes)
                {
                    if (!_graphToConstructionProtos.TryGetValue(g.ID, out var cProtos) || cProtos.Count == 0)
                        continue;

                    var nodeName = GetNodeName(n);
                    foreach (var cp in cProtos)
                    {
                        if (string.Equals(GetStringMember(cp, "TargetNode"), nodeName, StringComparison.OrdinalIgnoreCase))
                            return cp.ID;
                    }

                    if (TryExtractPipeLayerSuffix(nodeName, out var baseNodeName, out _) &&
                        !string.Equals(baseNodeName, nodeName, StringComparison.OrdinalIgnoreCase))
                    {
                        foreach (var cp in cProtos)
                        {
                            if (string.Equals(GetStringMember(cp, "TargetNode"), baseNodeName, StringComparison.OrdinalIgnoreCase))
                                return cp.ID;
                        }
                    }

                    firstGraphFallback ??= cProtos[0].ID;
                }
            }

            if (TryExtractPipeLayerSuffix(curId, out var strippedId, out _) &&
                !string.Equals(strippedId, curId, StringComparison.OrdinalIgnoreCase) &&
                visited.Add(strippedId))
            {
                queue.Enqueue(strippedId);
            }

            if (_protoMan.TryIndex<EntityPrototype>(curId, out var eProto) && eProto.Parents != null)
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
        EntityUid target,
        out ConstructionGraphPrototype? graph,
        out ConstructionGraphNode? node,
        out string? explicitTargetNode)
    {
        graph = null;
        node = null;
        explicitTargetNode = null;

        if (!_entMan.EntityExists(target))
            return false;

        EnsureCacheInitialized();

        // 1. Check client-visible ConstructionComponent first
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

        // 2. Fallback to EntityPrototype ID -> (Graph, Node) lookup
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

                if (curProto.Parents != null)
                {
                    foreach (var parentId in curProto.Parents)
                    {
                        if (visitedProtos.Add(parentId) && _protoMan.TryIndex<EntityPrototype>(parentId, out var parentProto))
                            protoQueue.Enqueue(parentProto);
                    }
                }
            }
        }

        // 3. Live-component fallback for Airlocks, Consoles (ComputerComponent), and Machines (MachineComponent)
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
             _container.TryGetContainer(target, "machine_board", out _) ||
             _container.TryGetContainer(target, "machine_parts", out _)) &&
            TryFindMachineGraphAndNode(out var machineGraph, out var machineNode) &&
            machineGraph != null && machineNode != null)
        {
            graph = machineGraph;
            node = machineNode;
            return true;
        }

        return false;
    }

    private void ApplyAirlockSecurityNodeOverride(
        EntityUid target,
        ConstructionGraphPrototype graph,
        ref ConstructionGraphNode? node)
    {
        if (!string.Equals(graph.ID, "Airlock", StringComparison.OrdinalIgnoreCase))
            return;

        var secComp = FindComponentByName(target, "WiresPanelSecurityComponent");
        if (secComp == null)
            return;

        if (GetMemberValue(secComp, "WiresAccessible") is false)
        {
            var secNodeName = ResolveCurrentSecurityNode(target, secComp, graph);
            if (!string.IsNullOrEmpty(secNodeName) && TryGetNodeByName(graph, secNodeName, out var secNode))
                node = secNode;
        }
        else if (node != null && GetNodeName(node).Contains("Security", StringComparison.OrdinalIgnoreCase))
        {
            if (TryGetNodeByName(graph, "airlock", out var airlockNode))
                node = airlockNode;
        }
    }

    public bool CanVerifyInPlaceNodeTransition(EntityUid uid, string graphId)
    {
        if (!_entMan.EntityExists(uid))
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

    private (ConstructionGraphPrototype Graph, ConstructionGraphNode Node) SelectBestNodeCandidate(
        EntityUid target,
        List<(ConstructionGraphPrototype Graph, ConstructionGraphNode Node)> candidates)
    {
        if (candidates.Count == 1)
            return candidates[0];

        bool hasBoard = _container.TryGetContainer(target, "board", out var boardCont) && boardCont.ContainedEntities.Count > 0;
        if (!hasBoard && HasMachineFrameBoard(target))
            hasBoard = true;

        TryGetSpriteLayerZeroState(target, out var spriteState);

        // Disambiguate ComputerFrame stages ("0" -> frameUnsecured, "1" -> boardUnsecured, "2" -> missingWires, "3" -> monitorMissing, "4" -> monitorUnsecured)
        if (candidates.Any(c => string.Equals(c.Graph.ID, "Computer", StringComparison.OrdinalIgnoreCase)))
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
                    if (string.Equals(GetNodeName(candidate.Node), desiredCompFrameNode, StringComparison.OrdinalIgnoreCase))
                        return candidate;
                }
            }

            if (hasBoard)
            {
                foreach (var candidate in candidates)
                {
                    var name = GetNodeName(candidate.Node);
                    if (name.Equals("monitorUnsecured", StringComparison.OrdinalIgnoreCase) ||
                        name.Equals("boardUnsecured", StringComparison.OrdinalIgnoreCase))
                    {
                        return candidate;
                    }
                }
            }
        }

        // Disambiguate MachineFrame stages ("missingWires" vs "machineFrame")
        if (candidates.Any(c => string.Equals(c.Graph.ID, "Machine", StringComparison.OrdinalIgnoreCase)))
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
                    if (string.Equals(GetNodeName(candidate.Node), "machineFrame", StringComparison.OrdinalIgnoreCase))
                        return candidate;
                }
            }
            else if (isMissingWires)
            {
                foreach (var candidate in candidates)
                {
                    if (string.Equals(GetNodeName(candidate.Node), "missingWires", StringComparison.OrdinalIgnoreCase))
                        return candidate;
                }
            }
        }

        if (!hasBoard)
        {
            foreach (var candidate in candidates)
            {
                var name = GetNodeName(candidate.Node);
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
