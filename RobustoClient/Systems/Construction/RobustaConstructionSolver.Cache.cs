using System.Collections;
using Content.Shared.Construction;
using Content.Shared.Construction.Prototypes;
using Robust.Shared.Prototypes;

namespace RobustoClient.Systems.Construction;

public sealed partial class RobustaConstructionSolver
{
    public void LoadIndexedGraphs(
        IEnumerable<IndexedConstructionGraph> graphs,
        IEnumerable<IndexedConstructionRecipe>? recipes = null)
    {
        ClearCache();
        _cacheInitialized = true;

        foreach (var graph in graphs)
        {
            _indexedGraphs[graph.Id] = graph;
            foreach (var node in graph.Nodes.Values)
            {
                if (!string.IsNullOrEmpty(node.EntityId))
                {
                    RegisterEntityNodeMapping(node.EntityId, graph, node);
                    RegisterEntityIdPipeLayerSuffix(node.EntityId);
                }
            }
        }

        if (recipes != null)
        {
            foreach (var recipe in recipes)
            {
                RegisterIndexedRecipe(recipe);
            }
        }

        RegisterStandardBaseNodeMappings();
    }

    public void EnsureCacheInitialized()
    {
        if (_cacheInitialized || _protoMan == null)
            return;

        var indexedGraphs = new List<IndexedConstructionGraph>();
        foreach (var rawGraph in _protoMan.EnumeratePrototypes<ConstructionGraphPrototype>())
        {
            indexedGraphs.Add(BuildIndexedGraph(rawGraph));
        }

        var indexedRecipes = new List<IndexedConstructionRecipe>();
        foreach (var cProto in _protoMan.EnumeratePrototypes<ConstructionPrototype>())
        {
            if (BuildIndexedRecipe(cProto) is { } recipe)
                indexedRecipes.Add(recipe);
        }

        LoadIndexedGraphs(indexedGraphs, indexedRecipes);

        TryFindComputerGraphAndNode(out var compGraph, out var compNode);
        TryFindMachineGraphAndNode(out var machineGraph, out var machineNode);
        TryFindAirlockGraphAndNode(out var airlockGraph, out var airlockNode);

        foreach (var entityProto in _protoMan.EnumeratePrototypes<EntityPrototype>())
        {
            RegisterEntityAlternativePipeLayers(entityProto);

            if (TryGetConstructionMappingFromPrototype(entityProto, out var graphId, out var nodeName) &&
                TryGetGraph(graphId, out var graph) &&
                TryGetNodeByName(graph, nodeName, out var node))
            {
                RegisterEntityNodeMapping(entityProto.ID, graph, node);
            }

            if (machineGraph != null && machineNode != null)
            {
                if (_itemUtil.TryGetPrototypeComponent<object>(entityProto, "MachineBoard", out var machineBoard) &&
                    machineBoard != null)
                {
                    var machineProtoId = GetStringMember(machineBoard, "Prototype")
                                         ?? GetStringMember(machineBoard, "MachinePrototype")
                                         ?? GetStringMember(machineBoard, "Machine");
                    if (!string.IsNullOrEmpty(machineProtoId))
                        RegisterEntityNodeMapping(machineProtoId, machineGraph, machineNode);
                }

                if (!string.Equals(entityProto.ID, "MachineFrame", StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(entityProto.ID, "ComputerFrame", StringComparison.OrdinalIgnoreCase) &&
                    (_itemUtil.HasPrototypeComponent(entityProto, "Machine") ||
                     _itemUtil.HasPrototypeComponent(entityProto, "Lathe") ||
                     _itemUtil.HasPrototypeComponent(entityProto, "SharedLathe") ||
                     HasPrototypeContainer(entityProto, "machine_board") ||
                     HasPrototypeContainer(entityProto, "machine_parts")))
                {
                    RegisterEntityNodeMapping(entityProto.ID, machineGraph, machineNode);
                }
            }

            if (compGraph != null && compNode != null)
            {
                if (_itemUtil.TryGetPrototypeComponent<object>(entityProto, "ComputerBoard", out var compBoard) &&
                    compBoard != null)
                {
                    var compProtoId = GetStringMember(compBoard, "Prototype");
                    if (!string.IsNullOrEmpty(compProtoId))
                        RegisterEntityNodeMapping(compProtoId, compGraph, compNode);
                }

                if (!string.Equals(entityProto.ID, "ComputerFrame", StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(entityProto.ID, "ComputerBroken", StringComparison.OrdinalIgnoreCase) &&
                    (_itemUtil.HasPrototypeComponent(entityProto, "Computer") ||
                     _itemUtil.HasPrototypeComponent(entityProto, "SharedComputer")))
                {
                    RegisterEntityNodeMapping(entityProto.ID, compGraph, compNode);
                }
            }

            if (airlockGraph != null && airlockNode != null &&
                !string.Equals(entityProto.ID, "AirlockAssembly", StringComparison.OrdinalIgnoreCase) &&
                !entityProto.ID.Contains("Windoor", StringComparison.OrdinalIgnoreCase) &&
                (_itemUtil.HasPrototypeComponent(entityProto, "Airlock") ||
                 _itemUtil.HasPrototypeComponent(entityProto, "WiresPanelSecurity")))
            {
                RegisterEntityNodeMapping(entityProto.ID, airlockGraph, airlockNode);
            }

            if (_itemUtil.TryGetPrototypeComponent<object>(entityProto, "ElectronicsBoard", out var elecBoard) &&
                elecBoard != null)
            {
                var elecProtoId = GetStringMember(elecBoard, "Prototype");
                if (!string.IsNullOrEmpty(elecProtoId))
                {
                    foreach (var g in _indexedGraphs.Values)
                    {
                        foreach (var n in g.Nodes.Values)
                        {
                            if (n.IsBoardNodeEntity)
                                RegisterEntityNodeMapping(elecProtoId, g, n);
                        }
                    }
                }
            }
        }
    }

    private void RegisterIndexedRecipe(IndexedConstructionRecipe recipe)
    {
        if (string.IsNullOrEmpty(recipe.GraphId))
            return;

        _recipesById[recipe.Id] = recipe;

        if (!_graphToConstructionProtos.TryGetValue(recipe.GraphId, out var list))
        {
            list = new List<IndexedConstructionRecipe>();
            _graphToConstructionProtos[recipe.GraphId] = list;
        }
        list.Add(recipe);

        if (!string.IsNullOrEmpty(recipe.TargetNode) &&
            TryGetGraph(recipe.GraphId, out var cGraph) &&
            TryGetNodeByName(cGraph, recipe.TargetNode, out var targetNode))
        {
            var targetEntId = targetNode.EntityId;
            if (!string.IsNullOrEmpty(targetEntId) && !_entityToConstructionProto.ContainsKey(targetEntId))
                _entityToConstructionProto[targetEntId] = recipe.Id;
        }

        if (recipe.AlternativePrototypes != null)
        {
            foreach (var (layer, altId) in recipe.AlternativePrototypes)
            {
                if (layer > 0 && !string.IsNullOrEmpty(altId))
                    _constructionAltLayerProtos[(recipe.Id, layer)] = altId;
            }
        }
    }

    private void RegisterStandardBaseNodeMappings()
    {
        TryFindComputerGraphAndNode(out var compGraph, out var compNode);
        TryFindMachineGraphAndNode(out var machineGraph, out var machineNode);
        TryFindAirlockGraphAndNode(out var airlockGraph, out var airlockNode);

        if (airlockGraph != null && airlockNode != null)
        {
            foreach (var baseAirlockId in BaseAirlockPrototypeIds)
            {
                RegisterEntityNodeMapping(baseAirlockId, airlockGraph, airlockNode);
            }
        }

        if (compGraph != null)
        {
            string[] compFrameNodeNames =
            {
                "frameUnsecured",
                "boardUnsecured",
                "missingWires",
                "monitorMissing",
                "monitorUnsecured"
            };
            foreach (var cfNodeName in compFrameNodeNames)
            {
                if (TryGetNodeByName(compGraph, cfNodeName, out var cfNode))
                    RegisterEntityNodeMapping("ComputerFrame", compGraph, cfNode);
            }

            if (compNode != null)
            {
                foreach (var baseConsoleId in BaseConsolePrototypeIds)
                {
                    RegisterEntityNodeMapping(baseConsoleId, compGraph, compNode);
                }
            }
        }

        if (machineGraph != null)
        {
            string[] machineFrameNodeNames = { "missingWires", "machineFrame" };
            foreach (var mfNodeName in machineFrameNodeNames)
            {
                if (TryGetNodeByName(machineGraph, mfNodeName, out var mfNode))
                    RegisterEntityNodeMapping("MachineFrame", machineGraph, mfNode);
            }

            if (machineNode != null)
            {
                foreach (var baseMachineId in BaseMachinePrototypeIds)
                {
                    RegisterEntityNodeMapping(baseMachineId, machineGraph, machineNode);
                }
            }
        }
    }

    private IndexedConstructionGraph BuildIndexedGraph(ConstructionGraphPrototype rawGraph)
    {
        var nodes = new Dictionary<string, IndexedGraphNode>(StringComparer.OrdinalIgnoreCase);
        var startNode = GetStringMember(rawGraph, "Start") ?? "start";

        foreach (var rawNode in EnumerateRawGraphNodes(rawGraph))
        {
            var nodeName = GetRawNodeName(rawNode);
            if (string.IsNullOrEmpty(nodeName))
                continue;

            var entityId = GetRawNodeEntityId(rawNode, null);
            var entObj = GetMemberValue(rawNode, "Entity") ?? GetMemberValue(rawNode, "_entity");
            bool isBoardNode = entObj != null &&
                               entObj.GetType().Name.Contains("BoardNodeEntity", StringComparison.OrdinalIgnoreCase);

            string? securityExamine = null;
            var actions = GetMemberValue(rawNode, "Actions") as IEnumerable
                          ?? GetMemberValue(rawNode, "_actions") as IEnumerable;
            if (actions != null)
            {
                foreach (var act in actions)
                {
                    if (act != null && GetStringMember(act, "Examine") is { Length: > 0 } ex)
                    {
                        securityExamine = ex;
                        break;
                    }
                }
            }

            bool isAnchorable = KnownAnchorableFrameNodes.Contains(nodeName);
            var protoIdForAnchor = entityId ?? GetInferredFramePrototypeForNode(rawGraph.ID, nodeName);
            if (!isAnchorable &&
                !string.IsNullOrEmpty(protoIdForAnchor) &&
                _protoMan != null &&
                _protoMan.TryIndex<EntityPrototype>(protoIdForAnchor, out var eProto) &&
                _itemUtil != null &&
                _itemUtil.HasPrototypeComponent(eProto, "Anchorable"))
            {
                isAnchorable = true;
            }

            var edges = new List<IndexedGraphEdge>();
            foreach (var rawEdge in GetRawNodeEdges(rawNode))
            {
                var targetName = GetStringMember(rawEdge, "Target");
                if (string.IsNullOrEmpty(targetName))
                    continue;

                var edgeSteps = new List<ConstructionActionStep>();
                ConvertRawEdgeSteps(rawEdge, targetName, edgeSteps);

                bool? reqAnchored = TryGetRawEdgeAnchoredCondition(rawEdge, out bool a) ? a : null;
                bool? reqWirePanel = TryGetRawEdgeWirePanelCondition(rawEdge, out bool w) ? w : null;
                bool? reqDoorWelded = TryGetRawEdgeDoorWeldedCondition(rawEdge, out bool d) ? d : null;
                bool? reqAllWiresCut = TryGetRawEdgeAllWiresCutCondition(rawEdge, out bool c) ? c : null;

                edges.Add(new IndexedGraphEdge(
                    targetName,
                    edgeSteps,
                    reqAnchored,
                    reqWirePanel,
                    reqDoorWelded,
                    reqAllWiresCut));
            }

            nodes[nodeName] = new IndexedGraphNode(
                nodeName,
                entityId,
                edges,
                isBoardNode,
                securityExamine,
                isAnchorable);
        }

        return new IndexedConstructionGraph(rawGraph.ID, startNode, nodes);
    }

    private IndexedConstructionRecipe? BuildIndexedRecipe(ConstructionPrototype cProto)
    {
        var graphId = GetStringMember(cProto, "Graph");
        if (string.IsNullOrEmpty(graphId))
            return null;

        var targetNode = GetStringMember(cProto, "TargetNode") ?? string.Empty;
        var startNode = GetStringMember(cProto, "StartNode") ?? "start";
        var altProtos = ExtractConstructionAlternativePrototypes(cProto);

        return new IndexedConstructionRecipe(cProto.ID, graphId, targetNode, startNode, altProtos);
    }

    private void RegisterEntityNodeMapping(string entityProtoId, IndexedConstructionGraph graph, IndexedGraphNode node)
    {
        if (!_entityToGraphNodes.TryGetValue(entityProtoId, out var list))
        {
            list = new List<(IndexedConstructionGraph, IndexedGraphNode)>();
            _entityToGraphNodes[entityProtoId] = list;
        }

        foreach (var existing in list)
        {
            if (string.Equals(existing.Graph.Id, graph.Id, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(existing.Node.Name, node.Name, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }
        }

        list.Add((graph, node));
    }

    private bool TryGetConstructionMappingFromPrototype(EntityPrototype proto, out string graphId, out string nodeName)
    {
        graphId = string.Empty;
        nodeName = string.Empty;

        if (!_itemUtil.TryGetPrototypeComponent<object>(proto, "Construction", out var compObj) || compObj == null)
            return false;

        graphId = GetStringMember(compObj, "Graph") ?? string.Empty;
        nodeName = GetStringMember(compObj, "Node") ?? string.Empty;
        return !string.IsNullOrEmpty(graphId) && !string.IsNullOrEmpty(nodeName);
    }

    private static Dictionary<int, string>? ExtractConstructionAlternativePrototypes(ConstructionPrototype cProto)
    {
        Dictionary<int, string>? result = null;
        var altObj = GetMemberValue(cProto, "AlternativePrototypes") ?? GetMemberValue(cProto, "_alternativePrototypes");
        if (altObj is IDictionary dict)
        {
            foreach (DictionaryEntry entry in dict)
            {
                if (TryParsePipeLayerValue(entry.Key, out int layer) && layer > 0)
                {
                    var altId = ExtractIdString(entry.Value);
                    if (!string.IsNullOrEmpty(altId))
                    {
                        result ??= new Dictionary<int, string>();
                        result[layer] = altId;
                    }
                }
            }
        }
        else if (altObj is IEnumerable enumerable && altObj is not string)
        {
            int idx = 0;
            foreach (var item in enumerable)
            {
                var altId = ExtractIdString(item);
                if (!string.IsNullOrEmpty(altId) && !string.Equals(altId, cProto.ID, StringComparison.OrdinalIgnoreCase))
                {
                    int layer = GetPipeLayerForIdOrNode(altId);
                    if (layer == 0 && idx >= 1 && idx <= 2)
                        layer = idx;
                    if (layer > 0)
                    {
                        result ??= new Dictionary<int, string>();
                        result[layer] = altId;
                    }
                }
                idx++;
            }
        }

        return result;
    }

    private void RegisterEntityIdPipeLayerSuffix(string entityId)
    {
        if (TryExtractPipeLayerSuffix(entityId, out var baseFromId, out int layerFromId) && layerFromId > 0)
        {
            _entityAltLayerProtos[(baseFromId, layerFromId)] = entityId;
            _altEntityToBaseAndLayer[entityId] = (baseFromId, layerFromId);
        }
    }

    private void RegisterEntityAlternativePipeLayers(EntityPrototype entityProto)
    {
        RegisterEntityIdPipeLayerSuffix(entityProto.ID);

        if (!_itemUtil.TryGetPrototypeComponent<object>(entityProto, "AtmosPipeLayers", out var pipeLayersComp) ||
            pipeLayersComp == null)
        {
            return;
        }

        var altObj = GetMemberValue(pipeLayersComp, "AlternativePrototypes") ??
                     GetMemberValue(pipeLayersComp, "_alternativePrototypes");
        if (altObj is IDictionary dict)
        {
            string? primaryId = null;
            foreach (DictionaryEntry entry in dict)
            {
                if (TryParsePipeLayerValue(entry.Key, out int l) && l == 0)
                {
                    primaryId = ExtractIdString(entry.Value);
                    break;
                }
            }

            primaryId ??= TryExtractPipeLayerSuffix(entityProto.ID, out var stripped, out _) ? stripped : entityProto.ID;

            foreach (DictionaryEntry entry in dict)
            {
                if (TryParsePipeLayerValue(entry.Key, out int layer) && layer > 0)
                {
                    var altId = ExtractIdString(entry.Value);
                    if (!string.IsNullOrEmpty(altId))
                    {
                        _entityAltLayerProtos[(primaryId, layer)] = altId;
                        _altEntityToBaseAndLayer[altId] = (primaryId, layer);
                    }
                }
            }
        }
    }
}
