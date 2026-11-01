using System.Collections;
using Content.Shared.Construction;
using Content.Shared.Construction.Components;
using Content.Shared.Construction.Prototypes;
using Robust.Shared.Prototypes;

namespace RobustoClient.Systems.Construction;

public sealed partial class RobustaConstructionSolver
{
    public void EnsureCacheInitialized()
    {
        if (_cacheInitialized)
            return;

        _cacheInitialized = true;
        _entityToGraphNodes.Clear();
        _graphToConstructionProtos.Clear();
        _entityToConstructionProto.Clear();
        _entityAltLayerProtos.Clear();
        _altEntityToBaseAndLayer.Clear();
        _constructionAltLayerProtos.Clear();

        foreach (var cProto in _protoMan.EnumeratePrototypes<ConstructionPrototype>())
        {
            var graphId = GetStringMember(cProto, "Graph");
            if (string.IsNullOrEmpty(graphId))
                continue;

            if (!_graphToConstructionProtos.TryGetValue(graphId, out var list))
            {
                list = new List<ConstructionPrototype>();
                _graphToConstructionProtos[graphId] = list;
            }
            list.Add(cProto);

            var targetNodeName = GetStringMember(cProto, "TargetNode");
            if (!string.IsNullOrEmpty(targetNodeName) &&
                TryGetGraph(graphId, out var cGraph) &&
                TryGetNodeByName(cGraph, targetNodeName, out var targetNode))
            {
                var targetEntId = GetNodeEntityId(targetNode, null);
                if (!string.IsNullOrEmpty(targetEntId) && !_entityToConstructionProto.ContainsKey(targetEntId))
                    _entityToConstructionProto[targetEntId] = cProto.ID;
            }

            RegisterConstructionAlternativePrototypes(cProto);
        }

        foreach (var graph in _protoMan.EnumeratePrototypes<ConstructionGraphPrototype>())
        {
            foreach (var node in EnumerateGraphNodes(graph))
            {
                var entityId = GetNodeEntityId(node, null);
                if (!string.IsNullOrEmpty(entityId))
                    RegisterEntityNodeMapping(entityId, graph, node);
            }
        }

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

        // Register all intermediate ComputerFrame nodes so ComputerFrame can disambiguate its exact stage
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

        // Register intermediate MachineFrame nodes and base machine prototypes
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

        // Map EntityPrototypes that declare ConstructionComponent, AtmosPipeLayers, Computer/Machine components, or are constructed from boards
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
                if (_itemUtil.TryGetPrototypeComponent<ComputerBoardComponent>(entityProto, "ComputerBoard", out var compBoard) &&
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

            if (_itemUtil.TryGetPrototypeComponent<ElectronicsBoardComponent>(entityProto, "ElectronicsBoard", out var elecBoard) &&
                elecBoard != null)
            {
                var elecProtoId = GetStringMember(elecBoard, "Prototype");
                if (!string.IsNullOrEmpty(elecProtoId))
                {
                    // Search existing graphs for a BoardNodeEntity node if not already mapped
                    foreach (var g in _protoMan.EnumeratePrototypes<ConstructionGraphPrototype>())
                    {
                        foreach (var n in EnumerateGraphNodes(g))
                        {
                            var entObj = GetMemberValue(n, "Entity");
                            if (entObj != null && entObj.GetType().Name.Contains("BoardNodeEntity", StringComparison.OrdinalIgnoreCase))
                            {
                                RegisterEntityNodeMapping(elecProtoId, g, n);
                            }
                        }
                    }
                }
            }
        }
    }

    private void RegisterEntityNodeMapping(string entityProtoId, ConstructionGraphPrototype graph, ConstructionGraphNode node)
    {
        if (!_entityToGraphNodes.TryGetValue(entityProtoId, out var list))
        {
            list = new List<(ConstructionGraphPrototype, ConstructionGraphNode)>();
            _entityToGraphNodes[entityProtoId] = list;
        }

        foreach (var existing in list)
        {
            if (string.Equals(existing.Graph.ID, graph.ID, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(GetNodeName(existing.Node), GetNodeName(node), StringComparison.OrdinalIgnoreCase))
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

    private void RegisterConstructionAlternativePrototypes(ConstructionPrototype cProto)
    {
        var altObj = GetMemberValue(cProto, "AlternativePrototypes") ?? GetMemberValue(cProto, "_alternativePrototypes");
        if (altObj is IDictionary dict)
        {
            foreach (DictionaryEntry entry in dict)
            {
                if (TryParsePipeLayerValue(entry.Key, out int layer) && layer > 0)
                {
                    var altId = ExtractIdString(entry.Value);
                    if (!string.IsNullOrEmpty(altId))
                        _constructionAltLayerProtos[(cProto.ID, layer)] = altId;
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
                        _constructionAltLayerProtos[(cProto.ID, layer)] = altId;
                }
                idx++;
            }
        }
    }

    private void RegisterEntityAlternativePipeLayers(EntityPrototype entityProto)
    {
        if (TryExtractPipeLayerSuffix(entityProto.ID, out var baseFromId, out int layerFromId) && layerFromId > 0)
        {
            _entityAltLayerProtos[(baseFromId, layerFromId)] = entityProto.ID;
            _altEntityToBaseAndLayer[entityProto.ID] = (baseFromId, layerFromId);
        }

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
