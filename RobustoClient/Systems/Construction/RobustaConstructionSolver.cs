using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Content.Shared.Atmos.Components;
using Content.Shared.Construction;
using Content.Shared.Construction.Components;
using Content.Shared.Construction.Prototypes;
using Content.Shared.Construction.Steps;
using Robust.Shared.Containers;
using Robust.Shared.GameObjects;
using Robust.Shared.IoC;
using Robust.Shared.Prototypes;
using RobustoClient.Systems.Abstract;

namespace RobustoClient.Systems.Construction;

public enum ConstructionActionKind
{
    StartGhostConstruction,
    AddMaterial,
    UseTool,
    CutWires,
    InsertPrototype,
    InsertComponent,
    InsertTag
}

public sealed record ConstructionActionStep(
    ConstructionActionKind Kind,
    string Description,
    string? PrototypeOrQualityOrTag = null,
    int Amount = 1,
    float DoAfterDelay = 0f,
    string? ExpectedNextNode = null,
    int? TargetPipeLayer = null
);

public sealed record SolvedConstructionPlan(
    string GraphId,
    string StartNode,
    string TargetNode,
    bool IsDeconstruct,
    List<ConstructionActionStep> Steps
);

public sealed class RobustaConstructionSolver : EntitySystem
{
    [Dependency] private readonly IEntityManager _entMan = default!;
    [Dependency] private readonly IPrototypeManager _protoMan = default!;
    [Dependency] private readonly IComponentFactory _compFactory = default!;
    [Dependency] private readonly ItemUtilitySystem _itemUtil = default!;
    [Dependency] private readonly SharedContainerSystem _container = default!;

    private bool _cacheInitialized;

    private readonly Dictionary<string, List<(ConstructionGraphPrototype Graph, ConstructionGraphNode Node)>> _entityToGraphNodes =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly Dictionary<string, List<ConstructionPrototype>> _graphToConstructionProtos =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly Dictionary<string, string> _entityToConstructionProto =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly Dictionary<(string BaseProtoId, int PipeLayer), string> _entityAltLayerProtos = new();

    private readonly Dictionary<string, (string BaseProtoId, int PipeLayer)> _altEntityToBaseAndLayer =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly Dictionary<(string BaseRecipeId, int PipeLayer), string> _constructionAltLayerProtos = new();

    private static readonly string[] PrimaryDeconstructTargets =
    {
        "start",
        "destroyed",
        "deconstructed",
        "destroyedMachineFrame"
    };

    private static readonly string[] FallbackDeconstructTargets =
    {
        "frame",
        "machineFrame",
        "girder",
        "frameUnsecured",
        "assembly",
        "missingWires"
    };

    private static readonly HashSet<string> NonTerminalBuildNodeNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "start",
        "frame",
        "machineFrame",
        "missingWires",
        "computerFrame",
        "frameUnsecured",
        "boardUnsecured",
        "monitorMissing",
        "monitorUnsecured",
        "girder",
        "reinforcedGirder",
        "wallFrame",
        "windowFrame",
        "assembly",
        "wired",
        "electronics",
        "glassElectronics",
        "apcFrame"
    };

    private static readonly HashSet<string> KnownAnchorableFrameNodes = new(StringComparer.OrdinalIgnoreCase)
    {
        "girder",
        "reinforcedGirder",
        "assembly",
        "frameUnsecured",
        "missingWires",
        "frame",
        "machineFrame",
        "computerFrame",
        "apcFrame",
        "wallFrame",
        "windowFrame",
        "pipe",
        "binary",
        "trinary",
        "unary",
        "disposal",
        "disposal_pipe",
        "trunk"
    };

    private static readonly string[] PipeAndAtmosKeywords =
    {
        "pipe",
        "gaspipe",
        "gasbinary",
        "gastrinary",
        "gasunary",
        "disposal",
        "manifold",
        "valve",
        "pump",
        "mixer",
        "filter",
        "vent",
        "scrubber",
        "injector",
        "trunk",
        "router",
        "junction",
        "tagger",
        "radiator",
        "heatexchanger",
        "connectorport",
        "gasport"
    };

    private static readonly string[] BaseConsolePrototypeIds =
    {
        "BaseComputer",
        "BaseStructureComputer",
        "BaseComputerAiAccess",
        "BaseComputerTabletop",
        "BaseComputerShuttle",
        "ComputerTabletop"
    };

    private static readonly string[] BaseMachinePrototypeIds =
    {
        "BaseMachine",
        "ConstructibleMachine",
        "BaseMachinePowered",
        "BaseLathe",
        "BaseLatheLube",
        "BaseHyperlathe",
        "BaseResearchAndDevelopmentPointSource",
        "BasePartMachine",
        "BaseHeaterMachine",
        "BaseThruster",
        "BaseSubstation",
        "BaseSMES",
        "BaseGenerator",
        "BasePowerCharger",
        "BaseRecharger"
    };

    private static readonly string[] BaseAirlockPrototypeIds =
    {
        "BaseAirlock",
        "Airlock",
        "AirlockGlass",
        "AirlockShuttle",
        "AirlockGlassShuttle",
        "AirlockExternal",
        "AirlockExternalGlass",
        "AirlockExternalShuttle",
        "AirlockExternalGlassShuttle",
        "HighSecDoor"
    };

    private string? _lastKnownMachineBoardProtoId;

    public void NoteHeldEntity(EntityUid? heldUid)
    {
        if (!heldUid.HasValue || !_entMan.EntityExists(heldUid.Value))
            return;

        if (IsMachineBoardEntity(heldUid.Value))
        {
            var protoId = _entMan.GetComponent<MetaDataComponent>(heldUid.Value).EntityPrototype?.ID;
            if (!string.IsNullOrEmpty(protoId))
                _lastKnownMachineBoardProtoId = protoId;
        }
    }

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<PrototypesReloadedEventArgs>(OnPrototypesReloaded);
    }

    private void OnPrototypesReloaded(PrototypesReloadedEventArgs args)
    {
        _cacheInitialized = false;
        _entityToGraphNodes.Clear();
        _graphToConstructionProtos.Clear();
        _entityToConstructionProto.Clear();
        _entityAltLayerProtos.Clear();
        _altEntityToBaseAndLayer.Clear();
        _constructionAltLayerProtos.Clear();
    }

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

    private bool TryGetGraph(string graphId, out ConstructionGraphPrototype graph)
    {
        if (_protoMan.TryIndex(graphId, out graph!))
            return true;

        foreach (var g in _protoMan.EnumeratePrototypes<ConstructionGraphPrototype>())
        {
            if (string.Equals(g.ID, graphId, StringComparison.OrdinalIgnoreCase))
            {
                graph = g;
                return true;
            }
        }

        graph = default!;
        return false;
    }

    private bool TryFindComputerGraphAndNode(
        out ConstructionGraphPrototype? compGraph,
        out ConstructionGraphNode? compNode)
    {
        compGraph = null;
        compNode = null;

        if (!TryGetGraph("Computer", out var g))
        {
            foreach (var candidate in _protoMan.EnumeratePrototypes<ConstructionGraphPrototype>())
            {
                foreach (var n in EnumerateGraphNodes(candidate))
                {
                    if (string.Equals(GetNodeEntityId(n, null), "ComputerFrame", StringComparison.OrdinalIgnoreCase))
                    {
                        g = candidate;
                        break;
                    }
                }
                if (g != null)
                    break;
            }
        }

        if (g == null)
            return false;

        compGraph = g;
        if (TryGetNodeByName(g, "computer", out var namedNode))
        {
            compNode = namedNode;
            return true;
        }

        foreach (var n in EnumerateGraphNodes(g))
        {
            var entObj = GetMemberValue(n, "Entity");
            if (entObj != null && entObj.GetType().Name.Contains("BoardNodeEntity", StringComparison.OrdinalIgnoreCase))
            {
                compNode = n;
                return true;
            }
        }

        return false;
    }

    private bool TryFindMachineGraphAndNode(
        out ConstructionGraphPrototype? machineGraph,
        out ConstructionGraphNode? machineNode)
    {
        machineGraph = null;
        machineNode = null;

        if (!TryGetGraph("Machine", out var g))
        {
            foreach (var candidate in _protoMan.EnumeratePrototypes<ConstructionGraphPrototype>())
            {
                foreach (var n in EnumerateGraphNodes(candidate))
                {
                    if (string.Equals(GetNodeEntityId(n, null), "MachineFrame", StringComparison.OrdinalIgnoreCase))
                    {
                        g = candidate;
                        break;
                    }
                }
                if (g != null)
                    break;
            }
        }

        if (g == null)
            return false;

        machineGraph = g;
        if (TryGetNodeByName(g, "machine", out var namedNode))
        {
            machineNode = namedNode;
            return true;
        }

        foreach (var n in EnumerateGraphNodes(g))
        {
            var entObj = GetMemberValue(n, "Entity");
            if (entObj != null && entObj.GetType().Name.Contains("BoardNodeEntity", StringComparison.OrdinalIgnoreCase))
            {
                machineNode = n;
                return true;
            }
        }

        return false;
    }

    private bool TryFindAirlockGraphAndNode(
        out ConstructionGraphPrototype? airlockGraph,
        out ConstructionGraphNode? airlockNode)
    {
        airlockGraph = null;
        airlockNode = null;

        if (!TryGetGraph("Airlock", out var g))
        {
            foreach (var candidate in _protoMan.EnumeratePrototypes<ConstructionGraphPrototype>())
            {
                if (TryGetNodeByName(candidate, "airlock", out _) &&
                    TryGetNodeByName(candidate, "assembly", out _))
                {
                    g = candidate;
                    break;
                }
            }
        }

        if (g == null)
            return false;

        airlockGraph = g;
        if (TryGetNodeByName(g, "airlock", out var namedNode))
        {
            airlockNode = namedNode;
            return true;
        }

        return false;
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

    public SolvedConstructionPlan? SolveBuildPlan(EntityUid target, string? desiredEntityProtoId = null)
    {
        if (!_entMan.EntityExists(target))
            return null;

        EnsureCacheInitialized();

        // Case A: target has ConstructionGhostComponent
        var ghostComp = FindComponentByName(target, "ConstructionGhostComponent");
        if (ghostComp != null)
        {
            var cProto = GetMemberValue(ghostComp, "Prototype") as ConstructionPrototype;
            if (cProto == null)
            {
                var protoId = GetStringMember(ghostComp, "Prototype");
                if (!string.IsNullOrEmpty(protoId))
                    _protoMan.TryIndex(protoId, out cProto);
            }

            if (cProto != null)
                return SolveBuildPlan(cProto, includeGhostStart: true, desiredEntityProtoId: desiredEntityProtoId);
        }

        // Case B: target is an in-world frame/unfinished structure or an unconfigured atmos contraption
        if (!TryResolveGraphAndNode(target, out var graph, out var currentNode, out var explicitTargetNode) ||
            graph == null || currentNode == null)
        {
            return null;
        }

        var currentNodeName = GetNodeName(currentNode);
        var targetNodeName = explicitTargetNode;
        int desiredPipeLayer = GetDesiredPipeLayer(desiredEntityProtoId, null);

        if (!string.IsNullOrEmpty(desiredEntityProtoId))
        {
            if (_entityToGraphNodes.TryGetValue(desiredEntityProtoId, out var desiredNodes))
            {
                foreach (var (dGraph, dNode) in desiredNodes)
                {
                    if (string.Equals(dGraph.ID, graph.ID, StringComparison.OrdinalIgnoreCase))
                    {
                        targetNodeName = GetNodeName(dNode);
                        break;
                    }
                }
            }
            else if (TryExtractPipeLayerSuffix(desiredEntityProtoId, out var baseDesiredId, out _) &&
                     _entityToGraphNodes.TryGetValue(baseDesiredId, out var baseNodes))
            {
                foreach (var (dGraph, dNode) in baseNodes)
                {
                    if (string.Equals(dGraph.ID, graph.ID, StringComparison.OrdinalIgnoreCase))
                    {
                        targetNodeName = GetNodeName(dNode);
                        break;
                    }
                }
            }
        }

        if (string.IsNullOrEmpty(targetNodeName) ||
            string.Equals(targetNodeName, currentNodeName, StringComparison.OrdinalIgnoreCase))
        {
            // If this is already an unanchored pipe/contraption or an unconfigured contraption at its base node
            if (IsPipeOrAtmosGraphOrNode(graph, currentNode, target))
            {
                int curLayer = DetectEntityPipeLayer(target, out _);
                bool isAnchored = _entMan.GetComponent<TransformComponent>(target).Anchored;
                bool canScrewdriver = ShouldUseScrewdriverForPipeLayer(graph, currentNode, desiredEntityProtoId);
                if (!isAnchored || (canScrewdriver && desiredPipeLayer != curLayer && !string.IsNullOrEmpty(desiredEntityProtoId)))
                {
                    var layerSteps = new List<ConstructionActionStep>();
                    if (!isAnchored)
                        AppendPostBuildPipeAnchorStepIfNeeded(target, graph, currentNode, layerSteps);
                    if (canScrewdriver && !string.IsNullOrEmpty(desiredEntityProtoId) && desiredPipeLayer != curLayer)
                        AppendPipeLayerScrewdriverSteps(curLayer, desiredPipeLayer, layerSteps);

                    if (layerSteps.Count > 0)
                    {
                        TryExtractPipeLayerSuffix(currentNodeName, out var baseCurNode, out _);
                        var finalNodeLabel = desiredPipeLayer > 0 ? $"{baseCurNode}Alt{desiredPipeLayer}" : baseCurNode;
                        return new SolvedConstructionPlan(
                            graph.ID,
                            currentNodeName,
                            finalNodeLabel,
                            IsDeconstruct: false,
                            Steps: layerSteps);
                    }
                }
            }

            targetNodeName = FindBestBuildTargetNode(graph, currentNode);
        }

        if (string.IsNullOrEmpty(targetNodeName) ||
            string.Equals(targetNodeName, currentNodeName, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var edgePath = FindShortestEdgePath(graph, currentNodeName, targetNodeName);
        if ((edgePath == null || edgePath.Count == 0) &&
            TryExtractPipeLayerSuffix(targetNodeName, out var fallbackBaseTargetNode, out _) &&
            !string.Equals(fallbackBaseTargetNode, targetNodeName, StringComparison.OrdinalIgnoreCase))
        {
            targetNodeName = fallbackBaseTargetNode;
            if (!string.Equals(targetNodeName, currentNodeName, StringComparison.OrdinalIgnoreCase))
                edgePath = FindShortestEdgePath(graph, currentNodeName, targetNodeName);
        }

        var steps = new List<ConstructionActionStep>();
        ConstructionGraphNode finalBuiltNode = currentNode;

        if (edgePath != null && edgePath.Count > 0)
        {
            for (int i = 0; i < edgePath.Count; i++)
            {
                var (fromNode, edge, toNode) = edgePath[i];
                bool isFirstEdge = i == 0;
                AppendAnchorTransitionStepIfNeeded(target, isFirstEdge, graph, fromNode, edge, toNode, isDeconstruct: false, steps);
                AppendMachineFrameStepsIfNeeded(target, graph, fromNode, toNode, desiredEntityProtoId, steps);
                ConvertEdgeSteps(edge, GetNodeName(toNode), steps);
            }
            finalBuiltNode = edgePath[^1].To;
            AppendPostBuildPipeAnchorStepIfNeeded(target, graph, finalBuiltNode, steps);
        }
        else
        {
            // No graph edge path exists between currentNodeName and targetNodeName.
            // Only allow post-build anchor/screwdriver steps if the entity is on the same base node
            // and is either unanchored or is a contraption that can change layer via Screwdriver.
            if (!IsPipeOrAtmosGraphOrNode(graph, currentNode, target))
                return null;

            TryExtractPipeLayerSuffix(currentNodeName, out var baseCurNode, out _);
            TryExtractPipeLayerSuffix(targetNodeName, out var baseTargetNode, out _);
            if (!string.Equals(baseCurNode, baseTargetNode, StringComparison.OrdinalIgnoreCase))
                return null;

            bool isTargetAnchored = _entMan.GetComponent<TransformComponent>(target).Anchored;
            bool canScrewdriver = ShouldUseScrewdriverForPipeLayer(graph, currentNode, desiredEntityProtoId);
            if (!canScrewdriver && isTargetAnchored)
                return null;

            if (!isTargetAnchored)
                AppendPostBuildPipeAnchorStepIfNeeded(target, graph, finalBuiltNode, steps);
        }

        if (ShouldUseScrewdriverForPipeLayer(graph, finalBuiltNode, desiredEntityProtoId) &&
            !string.IsNullOrEmpty(desiredEntityProtoId))
        {
            // Layer this edge actually builds. Node names no longer carry an Alt1/Alt2 suffix (upstream
            // variantized them), so read the layer from the node's resolved entity prototype.
            int builtLayer = GetDeclaredPipeLayer(GetNodeEntityId(finalBuiltNode, target));
            if (builtLayer == 0 && DetectEntityPipeLayer(target, out _) is var liveLayer && liveLayer > 0)
                builtLayer = liveLayer;

            if (desiredPipeLayer != builtLayer)
            {
                AppendPipeLayerScrewdriverSteps(builtLayer, desiredPipeLayer, steps);
                TryExtractPipeLayerSuffix(GetNodeName(finalBuiltNode), out var baseBuiltNode, out _);
                targetNodeName = desiredPipeLayer > 0 ? $"{baseBuiltNode}Alt{desiredPipeLayer}" : baseBuiltNode;
            }
        }

        if (steps.Count == 0)
            return null;

        return new SolvedConstructionPlan(
            graph.ID,
            currentNodeName,
            targetNodeName,
            IsDeconstruct: false,
            Steps: steps);
    }

    /// <summary>
    /// Solves the construction graph from the entity's current node to an explicit target node.
    /// </summary>
    public SolvedConstructionPlan? SolvePlanToNode(
        EntityUid target,
        string targetNodeName,
        string? startNodeOverride = null)
    {
        if (!_entMan.EntityExists(target) || string.IsNullOrWhiteSpace(targetNodeName))
            return null;

        EnsureCacheInitialized();
        if (!TryResolveGraphAndNode(target, out var graph, out var currentNode, out _) ||
            graph == null || currentNode == null)
        {
            if (FindComponentByName(target, "WiresPanelSecurityComponent") != null &&
                TryFindAirlockGraphAndNode(out var fallbackAirlockGraph, out var fallbackAirlockNode) &&
                fallbackAirlockGraph != null && fallbackAirlockNode != null)
            {
                graph = fallbackAirlockGraph;
                currentNode = fallbackAirlockNode;
                ApplyAirlockSecurityNodeOverride(target, graph, ref currentNode);
            }
            else
            {
                return null;
            }
        }

        if (!string.IsNullOrWhiteSpace(startNodeOverride) &&
            TryGetNodeByName(graph, startNodeOverride, out var overrideNode))
        {
            currentNode = overrideNode;
        }

        var currentNodeName = GetNodeName(currentNode!);
        if (string.Equals(currentNodeName, targetNodeName, StringComparison.OrdinalIgnoreCase))
            return new SolvedConstructionPlan(graph.ID, currentNodeName, targetNodeName, false, []);

        var edgePath = FindShortestEdgePath(graph, currentNodeName, targetNodeName);
        if (edgePath == null || edgePath.Count == 0)
            return null;

        var steps = new List<ConstructionActionStep>();
        for (var i = 0; i < edgePath.Count; i++)
        {
            var (fromNode, edge, toNode) = edgePath[i];
            AppendAnchorTransitionStepIfNeeded(
                target,
                i == 0,
                graph,
                fromNode,
                edge,
                toNode,
                isDeconstruct: false,
                steps);
            ConvertEdgeSteps(edge, GetNodeName(toNode), steps);
        }

        return new SolvedConstructionPlan(graph.ID, currentNodeName, targetNodeName, false, steps);
    }

    public string? ResolveCurrentSecurityNode(
        EntityUid target,
        object? secComp = null,
        ConstructionGraphPrototype? graph = null)
    {
        EnsureCacheInitialized();

        if (graph == null)
            TryFindAirlockGraphAndNode(out graph, out _);

        secComp ??= FindComponentByName(target, "WiresPanelSecurityComponent");
        if (secComp == null)
            return null;

        if (GetMemberValue(secComp, "WiresAccessible") is true)
            return "airlock";

        var examine = GetStringMember(secComp, "Examine");
        if (graph != null && !string.IsNullOrEmpty(examine))
        {
            // 1. Check node actions if present on the graph
            foreach (var n in EnumerateGraphNodes(graph))
            {
                var actions = GetMemberValue(n, "Actions") as IEnumerable
                              ?? GetMemberValue(n, "_actions") as IEnumerable;
                if (actions == null)
                    continue;

                foreach (var act in actions)
                {
                    if (act == null)
                        continue;

                    var actExamine = GetStringMember(act, "Examine");
                    if (string.Equals(actExamine, examine, StringComparison.OrdinalIgnoreCase))
                        return GetNodeName(n);
                }
            }
        }

        // 2. Map standard locale IDs, adapting to the actual nodes present in the Airlock graph
        var mapped = GetSecurityRemovalTarget(examine);
        if (!string.IsNullOrEmpty(mapped))
        {
            if (graph == null || TryGetNodeByName(graph, mapped, out _))
                return mapped;

            if (string.Equals(examine, "wires-panel-component-on-examine-security-level5", StringComparison.OrdinalIgnoreCase) &&
                TryGetNodeByName(graph, "maxSecurityUnfinished", out _))
            {
                return "maxSecurityUnfinished";
            }

            if ((string.Equals(examine, "wires-panel-component-on-examine-security-level6", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(examine, "wires-panel-component-on-examine-security-level7", StringComparison.OrdinalIgnoreCase)) &&
                TryGetNodeByName(graph, "maxSecurity", out _))
            {
                return "maxSecurity";
            }
        }

        // 3. Fallback to the component/prototype's SecurityLevel field
        var secLevel = GetStringMember(secComp, "SecurityLevel");
        if (string.IsNullOrEmpty(secLevel) && _entMan.EntityExists(target))
        {
            var proto = _entMan.GetComponent<MetaDataComponent>(target).EntityPrototype;
            if (proto != null && _itemUtil.TryGetPrototypeComponent<object>(proto, "WiresPanelSecurity", out var protoSec))
                secLevel = GetStringMember(protoSec, "SecurityLevel");
        }

        if (!string.IsNullOrEmpty(secLevel) && (graph == null || TryGetNodeByName(graph, secLevel, out _)))
            return secLevel;

        return mapped;
    }

    public string? ResolveNextSecurityNode(EntityUid target, string currentNodeName)
    {
        if (string.IsNullOrEmpty(currentNodeName))
            return null;

        EnsureCacheInitialized();

        if (TryFindAirlockGraphAndNode(out var graph, out _) && graph != null)
        {
            var path = FindShortestEdgePath(graph, currentNodeName, "airlock");
            if (path != null && path.Count > 0)
            {
                var nextInGraph = GetNodeName(path[0].To);
                if (!string.IsNullOrEmpty(nextInGraph))
                    return nextInGraph;
            }
        }

        return GetNextSecurityNode(currentNodeName);
    }

    public static string? GetSecurityRemovalTarget(string? examine)
    {
        return examine switch
        {
            "wires-panel-component-on-examine-security-level1" => "medSecurityUnfinished",
            "wires-panel-component-on-examine-security-level2" => "medSecurity",
            "wires-panel-component-on-examine-security-level3" => "highSecurityUnfinished",
            "wires-panel-component-on-examine-security-level4" => "highSecurity",
            "wires-panel-component-on-examine-security-level5" => "maxSecurity",
            "wires-panel-component-on-examine-security-level6" => "superMaxSecurityUnfinished",
            "wires-panel-component-on-examine-security-level7" => "superMaxSecurity",
            _ => null
        };
    }

    public static string? GetNextSecurityNode(string node)
    {
        return node switch
        {
            "medSecurity" => "medSecurityUnfinished",
            "highSecurity" => "highSecurityUnfinished",
            "maxSecurity" => "highSecurity",
            "maxSecurityUnfinished" => "airlock",
            "superMaxSecurity" => "superMaxSecurityUnfinished",
            "superMaxSecurityUnfinished" => "maxSecurity",
            "medSecurityUnfinished" or "highSecurityUnfinished" => "airlock",
            _ => null
        };
    }

    public SolvedConstructionPlan? SolveBuildPlan(
        ConstructionPrototype constructionProto,
        bool includeGhostStart = true,
        string? desiredEntityProtoId = null)
    {
        EnsureCacheInitialized();

        var graphId = GetStringMember(constructionProto, "Graph");
        var targetNodeName = GetStringMember(constructionProto, "TargetNode");
        var startNodeName = GetStringMember(constructionProto, "StartNode");

        if (string.IsNullOrEmpty(graphId) ||
            string.IsNullOrEmpty(targetNodeName) ||
            !TryGetGraph(graphId, out var graph))
        {
            return null;
        }

        // If building a MachineFrame ghost and the player has a MachineBoard (or desired machine prototype),
        // build all the way to the completed "machine" node instead of stopping at "machineFrame".
        if (string.Equals(graph.ID, "Machine", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(targetNodeName, "machine", StringComparison.OrdinalIgnoreCase) &&
            TryGetNodeByName(graph, "machine", out _) &&
            (!string.IsNullOrEmpty(desiredEntityProtoId) || TryFindAvailableMachineBoard(null, null, out _, out _)))
        {
            targetNodeName = "machine";
        }

        if (string.IsNullOrEmpty(startNodeName))
            startNodeName = GetStringMember(graph, "Start") ?? "start";

        if (!TryGetNodeByName(graph, startNodeName!, out _))
            startNodeName = "start";

        var edgePath = FindShortestEdgePath(graph, startNodeName!, targetNodeName);
        if (edgePath == null || edgePath.Count == 0)
            return null;

        var steps = new List<ConstructionActionStep>();
        if (includeGhostStart)
        {
            var firstNodeAfterGhost = GetNodeName(edgePath[0].To);
            float initialDoAfter = SumEdgeDoAfter(edgePath[0].Edge);
            steps.Add(new ConstructionActionStep(
                ConstructionActionKind.StartGhostConstruction,
                $"Start construction ({constructionProto.ID})",
                PrototypeOrQualityOrTag: constructionProto.ID,
                Amount: 1,
                DoAfterDelay: initialDoAfter,
                ExpectedNextNode: firstNodeAfterGhost));
        }

        for (int i = 0; i < edgePath.Count; i++)
        {
            var (fromNode, edge, toNode) = edgePath[i];
            bool isFirstEdge = i == 0;
            AppendAnchorTransitionStepIfNeeded(null, isFirstEdge, graph, fromNode, edge, toNode, isDeconstruct: false, steps);
            AppendMachineFrameStepsIfNeeded(null, graph, fromNode, toNode, desiredEntityProtoId, steps);
            ConvertEdgeSteps(edge, GetNodeName(toNode), steps);
        }

        var finalNode = edgePath[^1].To;
        AppendPostBuildPipeAnchorStepIfNeeded(null, graph, finalNode, steps);

        if (ShouldUseScrewdriverForPipeLayer(graph, finalNode, desiredEntityProtoId) &&
            !string.IsNullOrEmpty(desiredEntityProtoId))
        {
            int builtLayer = GetPipeLayerForIdOrNode(targetNodeName);
            int desiredLayer = GetDesiredPipeLayer(desiredEntityProtoId, targetNodeName);
            if (desiredLayer != builtLayer)
            {
                AppendPipeLayerScrewdriverSteps(builtLayer, desiredLayer, steps);
                TryExtractPipeLayerSuffix(targetNodeName, out var baseTargetNode, out _);
                targetNodeName = desiredLayer > 0 ? $"{baseTargetNode}Alt{desiredLayer}" : baseTargetNode;
            }
        }

        return new SolvedConstructionPlan(
            graph.ID,
            startNodeName!,
            targetNodeName,
            IsDeconstruct: false,
            Steps: steps);
    }

    public SolvedConstructionPlan? SolveDeconstructPlan(EntityUid target)
    {
        if (!_entMan.EntityExists(target))
            return null;

        EnsureCacheInitialized();

        if (!TryResolveGraphAndNode(target, out var graph, out var currentNode, out _) ||
            graph == null || currentNode == null)
        {
            return null;
        }

        var currentNodeName = GetNodeName(currentNode);
        if (string.IsNullOrEmpty(currentNodeName))
            return null;

        var candidateTargets = GetDeconstructCandidateNodes(target, graph, currentNodeName);
        foreach (var targetNodeName in candidateTargets)
        {
            if (string.Equals(targetNodeName, currentNodeName, StringComparison.OrdinalIgnoreCase))
                continue;

            var edgePath = FindShortestEdgePath(graph, currentNodeName, targetNodeName);
            if (edgePath == null || edgePath.Count == 0)
                continue;

            var steps = new List<ConstructionActionStep>();
            var firstEdge = edgePath[0].Edge;

            if (ShouldWeldDoorBeforeDeconstruct(target, graph, currentNode, firstEdge))
            {
                steps.Add(new ConstructionActionStep(
                    ConstructionActionKind.UseTool,
                    "Weld door shut (Welding)",
                    PrototypeOrQualityOrTag: "Welding",
                    Amount: 1,
                    DoAfterDelay: 1f,
                    ExpectedNextNode: null));
            }

            var panelComp = FindComponentByName(target, "WiresPanelComponent");
            bool opensOrNeedsWirePanel = panelComp != null &&
                ShouldOpenWirePanelBeforeEdge(graph, currentNode, firstEdge, panelComp);

            if (opensOrNeedsWirePanel && GetMemberValue(panelComp, "Open") is false)
            {
                var openTool = GetStringMember(panelComp, "OpeningTool") ?? "Screwing";
                steps.Add(new ConstructionActionStep(
                    ConstructionActionKind.UseTool,
                    $"Open wire panel ({openTool})",
                    PrototypeOrQualityOrTag: openTool,
                    Amount: 1,
                    DoAfterDelay: 0f,
                    ExpectedNextNode: null));
            }

            for (int i = 0; i < edgePath.Count; i++)
            {
                var (fromNode, edge, toNode) = edgePath[i];
                bool isFirstEdge = i == 0;
                if (ShouldCutInternalWiresBeforeEdge(target, graph, fromNode, edge, panelComp))
                {
                    steps.Add(new ConstructionActionStep(
                        ConstructionActionKind.CutWires,
                        "Cut internal wires (Cutting)",
                        PrototypeOrQualityOrTag: "Cutting",
                        Amount: 1,
                        DoAfterDelay: 0.5f,
                        ExpectedNextNode: null));
                }

                // When deconstructing a Machine (or a MachineFrame that contains a board) from "machineFrame" to "missingWires",
                // we must pry the MachineFrame a second time with a Crowbar (Prying) to pop out the circuitboard and parts
                // before the low-voltage cable can be cut with Wirecutters (Cutting).
                if (string.Equals(graph.ID, "Machine", StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(GetNodeName(fromNode), "machineFrame", StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(GetNodeName(toNode), "missingWires", StringComparison.OrdinalIgnoreCase) &&
                    (!isFirstEdge || HasMachineFrameBoard(target)))
                {
                    steps.Add(new ConstructionActionStep(
                        ConstructionActionKind.UseTool,
                        "Pry out board and parts (Prying)",
                        PrototypeOrQualityOrTag: "Prying",
                        Amount: 1,
                        DoAfterDelay: 0f,
                        ExpectedNextNode: null));
                }

                AppendAnchorTransitionStepIfNeeded(target, isFirstEdge, graph, fromNode, edge, toNode, isDeconstruct: true, steps);
                ConvertEdgeSteps(edge, GetNodeName(toNode), steps);
            }

            if (steps.Count == 0)
                continue;

            return new SolvedConstructionPlan(
                graph.ID,
                currentNodeName,
                targetNodeName,
                IsDeconstruct: true,
                Steps: steps);
        }

        return null;
    }

    private bool ShouldOpenWirePanelBeforeEdge(
        ConstructionGraphPrototype graph,
        ConstructionGraphNode currentNode,
        ConstructionGraphEdge firstEdge,
        IComponent panelComp)
    {
        if (TryGetEdgeWirePanelCondition(firstEdge, out bool requiredOpen))
            return requiredOpen;

        // Consoles (Computer graph) do not require opening a wire panel to deconstruct;
        // their first deconstruction step operates directly on the monitor/frame.
        if (string.Equals(graph.ID, "Computer", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(GetNodeName(currentNode), "computer", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var openTool = GetStringMember(panelComp, "OpeningTool") ?? "Screwing";
        if (FirstEdgeStepIsTool(firstEdge, openTool))
            return false;

        return true;
    }

    private bool ShouldWeldDoorBeforeDeconstruct(
        EntityUid target,
        ConstructionGraphPrototype graph,
        ConstructionGraphNode currentNode,
        ConstructionGraphEdge firstEdge)
    {
        if (TryGetEdgeDoorWeldedCondition(firstEdge, out bool requiredWelded))
            return requiredWelded;

        var weldableComp = FindComponentByName(target, "WeldableComponent");
        if (weldableComp == null)
            return false;

        if (GetMemberValue(weldableComp, "IsWelded") is true)
            return false;

        var graphId = graph.ID ?? string.Empty;
        var nodeName = GetNodeName(currentNode);
        if (graphId.Contains("Airlock", StringComparison.OrdinalIgnoreCase) ||
            nodeName.Contains("airlock", StringComparison.OrdinalIgnoreCase) ||
            FindComponentByName(target, "AirlockComponent") != null)
        {
            return true;
        }

        return false;
    }

    private bool ShouldCutInternalWiresBeforeEdge(
        EntityUid target,
        ConstructionGraphPrototype graph,
        ConstructionGraphNode currentNode,
        ConstructionGraphEdge firstEdge,
        IComponent? panelComp)
    {
        if (TryGetEdgeAllWiresCutCondition(firstEdge, out bool requiredAllCut))
            return requiredAllCut;

        var graphId = graph.ID ?? string.Empty;
        var nodeName = GetNodeName(currentNode);

        // Consoles and standard MachineFrames do not require cutting all hacking wires to deconstruct
        if (string.Equals(graphId, "Computer", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(graphId, "Machine", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(graphId, "AirlockShuttle", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(nodeName, "computer", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(nodeName, "machine", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        // Airlocks and Windoors require all internal wires in the wire panel to be cut when leaving the main door node
        if (string.Equals(nodeName, "airlock", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(nodeName, "glassAirlock", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(nodeName, "windoor", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(nodeName, "windoorSecure", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (string.Equals(graphId, "Airlock", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(graphId, "Windoor", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (panelComp != null && HasWiresUserInterface(target))
        {
            return true;
        }

        return false;
    }

    public bool HasWiresUserInterface(EntityUid target)
    {
        var uiComp = FindComponentByName(target, "UserInterfaceComponent");
        if (uiComp == null)
            return false;

        if (GetMemberValue(uiComp, "ClientOpenInterfaces") is IDictionary openDict)
        {
            foreach (DictionaryEntry entry in openDict)
            {
                var keyType = entry.Key?.GetType().Name ?? string.Empty;
                if (keyType.Contains("WiresUiKey", StringComparison.OrdinalIgnoreCase))
                    return true;
            }
        }

        if (GetMemberValue(uiComp, "Interfaces") is IDictionary interfacesDict)
        {
            foreach (DictionaryEntry entry in interfacesDict)
            {
                var keyType = entry.Key?.GetType().Name ?? string.Empty;
                if (keyType.Contains("WiresUiKey", StringComparison.OrdinalIgnoreCase))
                    return true;
            }
        }

        // Prototype fallback if Interfaces dictionary is private/inaccessible
        var proto = _entMan.GetComponent<MetaDataComponent>(target).EntityPrototype;
        if (proto != null &&
            (FindComponentByName(target, "AirlockComponent") != null ||
             FindComponentByName(target, "DoorBoltComponent") != null ||
             FindComponentByName(target, "WiresVisualsComponent") != null ||
             _itemUtil.HasPrototypeComponent(proto, "Wires") ||
             _itemUtil.HasPrototypeComponent(proto, "WiresPanelSecurity")))
        {
            return true;
        }

        return false;
    }

    public bool DoesEdgeReplaceOrDeleteEntity(EntityUid currentTarget, string graphId, string? expectedNextNode)
    {
        if (string.IsNullOrEmpty(expectedNextNode) || !_entMan.EntityExists(currentTarget))
            return false;

        EnsureCacheInitialized();

        if (!TryGetGraph(graphId, out var graph) ||
            !TryGetNodeByName(graph, expectedNextNode, out var toNode))
        {
            return string.Equals(expectedNextNode, "start", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(expectedNextNode, "girder", StringComparison.OrdinalIgnoreCase);
        }

        if (IsTerminalDeconstructNode(graph, toNode))
            return true;

        var currentProtoId = _entMan.GetComponent<MetaDataComponent>(currentTarget).EntityPrototype?.ID;

        // Transitions on an already-constructed Airlock between "airlock" and any wire-panel security node
        // happen in-place on the existing Airlock entity without replacing or deleting it.
        if (string.Equals(graph.ID, "Airlock", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(currentProtoId, "AirlockAssembly", StringComparison.OrdinalIgnoreCase))
        {
            var toNodeName = GetNodeName(toNode);
            if (string.Equals(toNodeName, "airlock", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(toNodeName, "glassAirlock", StringComparison.OrdinalIgnoreCase) ||
                toNodeName.Contains("Security", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        var entObj = GetMemberValue(toNode, "Entity") ?? GetMemberValue(toNode, "_entity");
        if (entObj != null && entObj.GetType().Name.Contains("BoardNodeEntity", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var nextProtoId = GetNodeEntityId(toNode, currentTarget)
                          ?? GetInferredFramePrototypeForNode(graph, toNode);

        if (!string.IsNullOrEmpty(nextProtoId) &&
            !string.IsNullOrEmpty(currentProtoId) &&
            !string.Equals(currentProtoId, nextProtoId, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return false;
    }

    private string? GetInferredFramePrototypeForNode(ConstructionGraphPrototype graph, ConstructionGraphNode node)
    {
        var nodeName = GetNodeName(node);
        if (string.Equals(graph.ID, "Computer", StringComparison.OrdinalIgnoreCase) &&
            (string.Equals(nodeName, "frameUnsecured", StringComparison.OrdinalIgnoreCase) ||
             string.Equals(nodeName, "boardUnsecured", StringComparison.OrdinalIgnoreCase) ||
             string.Equals(nodeName, "missingWires", StringComparison.OrdinalIgnoreCase) ||
             string.Equals(nodeName, "monitorMissing", StringComparison.OrdinalIgnoreCase) ||
             string.Equals(nodeName, "monitorUnsecured", StringComparison.OrdinalIgnoreCase)))
        {
            return "ComputerFrame";
        }

        if (string.Equals(graph.ID, "Machine", StringComparison.OrdinalIgnoreCase) &&
            (string.Equals(nodeName, "machineFrame", StringComparison.OrdinalIgnoreCase) ||
             string.Equals(nodeName, "missingWires", StringComparison.OrdinalIgnoreCase)))
        {
            return "MachineFrame";
        }

        if (string.Equals(graph.ID, "Airlock", StringComparison.OrdinalIgnoreCase) &&
            (string.Equals(nodeName, "assembly", StringComparison.OrdinalIgnoreCase) ||
             string.Equals(nodeName, "wired", StringComparison.OrdinalIgnoreCase) ||
             string.Equals(nodeName, "electronics", StringComparison.OrdinalIgnoreCase)))
        {
            return "AirlockAssembly";
        }

        return null;
    }

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

    public bool HasMachineFrameBoard(EntityUid uid)
    {
        if (!_entMan.EntityExists(uid))
            return false;

        if (_container.TryGetContainer(uid, "machine_board", out var boardCont) &&
            boardCont.ContainedEntities.Count > 0)
        {
            return true;
        }

        var mfComp = FindComponentByName(uid, "MachineFrameComponent");
        if (mfComp != null && GetMemberValue(mfComp, "HasBoard") is bool hasBoardProp && hasBoardProp)
            return true;

        if (TryGetSpriteLayerZeroState(uid, out var spriteState) && !string.IsNullOrEmpty(spriteState))
        {
            if (spriteState.EndsWith("_2", StringComparison.OrdinalIgnoreCase))
                return true;
            if (spriteState.EndsWith("_0", StringComparison.OrdinalIgnoreCase) ||
                spriteState.EndsWith("_1", StringComparison.OrdinalIgnoreCase))
                return false;
        }

        if (TryGetMachineFrameVisualState(uid, out int visualState))
            return visualState >= 3;

        return false;
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

    private bool TryGetMachineFrameVisualState(EntityUid uid, out int visualState)
    {
        visualState = 0;
        var appComp = FindComponentByName(uid, "AppearanceComponent");
        if (appComp == null)
            return false;

        if (GetMemberValue(appComp, "AppearanceData") is IDictionary dict)
        {
            foreach (DictionaryEntry entry in dict)
            {
                var keyType = entry.Key?.GetType().Name ?? string.Empty;
                var keyName = entry.Key?.ToString() ?? string.Empty;
                if (keyType.Contains("MachineFrameVisuals", StringComparison.OrdinalIgnoreCase) ||
                    keyName.Contains("MachineFrameVisuals", StringComparison.OrdinalIgnoreCase))
                {
                    if (entry.Value is int i)
                    {
                        visualState = i;
                        return true;
                    }
                }
            }
        }

        return false;
    }

    public bool TryGetSpriteLayerZeroState(EntityUid uid, out string? stateName)
    {
        stateName = null;
        var spriteComp = FindComponentByName(uid, "SpriteComponent");
        if (spriteComp == null)
            return false;

        try
        {
            var layerGetState = spriteComp.GetType().GetMethod(
                "LayerGetState",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance,
                null,
                [typeof(int)],
                null);
            if (layerGetState != null)
            {
                var stateIdObj = layerGetState.Invoke(spriteComp, [0]);
                var name = GetStringMember(stateIdObj, "Name") ?? stateIdObj?.ToString();
                if (!string.IsNullOrEmpty(name))
                {
                    stateName = name;
                    return true;
                }
            }

            var layers = GetMemberValue(spriteComp, "AllLayers") as IEnumerable
                         ?? GetMemberValue(spriteComp, "_layers") as IEnumerable;
            if (layers != null)
            {
                foreach (var entry in layers)
                {
                    if (entry == null)
                        continue;

                    var layer = entry is DictionaryEntry de ? de.Value : GetMemberValue(entry, "Value") ?? entry;
                    if (layer == null)
                        continue;

                    var stateObj = GetMemberValue(layer, "RsiState")
                                   ?? GetMemberValue(layer, "State")
                                   ?? GetMemberValue(layer, "_state");
                    var name = GetStringMember(stateObj, "Name") ?? stateObj?.ToString();
                    if (!string.IsNullOrEmpty(name))
                    {
                        stateName = name;
                        return true;
                    }
                    break;
                }
            }
        }
        catch
        {
            // Ignore reflection differences across forks
        }

        return false;
    }

    private string? FindBestBuildTargetNode(ConstructionGraphPrototype graph, ConstructionGraphNode currentNode)
    {
        var currentName = GetNodeName(currentNode);

        // 1. Check ConstructionPrototype recipes associated with this graph (excluding intermediate frame nodes)
        if (_graphToConstructionProtos.TryGetValue(graph.ID, out var protos))
        {
            string? bestTarget = null;
            int bestDist = int.MaxValue;

            foreach (var cProto in protos)
            {
                var tNode = GetStringMember(cProto, "TargetNode");
                if (string.IsNullOrEmpty(tNode) ||
                    string.Equals(tNode, currentName, StringComparison.OrdinalIgnoreCase) ||
                    NonTerminalBuildNodeNames.Contains(tNode))
                {
                    continue;
                }

                var path = FindShortestEdgePath(graph, currentName, tNode);
                if (path != null && path.Count > 0 && path.Count < bestDist)
                {
                    bestDist = path.Count;
                    bestTarget = tNode;
                }
            }

            if (!string.IsNullOrEmpty(bestTarget))
                return bestTarget;
        }

        // 2. Check well-known terminal node names on the graph (e.g. "machine", "computer", "wall", "reinforcedWall", "airlock", "window")
        string[] commonBuildTargets = { "machine", "computer", "wall", "reinforcedWall", "airlock", "window" };
        foreach (var candidate in commonBuildTargets)
        {
            if (!string.Equals(candidate, currentName, StringComparison.OrdinalIgnoreCase) &&
                TryGetNodeByName(graph, candidate, out _))
            {
                var path = FindShortestEdgePath(graph, currentName, candidate);
                if (path != null && path.Count > 0)
                    return candidate;
            }
        }

        // 3. Fallback: nearest reachable node that produces a constructed entity and isn't a deconstruction/start node
        foreach (var node in EnumerateGraphNodes(graph))
        {
            var nodeName = GetNodeName(node);
            if (string.IsNullOrEmpty(nodeName) ||
                string.Equals(nodeName, currentName, StringComparison.OrdinalIgnoreCase) ||
                PrimaryDeconstructTargets.Contains(nodeName, StringComparer.OrdinalIgnoreCase) ||
                NonTerminalBuildNodeNames.Contains(nodeName))
            {
                continue;
            }

            var path = FindShortestEdgePath(graph, currentName, nodeName);
            if (path != null && path.Count > 0)
                return nodeName;
        }

        return null;
    }

    private List<string> GetDeconstructCandidateNodes(
        EntityUid target,
        ConstructionGraphPrototype graph,
        string currentNodeName)
    {
        var result = new List<string>();
        var added = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void TryAdd(string? name)
        {
            if (string.IsNullOrEmpty(name) ||
                string.Equals(name, currentNodeName, StringComparison.OrdinalIgnoreCase))
                return;

            if (TryGetNodeByName(graph, name, out _) && added.Add(name))
                result.Add(name);
        }

        var constructionComp = FindComponentByName(target, "ConstructionComponent");
        if (constructionComp != null)
            TryAdd(GetStringMember(constructionComp, "DeconstructionNode"));

        TryAdd(GetStringMember(graph, "Start"));

        foreach (var primary in PrimaryDeconstructTargets)
            TryAdd(primary);

        // Any node whose Entity is null/empty (such as "start" or a deleted-entity terminal node)
        foreach (var n in EnumerateGraphNodes(graph))
        {
            var name = GetNodeName(n);
            if (string.IsNullOrEmpty(GetNodeEntityId(n, null)) &&
                GetNodeEdges(n).Count == 0)
            {
                TryAdd(name);
            }
        }

        foreach (var fallback in FallbackDeconstructTargets)
            TryAdd(fallback);

        return result;
    }

    private List<(ConstructionGraphNode From, ConstructionGraphEdge Edge, ConstructionGraphNode To)>? FindShortestEdgePath(
        ConstructionGraphPrototype graph,
        string startNodeName,
        string targetNodeName)
    {
        if (!TryGetNodeByName(graph, startNodeName, out var startNode) ||
            !TryGetNodeByName(graph, targetNodeName, out var targetNode))
        {
            return null;
        }

        var queue = new Queue<ConstructionGraphNode>();
        var cameFrom = new Dictionary<string, (ConstructionGraphNode From, ConstructionGraphEdge Edge)>(StringComparer.OrdinalIgnoreCase);
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var startName = GetNodeName(startNode);
        queue.Enqueue(startNode);
        visited.Add(startName);

        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            var currentName = GetNodeName(current);

            if (string.Equals(currentName, targetNodeName, StringComparison.OrdinalIgnoreCase))
            {
                var path = new List<(ConstructionGraphNode From, ConstructionGraphEdge Edge, ConstructionGraphNode To)>();
                var traceNode = targetNode;
                var traceName = GetNodeName(traceNode);

                while (!string.Equals(traceName, startName, StringComparison.OrdinalIgnoreCase))
                {
                    if (!cameFrom.TryGetValue(traceName, out var prev))
                        return null;

                    path.Add((prev.From, prev.Edge, traceNode));
                    traceNode = prev.From;
                    traceName = GetNodeName(traceNode);
                }

                path.Reverse();
                return path;
            }

            foreach (var edge in GetNodeEdges(current))
            {
                var edgeTargetName = GetStringMember(edge, "Target");
                if (string.IsNullOrEmpty(edgeTargetName))
                    continue;

                if (!TryGetNodeByName(graph, edgeTargetName, out var nextNode))
                    continue;

                // Never traverse through a terminal deconstruction node (like "start") as an intermediate step
                if (!string.Equals(edgeTargetName, targetNodeName, StringComparison.OrdinalIgnoreCase) &&
                    IsTerminalDeconstructNode(graph, nextNode))
                {
                    continue;
                }

                if (!visited.Add(edgeTargetName))
                    continue;

                cameFrom[edgeTargetName] = (current, edge);
                queue.Enqueue(nextNode);
            }
        }

        return null;
    }

    private float SumEdgeDoAfter(ConstructionGraphEdge edge)
    {
        float total = 0f;
        var tmp = new List<ConstructionActionStep>();
        ConvertEdgeSteps(edge, string.Empty, tmp);
        foreach (var s in tmp)
            total += s.DoAfterDelay;
        return total;
    }

    private void AppendAnchorTransitionStepIfNeeded(
        EntityUid? target,
        bool isFirstEdge,
        ConstructionGraphPrototype graph,
        ConstructionGraphNode fromNode,
        ConstructionGraphEdge edge,
        ConstructionGraphNode toNode,
        bool isDeconstruct,
        List<ConstructionActionStep> steps)
    {
        var fromName = GetNodeName(fromNode);

        // Avoid adding a duplicate anchoring step if the edge's own steps already use Anchoring
        if (EdgeContainsToolStep(edge, "Anchoring"))
            return;

        if (steps.Count > 0 &&
            steps[^1].Kind == ConstructionActionKind.UseTool &&
            string.Equals(steps[^1].PrototypeOrQualityOrTag, "Anchoring", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        // Never attempt to anchor the "start" ghost node before the structure is even spawned
        if (!isDeconstruct && string.Equals(fromName, "start", StringComparison.OrdinalIgnoreCase))
            return;

        // 1. Check explicit EntityAnchored condition on the edge if present on the client
        if (TryGetEdgeAnchoredCondition(edge, out bool requiredAnchored))
        {
            if (!requiredAnchored)
            {
                steps.Add(new ConstructionActionStep(
                    ConstructionActionKind.UseTool,
                    "Unanchor structure (Anchoring)",
                    PrototypeOrQualityOrTag: "Anchoring",
                    Amount: 1,
                    DoAfterDelay: 1f,
                    ExpectedNextNode: null));
            }
            else
            {
                steps.Add(new ConstructionActionStep(
                    ConstructionActionKind.UseTool,
                    "Anchor frame (Anchoring)",
                    PrototypeOrQualityOrTag: "Anchoring",
                    Amount: 1,
                    DoAfterDelay: 1f,
                    ExpectedNextNode: null));
            }
            return;
        }

        // 2. Fallback when edge conditions are serverOnly (stripped on client)
        if (isDeconstruct)
        {
            if (IsTerminalDeconstructNode(graph, toNode) &&
                NeedsUnanchorBeforeDeconstruct(target, isFirstEdge, graph, fromNode, edge))
            {
                steps.Add(new ConstructionActionStep(
                    ConstructionActionKind.UseTool,
                    "Unanchor structure (Anchoring)",
                    PrototypeOrQualityOrTag: "Anchoring",
                    Amount: 1,
                    DoAfterDelay: 1f,
                    ExpectedNextNode: null));
            }
        }
        else
        {
            // Anchoring intermediate frames/assemblies that spawn unanchored (skipped automatically at runtime if already anchored)
            if (KnownAnchorableFrameNodes.Contains(fromName))
            {
                steps.Add(new ConstructionActionStep(
                    ConstructionActionKind.UseTool,
                    "Anchor frame (Anchoring)",
                    PrototypeOrQualityOrTag: "Anchoring",
                    Amount: 1,
                    DoAfterDelay: 1f,
                    ExpectedNextNode: null));
            }
        }
    }

    private void AppendPostBuildPipeAnchorStepIfNeeded(
        EntityUid? target,
        ConstructionGraphPrototype graph,
        ConstructionGraphNode finalNode,
        List<ConstructionActionStep> steps)
    {
        if (!IsPipeOrAtmosGraphOrNode(graph, finalNode, target))
            return;

        if (steps.Count > 0 &&
            steps[^1].Kind == ConstructionActionKind.UseTool &&
            string.Equals(steps[^1].PrototypeOrQualityOrTag, "Anchoring", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        steps.Add(new ConstructionActionStep(
            ConstructionActionKind.UseTool,
            "Anchor frame (Anchoring)",
            PrototypeOrQualityOrTag: "Anchoring",
            Amount: 1,
            DoAfterDelay: 1f,
            ExpectedNextNode: null));
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

    public void ResolveExportBlueprintPrototypes(
        EntityUid uid,
        string rawEntityProtoId,
        out string exportedEntityProtoId,
        out string? exportedConstructionProtoId)
    {
        EnsureCacheInitialized();

        if (rawEntityProtoId.Contains("Manifold", StringComparison.OrdinalIgnoreCase))
        {
            exportedEntityProtoId = TryExtractPipeLayerSuffix(rawEntityProtoId, out var baseManifold, out _)
                ? baseManifold
                : rawEntityProtoId;
            exportedConstructionProtoId = ResolveConstructionPrototypeForEntity(exportedEntityProtoId);
            return;
        }

        exportedEntityProtoId = rawEntityProtoId;
        int pipeLayer = DetectEntityPipeLayer(uid, out var detectedAltProtoId);

        if (pipeLayer > 0)
        {
            if (!string.IsNullOrEmpty(detectedAltProtoId))
            {
                exportedEntityProtoId = detectedAltProtoId;
            }
            else if (TryExtractPipeLayerSuffix(rawEntityProtoId, out var baseId, out int existingLayer) && existingLayer == pipeLayer)
            {
                exportedEntityProtoId = rawEntityProtoId;
            }
            else
            {
                var cleanBase = TryExtractPipeLayerSuffix(rawEntityProtoId, out var b, out _) ? b : rawEntityProtoId;
                if (_entityAltLayerProtos.TryGetValue((cleanBase, pipeLayer), out var mappedAlt))
                {
                    exportedEntityProtoId = mappedAlt;
                }
                else
                {
                    var suffix = pipeLayer == 1 ? "Alt1" : "Alt2";
                    exportedEntityProtoId = cleanBase + suffix;
                }
            }
        }

        exportedConstructionProtoId = ResolveConstructionPrototypeForEntity(exportedEntityProtoId);
    }

    /// <summary>
    /// Reads the live pipe layer of an entity, or null when it has no adjustable pipe layer.
    /// </summary>
    private int? GetLivePipeLayer(EntityUid uid)
    {
        if (!_entMan.TryGetComponent<AtmosPipeLayersComponent>(uid, out var layers))
            return null;

        // Locked layers (e.g. manifolds) and single-layer entities never report a layered position.
        if (layers.PipeLayersLocked || layers.NumberOfPipeLayers <= 1)
            return null;

        return (int)layers.CurrentPipeLayer;
    }

    private string ResolveAltLayerProtoId(string protoId, int pipeLayer)
    {
        if (string.IsNullOrEmpty(protoId))
            return string.Empty;

        if (TryExtractPipeLayerSuffix(protoId, out _, out int existingLayer) && existingLayer == pipeLayer)
            return protoId;

        var cleanBase = TryExtractPipeLayerSuffix(protoId, out var stripped, out _) ? stripped : protoId;
        if (_entityAltLayerProtos.TryGetValue((cleanBase, pipeLayer), out var mappedAlt))
            return mappedAlt;

        return pipeLayer == 0 ? cleanBase : cleanBase + (pipeLayer == 1 ? "Alt1" : "Alt2");
    }

    /// <summary>
    /// Pipe layer an entity prototype is declared to spawn on, or 0 when it is not layer-specific.
    /// Upstream variantized pipe layers, so the layer lives on <c>AtmosPipeLayers.pipeLayer</c> rather
    /// than in the prototype id suffix.
    /// </summary>
    private int GetDeclaredPipeLayer(string? entityProtoId)
    {
        if (string.IsNullOrEmpty(entityProtoId))
            return 0;

        int suffixLayer = GetPipeLayerForIdOrNode(entityProtoId);
        if (suffixLayer > 0)
            return suffixLayer;

        if (_protoMan.TryIndex<EntityPrototype>(entityProtoId, out var proto) &&
            _itemUtil.TryGetPrototypeComponent<AtmosPipeLayersComponent>(proto, "AtmosPipeLayers", out var layers) &&
            layers != null)
        {
            return (int)layers.CurrentPipeLayer;
        }

        return 0;
    }

    public int DetectEntityPipeLayer(EntityUid uid, out string? altEntityProtoId)
    {
        altEntityProtoId = null;
        if (!_entMan.EntityExists(uid))
            return 0;

        EnsureCacheInitialized();

        var protoId = _entMan.GetComponent<MetaDataComponent>(uid).EntityPrototype?.ID ?? string.Empty;

        // The prototype id is only the *initial* layer. Upstream pipe layers are variant prototypes that are
        // cycled in place via AtmosPipeLayersComponent.CurrentPipeLayer, so a live component value always wins.
        if (GetLivePipeLayer(uid) is { } liveLayer)
        {
            altEntityProtoId = ResolveAltLayerProtoId(protoId, liveLayer);
            return liveLayer;
        }

        if (!string.IsNullOrEmpty(protoId))
        {
            // Manifolds bridge all 3 pipe layers simultaneously and never have a single Alt1/Alt2 pipe layer
            if (protoId.Contains("Manifold", StringComparison.OrdinalIgnoreCase))
            {
                altEntityProtoId = TryExtractPipeLayerSuffix(protoId, out var baseManifold, out _) ? baseManifold : protoId;
                return 0;
            }

            if (_altEntityToBaseAndLayer.TryGetValue(protoId, out var cached))
            {
                altEntityProtoId = protoId;
                return cached.PipeLayer;
            }

            if (TryExtractPipeLayerSuffix(protoId, out _, out int suffixLayer) && suffixLayer > 0)
            {
                altEntityProtoId = protoId;
                return suffixLayer;
            }

            // Standard pipes (GasPipe*, DisposalPipe*) encode their layer directly in their EntityPrototype ID
            // (e.g. GasPipeStraight, GasPipeStraightAlt1, GasPipeBendAlt2) and are placed directly without screwdriving.
            if (protoId.Contains("GasPipe", StringComparison.OrdinalIgnoreCase) ||
                protoId.Contains("DisposalPipe", StringComparison.OrdinalIgnoreCase))
            {
                return 0;
            }
        }

        int detectedLayer = 0;
        IDictionary? liveAltProtos = null;
        const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

        // 1. Inspect AtmosPipeLayersComponent and all live components for AtmosPipeLayer / PipeLayer fields or properties
        foreach (var comp in _entMan.GetComponents(uid))
        {
            var compType = comp.GetType();
            var compName = compType.Name;
            if (compName is "TransformComponent" or "MetaDataComponent" or "PhysicsComponent" or "FixturesComponent")
                continue;

            if (liveAltProtos == null &&
                (GetMemberValue(comp, "AlternativePrototypes") ?? GetMemberValue(comp, "_alternativePrototypes")) is IDictionary altDict)
            {
                liveAltProtos = altDict;
            }

            if (compName.Contains("PipeLayer", StringComparison.OrdinalIgnoreCase) ||
                compName.Contains("AtmosPipe", StringComparison.OrdinalIgnoreCase))
            {
                var rawLayer = GetMemberValue(comp, "CurrentPipeLayer")
                               ?? GetMemberValue(comp, "PipeLayer")
                               ?? GetMemberValue(comp, "Layer")
                               ?? GetMemberValue(comp, "CurrentLayer");
                if (TryParsePipeLayerValue(rawLayer, out int compLayer) && compLayer > 0)
                {
                    detectedLayer = compLayer;
                    break;
                }
            }

            foreach (var prop in compType.GetProperties(flags))
            {
                if (prop.GetIndexParameters().Length > 0)
                    continue;

                var pTypeName = prop.PropertyType.Name;
                bool isPipeLayerMember = pTypeName.Contains("PipeLayer", StringComparison.OrdinalIgnoreCase) ||
                                         prop.Name.Contains("PipeLayer", StringComparison.OrdinalIgnoreCase);
                if (isPipeLayerMember || prop.PropertyType.IsEnum)
                {
                    try
                    {
                        var val = prop.GetValue(comp);
                        if (isPipeLayerMember)
                        {
                            if (TryParsePipeLayerValue(val, out int pLayer) && pLayer > 0)
                            {
                                detectedLayer = pLayer;
                                break;
                            }
                        }
                        else if (val is Enum eVal)
                        {
                            var eStr = eVal.ToString();
                            if (eStr.Equals("Secondary", StringComparison.OrdinalIgnoreCase) ||
                                eStr.Equals("Alt1", StringComparison.OrdinalIgnoreCase))
                            {
                                detectedLayer = 1;
                                break;
                            }
                            if (eStr.Equals("Tertiary", StringComparison.OrdinalIgnoreCase) ||
                                eStr.Equals("Alt2", StringComparison.OrdinalIgnoreCase))
                            {
                                detectedLayer = 2;
                                break;
                            }
                        }
                    }
                    catch
                    {
                        // Ignore reflection getter exceptions
                    }
                }
            }

            if (detectedLayer > 0)
                break;

            foreach (var field in compType.GetFields(flags))
            {
                var fTypeName = field.FieldType.Name;
                bool isPipeLayerField = fTypeName.Contains("PipeLayer", StringComparison.OrdinalIgnoreCase) ||
                                        field.Name.Contains("PipeLayer", StringComparison.OrdinalIgnoreCase);
                if (isPipeLayerField || field.FieldType.IsEnum)
                {
                    try
                    {
                        var val = field.GetValue(comp);
                        if (isPipeLayerField)
                        {
                            if (TryParsePipeLayerValue(val, out int fLayer) && fLayer > 0)
                            {
                                detectedLayer = fLayer;
                                break;
                            }
                        }
                        else if (val is Enum eVal)
                        {
                            var eStr = eVal.ToString();
                            if (eStr.Equals("Secondary", StringComparison.OrdinalIgnoreCase) ||
                                eStr.Equals("Alt1", StringComparison.OrdinalIgnoreCase))
                            {
                                detectedLayer = 1;
                                break;
                            }
                            if (eStr.Equals("Tertiary", StringComparison.OrdinalIgnoreCase) ||
                                eStr.Equals("Alt2", StringComparison.OrdinalIgnoreCase))
                            {
                                detectedLayer = 2;
                                break;
                            }
                        }
                    }
                    catch
                    {
                        // Ignore
                    }
                }
            }

            if (detectedLayer > 0)
                break;
        }

        // 2. Inspect NodeContainerComponent PipeNodes if present on client
        if (detectedLayer == 0 && !protoId.Contains("Manifold", StringComparison.OrdinalIgnoreCase))
        {
            var nodeCont = FindComponentByName(uid, "NodeContainerComponent");
            if (nodeCont != null &&
                (GetMemberValue(nodeCont, "Nodes") ?? GetMemberValue(nodeCont, "_nodes")) is IDictionary nodesDict)
            {
                var foundLayers = new HashSet<int>();
                int primaryPipeNodeLayer = 0;

                foreach (DictionaryEntry entry in nodesDict)
                {
                    var nodeObj = entry.Value;
                    if (nodeObj == null)
                        continue;

                    var rawNodeLayer = GetMemberValue(nodeObj, "CurrentPipeLayer")
                                       ?? GetMemberValue(nodeObj, "PipeLayer")
                                       ?? GetMemberValue(nodeObj, "Layer");
                    if (TryParsePipeLayerValue(rawNodeLayer, out int nLayer))
                    {
                        foundLayers.Add(nLayer);
                        var nodeKey = entry.Key?.ToString() ?? string.Empty;
                        if (nLayer > 0 &&
                            (nodeKey.Equals("pipe", StringComparison.OrdinalIgnoreCase) ||
                             nodeKey.Equals("inlet", StringComparison.OrdinalIgnoreCase) ||
                             nodeKey.Equals("port", StringComparison.OrdinalIgnoreCase)))
                        {
                            primaryPipeNodeLayer = nLayer;
                        }
                    }
                }

                if (foundLayers.Count == 1)
                {
                    int single = foundLayers.First();
                    if (single > 0)
                        detectedLayer = single;
                }
                else if (primaryPipeNodeLayer > 0 && foundLayers.Count < 3)
                {
                    detectedLayer = primaryPipeNodeLayer;
                }
            }
        }

        // 3. Inspect AppearanceComponent.AppearanceData for AtmosPipeLayer / PipeVisuals
        if (detectedLayer == 0)
        {
            var appComp = FindComponentByName(uid, "AppearanceComponent");
            if (appComp != null &&
                (GetMemberValue(appComp, "AppearanceData") ?? GetMemberValue(appComp, "_appearanceData")) is IDictionary appDict)
            {
                foreach (DictionaryEntry entry in appDict)
                {
                    if (entry.Value is Enum valEnum)
                    {
                        var enumStr = valEnum.ToString();
                        if (enumStr.Equals("Secondary", StringComparison.OrdinalIgnoreCase) ||
                            enumStr.Equals("Alt1", StringComparison.OrdinalIgnoreCase))
                        {
                            detectedLayer = 1;
                            break;
                        }
                        if (enumStr.Equals("Tertiary", StringComparison.OrdinalIgnoreCase) ||
                            enumStr.Equals("Alt2", StringComparison.OrdinalIgnoreCase))
                        {
                            detectedLayer = 2;
                            break;
                        }
                    }

                    var keyType = entry.Key?.GetType().Name ?? string.Empty;
                    var keyStr = entry.Key?.ToString() ?? string.Empty;
                    var valType = entry.Value?.GetType().Name ?? string.Empty;
                    if (keyType.Contains("PipeLayer", StringComparison.OrdinalIgnoreCase) ||
                        keyStr.Contains("PipeLayer", StringComparison.OrdinalIgnoreCase) ||
                        valType.Contains("PipeLayer", StringComparison.OrdinalIgnoreCase) ||
                        keyType.Contains("AtmosPipe", StringComparison.OrdinalIgnoreCase))
                    {
                        if (TryParsePipeLayerValue(entry.Value, out int appLayer) && appLayer > 0)
                        {
                            detectedLayer = appLayer;
                            break;
                        }
                    }
                }
            }
        }

        // 4. Inspect SpriteComponent visible layers for Alt1/Alt2 or Secondary/Tertiary RSI states
        if (detectedLayer == 0)
        {
            var spriteComp = FindComponentByName(uid, "SpriteComponent");
            if (spriteComp != null)
            {
                var layers = GetMemberValue(spriteComp, "AllLayers") as IEnumerable
                             ?? GetMemberValue(spriteComp, "_layers") as IEnumerable;
                if (layers != null)
                {
                    foreach (var entry in layers)
                    {
                        if (entry == null)
                            continue;

                        var layer = entry is DictionaryEntry de ? de.Value : GetMemberValue(entry, "Value") ?? entry;
                        if (layer == null)
                            continue;

                        if (GetMemberValue(layer, "Visible") is false)
                            continue;

                        var stateObj = GetMemberValue(layer, "State");
                        var stateName = GetStringMember(stateObj, "Name")
                                        ?? GetStringMember(layer, "RsiState")
                                        ?? stateObj?.ToString()
                                        ?? string.Empty;
                        var rsiObj = GetMemberValue(layer, "Rsi") ?? GetMemberValue(layer, "ActualRsi");
                        var rsiPath = GetStringMember(rsiObj, "Path") ?? rsiObj?.ToString() ?? string.Empty;

                        bool isFrame = protoId.Contains("Frame", StringComparison.OrdinalIgnoreCase);
                        if (stateName.Contains("Alt1", StringComparison.OrdinalIgnoreCase) ||
                            stateName.Contains("Secondary", StringComparison.OrdinalIgnoreCase) ||
                            (!isFrame && stateName.EndsWith("_1", StringComparison.OrdinalIgnoreCase)) ||
                            rsiPath.Contains("alt1", StringComparison.OrdinalIgnoreCase) ||
                            rsiPath.Contains("secondary", StringComparison.OrdinalIgnoreCase))
                        {
                            detectedLayer = 1;
                            break;
                        }

                        if (stateName.Contains("Alt2", StringComparison.OrdinalIgnoreCase) ||
                            stateName.Contains("Tertiary", StringComparison.OrdinalIgnoreCase) ||
                            (!isFrame && stateName.EndsWith("_2", StringComparison.OrdinalIgnoreCase)) ||
                            rsiPath.Contains("alt2", StringComparison.OrdinalIgnoreCase) ||
                            rsiPath.Contains("tertiary", StringComparison.OrdinalIgnoreCase))
                        {
                            detectedLayer = 2;
                            break;
                        }
                    }
                }
            }
        }

        if (detectedLayer > 0)
        {
            if (liveAltProtos != null)
            {
                foreach (DictionaryEntry entry in liveAltProtos)
                {
                    if (TryParsePipeLayerValue(entry.Key, out int kLayer) && kLayer == detectedLayer)
                    {
                        var candidate = ExtractIdString(entry.Value);
                        if (!string.IsNullOrEmpty(candidate))
                        {
                            altEntityProtoId = candidate;
                            break;
                        }
                    }
                }
            }

            if (string.IsNullOrEmpty(altEntityProtoId) && !string.IsNullOrEmpty(protoId))
            {
                var cleanBase = TryExtractPipeLayerSuffix(protoId, out var b, out _) ? b : protoId;
                if (_entityAltLayerProtos.TryGetValue((cleanBase, detectedLayer), out var cachedAlt))
                {
                    altEntityProtoId = cachedAlt;
                }
                else
                {
                    var suffix = detectedLayer == 1 ? "Alt1" : "Alt2";
                    altEntityProtoId = cleanBase + suffix;
                }
            }
        }

        return detectedLayer;
    }

    public bool ShouldUseScrewdriverForPipeLayer(
        ConstructionGraphPrototype graph,
        ConstructionGraphNode node,
        string? desiredEntityProtoId)
    {
        if (!IsPipeOrAtmosGraphOrNode(graph, node, null))
            return false;

        var graphId = graph.ID ?? string.Empty;
        var nodeName = GetNodeName(node);
        var protoId = desiredEntityProtoId ?? string.Empty;

        // Pipes (GasPipe / DisposalPipe) and Manifolds are placed directly on their target layer via ConstructionPrototype,
        // never via Screwdriver cycling. Only use Screwdriver cycling for contraptions (connectors, mixers, filters, vents, etc.).
        if (graphId.Equals("GasPipe", StringComparison.OrdinalIgnoreCase) ||
            graphId.Contains("Disposal", StringComparison.OrdinalIgnoreCase) ||
            nodeName.Contains("manifold", StringComparison.OrdinalIgnoreCase) ||
            protoId.Contains("GasPipe", StringComparison.OrdinalIgnoreCase) ||
            protoId.Contains("DisposalPipe", StringComparison.OrdinalIgnoreCase) ||
            protoId.Contains("Manifold", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return true;
    }

    public int GetDesiredPipeLayer(string? entityProtoId, string? fallbackNodeOrRecipeId)
    {
        EnsureCacheInitialized();

        if (!string.IsNullOrEmpty(entityProtoId))
        {
            if (entityProtoId.Contains("Manifold", StringComparison.OrdinalIgnoreCase))
                return 0;

            if (_altEntityToBaseAndLayer.TryGetValue(entityProtoId, out var cached))
                return cached.PipeLayer;

            if (TryExtractPipeLayerSuffix(entityProtoId, out _, out int layer) && layer > 0)
                return layer;
        }

        if (!string.IsNullOrEmpty(fallbackNodeOrRecipeId))
        {
            if (fallbackNodeOrRecipeId.Contains("Manifold", StringComparison.OrdinalIgnoreCase))
                return 0;

            if (TryExtractPipeLayerSuffix(fallbackNodeOrRecipeId, out _, out int fbLayer) && fbLayer > 0)
                return fbLayer;
        }

        return 0;
    }

    public static int GetPipeLayerForIdOrNode(string? idOrNodeName)
    {
        return TryExtractPipeLayerSuffix(idOrNodeName, out _, out int layer) ? layer : 0;
    }

    public string NormalizeBaseEntityPrototypeId(string? protoId)
    {
        if (string.IsNullOrEmpty(protoId))
            return string.Empty;

        EnsureCacheInitialized();

        if (_altEntityToBaseAndLayer.TryGetValue(protoId, out var cached))
            return cached.BaseProtoId;

        return TryExtractPipeLayerSuffix(protoId, out var baseId, out _) ? baseId : protoId;
    }

    public static bool TryExtractPipeLayerSuffix(string? id, out string baseId, out int pipeLayer)
    {
        baseId = id ?? string.Empty;
        pipeLayer = 0;
        if (string.IsNullOrEmpty(id))
            return false;

        if (id.EndsWith("Alt1", StringComparison.OrdinalIgnoreCase))
        {
            baseId = id[..^4];
            pipeLayer = 1;
            return true;
        }

        if (id.EndsWith("Alt2", StringComparison.OrdinalIgnoreCase))
        {
            baseId = id[..^4];
            pipeLayer = 2;
            return true;
        }

        if (id.EndsWith("Secondary", StringComparison.OrdinalIgnoreCase) && id.Length > 9)
        {
            baseId = id[..^9];
            pipeLayer = 1;
            return true;
        }

        if (id.EndsWith("Tertiary", StringComparison.OrdinalIgnoreCase) && id.Length > 8)
        {
            baseId = id[..^8];
            pipeLayer = 2;
            return true;
        }

        if (id.EndsWith("Alt1Flipped", StringComparison.OrdinalIgnoreCase) && id.Length > 11)
        {
            baseId = id[..^11] + "Flipped";
            pipeLayer = 1;
            return true;
        }

        if (id.EndsWith("Alt2Flipped", StringComparison.OrdinalIgnoreCase) && id.Length > 11)
        {
            baseId = id[..^11] + "Flipped";
            pipeLayer = 2;
            return true;
        }

        return false;
    }

    private static bool TryParsePipeLayerValue(object? val, out int pipeLayer)
    {
        pipeLayer = 0;
        if (val == null)
            return false;

        var str = val.ToString() ?? string.Empty;
        if (str.Equals("Primary", StringComparison.OrdinalIgnoreCase) ||
            str.Equals("Center", StringComparison.OrdinalIgnoreCase) ||
            str.Equals("0", StringComparison.OrdinalIgnoreCase))
        {
            pipeLayer = 0;
            return true;
        }

        if (str.Equals("Secondary", StringComparison.OrdinalIgnoreCase) ||
            str.Equals("Alt1", StringComparison.OrdinalIgnoreCase) ||
            str.Equals("1", StringComparison.OrdinalIgnoreCase))
        {
            pipeLayer = 1;
            return true;
        }

        if (str.Equals("Tertiary", StringComparison.OrdinalIgnoreCase) ||
            str.Equals("Alt2", StringComparison.OrdinalIgnoreCase) ||
            str.Equals("2", StringComparison.OrdinalIgnoreCase))
        {
            pipeLayer = 2;
            return true;
        }

        if (val is Enum or int or byte or sbyte or short or ushort)
        {
            try
            {
                int v = Convert.ToInt32(val);
                if (v >= 0 && v <= 2)
                {
                    pipeLayer = v;
                    return true;
                }
            }
            catch
            {
                // Ignore
            }
        }

        return false;
    }

    private static void AppendPipeLayerScrewdriverSteps(
        int currentLayer,
        int desiredLayer,
        List<ConstructionActionStep> steps)
    {
        int cur = ((currentLayer % 3) + 3) % 3;
        int dst = ((desiredLayer % 3) + 3) % 3;
        if (cur == dst)
            return;

        int clicks = (dst - cur + 3) % 3;
        for (int i = 1; i <= clicks; i++)
        {
            int stepTargetLayer = (cur + i) % 3;
            string label = stepTargetLayer switch
            {
                1 => "Alt1/Secondary",
                2 => "Alt2/Tertiary",
                _ => "Primary"
            };

            steps.Add(new ConstructionActionStep(
                ConstructionActionKind.UseTool,
                $"Set pipe layer to {label} (Screwing)",
                PrototypeOrQualityOrTag: "Screwing",
                Amount: 1,
                DoAfterDelay: 0.25f,
                ExpectedNextNode: null,
                TargetPipeLayer: stepTargetLayer));
        }
    }


    private bool IsTerminalDeconstructNode(ConstructionGraphPrototype graph, ConstructionGraphNode toNode)
    {
        var toName = GetNodeName(toNode);
        if (string.IsNullOrEmpty(toName))
            return false;

        if (string.Equals(toName, "start", StringComparison.OrdinalIgnoreCase))
            return true;

        var graphStart = GetStringMember(graph, "Start");
        if (!string.IsNullOrEmpty(graphStart) &&
            string.Equals(toName, graphStart, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (PrimaryDeconstructTargets.Contains(toName, StringComparer.OrdinalIgnoreCase))
            return true;

        return string.IsNullOrEmpty(GetNodeEntityId(toNode, null)) &&
               GetNodeEdges(toNode).Count == 0;
    }

    private bool NeedsUnanchorBeforeDeconstruct(
        EntityUid? target,
        bool isFirstEdge,
        ConstructionGraphPrototype graph,
        ConstructionGraphNode fromNode,
        ConstructionGraphEdge edge)
    {
        var fromName = GetNodeName(fromNode);

        if (KnownAnchorableFrameNodes.Contains(fromName))
            return true;

        if (IsPipeOrAtmosGraphOrNode(graph, fromNode, target))
            return true;

        // Machine frames at missingWires and Computer frames at frameUnsecured dismantle directly via Screwing
        if ((string.Equals(graph.ID, "Machine", StringComparison.OrdinalIgnoreCase) &&
             string.Equals(fromName, "missingWires", StringComparison.OrdinalIgnoreCase)) ||
            (string.Equals(graph.ID, "Computer", StringComparison.OrdinalIgnoreCase) &&
             string.Equals(fromName, "frameUnsecured", StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        // Grilles deconstruct directly via Cutting without unanchoring
        if (string.Equals(graph.ID, "Grille", StringComparison.OrdinalIgnoreCase) ||
            fromName.Contains("grille", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (HasAnchorableComponent(target, isFirstEdge, graph, fromNode) &&
            (EdgeContainsToolStep(edge, "Welding") ||
             EdgeContainsToolStep(edge, "Screwing") ||
             EdgeContainsToolStep(edge, "Prying")))
        {
            return true;
        }

        return false;
    }

    private bool IsPipeOrAtmosGraphOrNode(
        ConstructionGraphPrototype graph,
        ConstructionGraphNode node,
        EntityUid? target)
    {
        var graphId = graph.ID ?? string.Empty;
        var nodeName = GetNodeName(node);
        var nodeEntId = GetNodeEntityId(node, target) ?? string.Empty;
        var targetProtoId = target.HasValue && _entMan.EntityExists(target.Value)
            ? _entMan.GetComponent<MetaDataComponent>(target.Value).EntityPrototype?.ID ?? string.Empty
            : string.Empty;

        foreach (var kw in PipeAndAtmosKeywords)
        {
            if (graphId.Contains(kw, StringComparison.OrdinalIgnoreCase) ||
                nodeName.Contains(kw, StringComparison.OrdinalIgnoreCase) ||
                nodeEntId.Contains(kw, StringComparison.OrdinalIgnoreCase) ||
                targetProtoId.Contains(kw, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private bool HasAnchorableComponent(
        EntityUid? target,
        bool isFirstEdge,
        ConstructionGraphPrototype graph,
        ConstructionGraphNode fromNode)
    {
        if (isFirstEdge && target.HasValue && _entMan.EntityExists(target.Value))
        {
            if (_entMan.HasComponent<AnchorableComponent>(target.Value) ||
                FindComponentByName(target.Value, "AnchorableComponent") != null)
            {
                return true;
            }
        }

        var protoId = GetNodeEntityId(fromNode, target)
                      ?? GetInferredFramePrototypeForNode(graph, fromNode);
        if (!string.IsNullOrEmpty(protoId) &&
            _protoMan.TryIndex<EntityPrototype>(protoId, out var eProto))
        {
            if (_itemUtil.HasPrototypeComponent(eProto, "Anchorable"))
                return true;
        }

        return false;
    }

    private bool TryGetEdgeAnchoredCondition(ConstructionGraphEdge edge, out bool requiredAnchored)
    {
        requiredAnchored = false;
        foreach (var cond in EnumerateEdgeConditions(edge))
        {
            if (cond.GetType().Name.Contains("EntityAnchored", StringComparison.OrdinalIgnoreCase))
            {
                if (GetMemberValue(cond, "Anchored") is bool b)
                {
                    requiredAnchored = b;
                    return true;
                }
            }
        }
        return false;
    }

    private bool TryGetEdgeWirePanelCondition(ConstructionGraphEdge edge, out bool requiredOpen)
    {
        requiredOpen = true;
        foreach (var cond in EnumerateEdgeConditions(edge))
        {
            if (cond.GetType().Name.Contains("WirePanel", StringComparison.OrdinalIgnoreCase))
            {
                if (GetMemberValue(cond, "Open") is bool b)
                    requiredOpen = b;
                return true;
            }
        }
        return false;
    }

    private bool TryGetEdgeDoorWeldedCondition(ConstructionGraphEdge edge, out bool requiredWelded)
    {
        requiredWelded = true;
        foreach (var cond in EnumerateEdgeConditions(edge))
        {
            if (cond.GetType().Name.Contains("DoorWelded", StringComparison.OrdinalIgnoreCase))
            {
                if (GetMemberValue(cond, "Welded") is bool b)
                    requiredWelded = b;
                return true;
            }
        }
        return false;
    }

    private bool TryGetEdgeAllWiresCutCondition(ConstructionGraphEdge edge, out bool requiredAllCut)
    {
        requiredAllCut = true;
        foreach (var cond in EnumerateEdgeConditions(edge))
        {
            var cName = cond.GetType().Name;
            if (cName.Contains("AllWiresCut", StringComparison.OrdinalIgnoreCase) ||
                cName.Contains("WiresCut", StringComparison.OrdinalIgnoreCase))
            {
                if (GetMemberValue(cond, "Value") is bool b)
                    requiredAllCut = b;
                return true;
            }
        }
        return false;
    }

    private static IEnumerable<object> EnumerateEdgeConditions(ConstructionGraphEdge edge)
    {
        var conds = GetMemberValue(edge, "Conditions") as IEnumerable
                    ?? GetMemberValue(edge, "_conditions") as IEnumerable;
        if (conds == null)
            yield break;

        foreach (var c in conds)
        {
            if (c != null)
                yield return c;
        }
    }

    private bool FirstEdgeStepIsTool(ConstructionGraphEdge edge, string toolQuality)
    {
        var tmp = new List<ConstructionActionStep>();
        ConvertEdgeSteps(edge, string.Empty, tmp);
        return tmp.Count > 0 &&
               tmp[0].Kind == ConstructionActionKind.UseTool &&
               string.Equals(tmp[0].PrototypeOrQualityOrTag, toolQuality, StringComparison.OrdinalIgnoreCase);
    }

    private bool EdgeContainsToolStep(ConstructionGraphEdge edge, string toolQuality)
    {
        var tmp = new List<ConstructionActionStep>();
        ConvertEdgeSteps(edge, string.Empty, tmp);
        foreach (var s in tmp)
        {
            if (s.Kind == ConstructionActionKind.UseTool &&
                string.Equals(s.PrototypeOrQualityOrTag, toolQuality, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }

    private void AppendMachineFrameStepsIfNeeded(
        EntityUid? target,
        ConstructionGraphPrototype graph,
        ConstructionGraphNode fromNode,
        ConstructionGraphNode toNode,
        string? desiredEntityProtoId,
        List<ConstructionActionStep> steps)
    {
        if (!string.Equals(graph.ID, "Machine", StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(GetNodeName(fromNode), "machineFrame", StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(GetNodeName(toNode), "machine", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        bool alreadyHasBoard = target.HasValue && HasMachineFrameBoard(target.Value);
        EntityUid? boardUid = null;
        EntityPrototype? boardProto = null;

        if (target.HasValue &&
            _entMan.EntityExists(target.Value) &&
            _container.TryGetContainer(target.Value, "machine_board", out var boardContainer) &&
            boardContainer.ContainedEntities.Count > 0)
        {
            boardUid = boardContainer.ContainedEntities[0];
            boardProto = _entMan.GetComponent<MetaDataComponent>(boardUid.Value).EntityPrototype;
            if (boardProto != null)
                _lastKnownMachineBoardProtoId = boardProto.ID;
        }

        if (boardUid == null && boardProto == null)
        {
            if (!alreadyHasBoard)
            {
                TryFindAvailableMachineBoard(target, desiredEntityProtoId, out boardUid, out boardProto);
            }
            else
            {
                // Frame already has a board inside, but closed container contents may not be networked to client;
                // resolve board prototype from desiredEntityProtoId or the last held/inserted MachineBoard.
                if (!string.IsNullOrEmpty(desiredEntityProtoId))
                    TryFindBoardPrototypeForMachine(desiredEntityProtoId, out boardProto);
                if (boardProto == null && !string.IsNullOrEmpty(_lastKnownMachineBoardProtoId))
                    _protoMan.TryIndex(_lastKnownMachineBoardProtoId, out boardProto);
                if (boardProto == null)
                    TryFindAvailableMachineBoard(target, desiredEntityProtoId, out boardUid, out boardProto);
            }
        }

        if (!alreadyHasBoard)
        {
            if (boardProto != null)
            {
                _lastKnownMachineBoardProtoId = boardProto.ID;
                steps.Add(new ConstructionActionStep(
                    ConstructionActionKind.InsertPrototype,
                    $"Insert {boardProto.ID}",
                    PrototypeOrQualityOrTag: boardProto.ID,
                    Amount: 1,
                    DoAfterDelay: 0f,
                    ExpectedNextNode: null));
            }
            else
            {
                steps.Add(new ConstructionActionStep(
                    ConstructionActionKind.InsertComponent,
                    "Insert MachineBoard",
                    PrototypeOrQualityOrTag: "MachineBoard",
                    Amount: 1,
                    DoAfterDelay: 0f,
                    ExpectedNextNode: null));
            }
        }

        object? boardComp = null;
        if (boardUid.HasValue && _entMan.EntityExists(boardUid.Value))
            boardComp = FindComponentByName(boardUid.Value, "MachineBoardComponent");
        if (boardComp == null && boardProto != null)
            _itemUtil.TryGetPrototypeComponent<object>(boardProto, "MachineBoard", out boardComp);

        if (boardComp == null)
            return;

        // 1. Stack / Material Requirements (StackRequirements, MaterialRequirements, MaterialIdRequirements)
        var seenStacks = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string[] stackPropNames = { "StackRequirements", "MaterialRequirements", "MaterialIdRequirements" };
        foreach (var propName in stackPropNames)
        {
            if (GetMemberValue(boardComp, propName) is not IDictionary stackReqs)
                continue;

            foreach (DictionaryEntry entry in stackReqs)
            {
                var stackId = ExtractIdString(entry.Key);
                var amount = entry.Value is int i ? i : GetIntMember(entry.Value, "Amount", 1);
                if (amount > 0 && !string.IsNullOrEmpty(stackId) && seenStacks.Add(stackId))
                {
                    steps.Add(new ConstructionActionStep(
                        ConstructionActionKind.AddMaterial,
                        $"Add {amount}x {stackId}",
                        PrototypeOrQualityOrTag: stackId,
                        Amount: amount,
                        DoAfterDelay: 0f,
                        ExpectedNextNode: null));
                }
            }
        }

        // 2. MachinePart Requirements (Requirements, MachinePartRequirements, PartRequirements)
        var seenParts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string[] partPropNames = { "Requirements", "MachinePartRequirements", "PartRequirements" };
        foreach (var propName in partPropNames)
        {
            if (GetMemberValue(boardComp, propName) is not IDictionary partReqs)
                continue;

            foreach (DictionaryEntry entry in partReqs)
            {
                var partId = ExtractIdString(entry.Key);
                var amount = entry.Value is int i ? i : GetIntMember(entry.Value, "Amount", 1);
                if (amount <= 0 || string.IsNullOrEmpty(partId) || !seenParts.Add(partId) || seenStacks.Contains(partId))
                    continue;

                if (_protoMan.HasIndex<Content.Shared.Stacks.StackPrototype>(partId))
                {
                    steps.Add(new ConstructionActionStep(
                        ConstructionActionKind.AddMaterial,
                        $"Add {amount}x {partId}",
                        PrototypeOrQualityOrTag: partId,
                        Amount: amount,
                        DoAfterDelay: 0f,
                        ExpectedNextNode: null));
                }
                else
                {
                    for (int k = 0; k < amount; k++)
                    {
                        steps.Add(new ConstructionActionStep(
                            ConstructionActionKind.InsertComponent,
                            $"Insert {partId} ({k + 1}/{amount})",
                            PrototypeOrQualityOrTag: $"MachinePart:{partId}",
                            Amount: 1,
                            DoAfterDelay: 0f,
                            ExpectedNextNode: null));
                    }
                }
            }
        }

        // 3. Component Requirements (each item inserted individually)
        if (GetMemberValue(boardComp, "ComponentRequirements") is IDictionary compReqs)
        {
            foreach (DictionaryEntry entry in compReqs)
            {
                var compName = ExtractIdString(entry.Key);
                var amt = entry.Value is int i ? i : GetIntMember(entry.Value, "Amount", 1);
                if (amt > 0 && !string.IsNullOrEmpty(compName))
                {
                    for (int k = 0; k < amt; k++)
                    {
                        steps.Add(new ConstructionActionStep(
                            ConstructionActionKind.InsertComponent,
                            $"Insert {compName} ({k + 1}/{amt})",
                            PrototypeOrQualityOrTag: compName,
                            Amount: 1,
                            DoAfterDelay: 0f,
                            ExpectedNextNode: null));
                    }
                }
            }
        }

        // 4. Tag Requirements (each item inserted individually)
        if (GetMemberValue(boardComp, "TagRequirements") is IDictionary tagReqs)
        {
            foreach (DictionaryEntry entry in tagReqs)
            {
                var tagId = ExtractIdString(entry.Key);
                var amt = entry.Value is int i ? i : GetIntMember(entry.Value, "Amount", 1);
                if (amt > 0 && !string.IsNullOrEmpty(tagId))
                {
                    for (int k = 0; k < amt; k++)
                    {
                        steps.Add(new ConstructionActionStep(
                            ConstructionActionKind.InsertTag,
                            $"Insert [{tagId}] ({k + 1}/{amt})",
                            PrototypeOrQualityOrTag: tagId,
                            Amount: 1,
                            DoAfterDelay: 0f,
                            ExpectedNextNode: null));
                    }
                }
            }
        }
    }

    public bool IsMachineBoardEntity(EntityUid uid)
    {
        if (!_entMan.EntityExists(uid))
            return false;

        if (FindComponentByName(uid, "MachineBoardComponent") != null)
            return true;

        var proto = _entMan.GetComponent<MetaDataComponent>(uid).EntityPrototype;
        if (proto == null)
            return false;

        if (_itemUtil.HasPrototypeComponent(proto, "MachineBoard"))
            return true;

        if (proto.ID.Contains("MachineCircuitboard", StringComparison.OrdinalIgnoreCase))
            return true;

        if (proto.Parents != null &&
            proto.Parents.Any(p => p.Contains("MachineCircuitboard", StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        return false;
    }

    private bool TryFindAvailableMachineBoard(
        EntityUid? target,
        string? desiredMachineProtoId,
        out EntityUid? boardUid,
        out EntityPrototype? boardProto)
    {
        boardUid = null;
        boardProto = null;

        var candidates = _itemUtil.FindItemsByPredicate(IsMachineBoardEntity);
        if (!string.IsNullOrEmpty(desiredMachineProtoId))
        {
            foreach (var loc in candidates)
            {
                var proto = _entMan.GetComponent<MetaDataComponent>(loc.Entity).EntityPrototype;
                object? boardComp = FindComponentByName(loc.Entity, "MachineBoardComponent");
                if (boardComp == null && proto != null)
                    _itemUtil.TryGetPrototypeComponent<object>(proto, "MachineBoard", out boardComp);

                var builtProto = GetStringMember(boardComp, "Prototype")
                                 ?? GetStringMember(boardComp, "MachinePrototype")
                                 ?? GetStringMember(boardComp, "Machine");
                if (string.Equals(builtProto, desiredMachineProtoId, StringComparison.OrdinalIgnoreCase))
                {
                    boardUid = loc.Entity;
                    boardProto = proto;
                    return true;
                }
            }
        }

        if (candidates.Count > 0)
        {
            boardUid = candidates[0].Entity;
            boardProto = _entMan.GetComponent<MetaDataComponent>(boardUid.Value).EntityPrototype;
            return true;
        }

        if (!string.IsNullOrEmpty(desiredMachineProtoId) &&
            TryFindBoardPrototypeForMachine(desiredMachineProtoId, out boardProto))
        {
            return true;
        }

        return false;
    }

    private bool TryFindBoardPrototypeForMachine(string machineProtoId, out EntityPrototype? boardProto)
    {
        boardProto = null;
        foreach (var eProto in _protoMan.EnumeratePrototypes<EntityPrototype>())
        {
            if (!_itemUtil.TryGetPrototypeComponent<object>(eProto, "MachineBoard", out var boardComp) || boardComp == null)
                continue;

            var builtProto = GetStringMember(boardComp, "Prototype")
                             ?? GetStringMember(boardComp, "MachinePrototype")
                             ?? GetStringMember(boardComp, "Machine");
            if (string.Equals(builtProto, machineProtoId, StringComparison.OrdinalIgnoreCase))
            {
                boardProto = eProto;
                return true;
            }
        }
        return false;
    }

    private bool HasPrototypeContainer(EntityPrototype proto, string containerId)
    {
        if (!_itemUtil.TryGetPrototypeComponent<object>(proto, "ContainerContainer", out var ccObj) || ccObj == null)
            return false;

        if (GetMemberValue(ccObj, "Containers") is IDictionary dict)
        {
            foreach (DictionaryEntry entry in dict)
            {
                var key = ExtractIdString(entry.Key);
                if (string.Equals(key, containerId, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
        }

        return false;
    }

    private static string? ExtractIdString(object? val)
    {
        if (val == null)
            return null;
        if (val is string s)
            return s;
        if (GetMemberValue(val, "Id") is string idStr)
            return idStr;
        return val.ToString();
    }

    private void ConvertEdgeSteps(
        ConstructionGraphEdge edge,
        string expectedNextNode,
        List<ConstructionActionStep> output)
    {
        var rawSteps = GetMemberValue(edge, "Steps") as IEnumerable ?? GetMemberValue(edge, "_steps") as IEnumerable;
        if (rawSteps == null)
            return;

        foreach (var stepObj in rawSteps)
        {
            if (stepObj != null)
                ConvertSingleOrNestedStep(stepObj, expectedNextNode, output);
        }
    }

    private void ConvertSingleOrNestedStep(
        object stepObj,
        string expectedNextNode,
        List<ConstructionActionStep> output)
    {
        var type = stepObj.GetType();
        var typeName = type.Name;

        // Handle NestedConstructionGraphStep (which stores nested step collections)
        if (typeName.Contains("NestedConstructionGraphStep", StringComparison.OrdinalIgnoreCase))
        {
            var nestedEnumerable = GetMemberValue(stepObj, "Steps") as IEnumerable
                                   ?? GetMemberValue(stepObj, "_steps") as IEnumerable;
            if (nestedEnumerable != null)
            {
                foreach (var sub in nestedEnumerable)
                {
                    if (sub == null)
                        continue;

                    if (sub is IEnumerable subList && sub is not string)
                    {
                        foreach (var innerStep in subList)
                        {
                            if (innerStep != null)
                                ConvertSingleOrNestedStep(innerStep, expectedNextNode, output);
                        }
                    }
                    else
                    {
                        ConvertSingleOrNestedStep(sub, expectedNextNode, output);
                    }
                }
            }
            return;
        }

        float doAfter = GetFloatMember(stepObj, "DoAfter", 0f);
        int amount = Math.Max(1, GetIntMember(stepObj, "Amount", 1));

        // 1. MaterialConstructionGraphStep
        if (typeName.Contains("MaterialConstructionGraphStep", StringComparison.OrdinalIgnoreCase))
        {
            var materialId = GetStringMember(stepObj, "MaterialPrototypeId")
                             ?? GetStringMember(stepObj, "Material")
                             ?? "Steel";
            output.Add(new ConstructionActionStep(
                ConstructionActionKind.AddMaterial,
                $"Add {amount}x {materialId}",
                PrototypeOrQualityOrTag: materialId,
                Amount: amount,
                DoAfterDelay: doAfter,
                ExpectedNextNode: expectedNextNode));
            return;
        }

        // 2. ToolConstructionGraphStep
        if (typeName.Contains("ToolConstructionGraphStep", StringComparison.OrdinalIgnoreCase))
        {
            var toolQuality = GetStringMember(stepObj, "Tool") ?? "Wrenching";
            output.Add(new ConstructionActionStep(
                ConstructionActionKind.UseTool,
                $"Use {toolQuality} tool",
                PrototypeOrQualityOrTag: toolQuality,
                Amount: 1,
                DoAfterDelay: doAfter,
                ExpectedNextNode: expectedNextNode));
            return;
        }

        // 3. PrototypeConstructionGraphStep / EntityInsertConstructionGraphStep
        if (typeName.Contains("PrototypeConstructionGraphStep", StringComparison.OrdinalIgnoreCase) ||
            (typeName.Contains("EntityInsertConstructionGraphStep", StringComparison.OrdinalIgnoreCase) &&
             !typeName.Contains("Component", StringComparison.OrdinalIgnoreCase) &&
             !typeName.Contains("Tag", StringComparison.OrdinalIgnoreCase)))
        {
            var protoId = GetStringMember(stepObj, "Prototype")
                          ?? GetStringMember(stepObj, "PrototypeId")
                          ?? string.Empty;
            output.Add(new ConstructionActionStep(
                ConstructionActionKind.InsertPrototype,
                $"Insert {amount}x {protoId}",
                PrototypeOrQualityOrTag: protoId,
                Amount: amount,
                DoAfterDelay: doAfter,
                ExpectedNextNode: expectedNextNode));
            return;
        }

        // 4. ComponentConstructionGraphStep
        if (typeName.Contains("ComponentConstructionGraphStep", StringComparison.OrdinalIgnoreCase))
        {
            var compName = GetStringMember(stepObj, "Component") ?? string.Empty;
            output.Add(new ConstructionActionStep(
                ConstructionActionKind.InsertComponent,
                $"Insert {amount}x [{compName}]",
                PrototypeOrQualityOrTag: compName,
                Amount: amount,
                DoAfterDelay: doAfter,
                ExpectedNextNode: expectedNextNode));
            return;
        }

        // 5. TagConstructionGraphStep / MultipleTagsConstructionGraphStep
        if (typeName.Contains("TagConstructionGraphStep", StringComparison.OrdinalIgnoreCase) ||
            typeName.Contains("MultipleTagsConstructionGraphStep", StringComparison.OrdinalIgnoreCase))
        {
            var tag = GetStringMember(stepObj, "Tag") ?? GetStringMember(stepObj, "_tag");
            if (string.IsNullOrEmpty(tag))
            {
                tag = FirstElementAsString(GetMemberValue(stepObj, "AllTags") ?? GetMemberValue(stepObj, "_allTags"))
                      ?? FirstElementAsString(GetMemberValue(stepObj, "AnyTags") ?? GetMemberValue(stepObj, "_anyTags"))
                      ?? string.Empty;
            }

            output.Add(new ConstructionActionStep(
                ConstructionActionKind.InsertTag,
                $"Insert {amount}x tag:{tag}",
                PrototypeOrQualityOrTag: tag,
                Amount: amount,
                DoAfterDelay: doAfter,
                ExpectedNextNode: expectedNextNode));
        }
    }

    private IEnumerable<ConstructionGraphNode> EnumerateGraphNodes(ConstructionGraphPrototype graph)
    {
        var nodesDict = GetMemberValue(graph, "Nodes") as IDictionary ?? GetMemberValue(graph, "_nodes") as IDictionary;
        if (nodesDict != null)
        {
            foreach (var val in nodesDict.Values)
            {
                if (val is ConstructionGraphNode node)
                    yield return node;
            }
            yield break;
        }

        if (GetMemberValue(graph, "_graph") is IEnumerable graphList)
        {
            foreach (var val in graphList)
            {
                if (val is ConstructionGraphNode node)
                    yield return node;
            }
        }
    }

    private bool TryGetNodeByName(ConstructionGraphPrototype graph, string nodeName, out ConstructionGraphNode node)
    {
        foreach (var n in EnumerateGraphNodes(graph))
        {
            if (string.Equals(GetNodeName(n), nodeName, StringComparison.OrdinalIgnoreCase))
            {
                node = n;
                return true;
            }
        }

        node = default!;
        return false;
    }

    private static string GetNodeName(ConstructionGraphNode node)
        => GetStringMember(node, "Name") ?? string.Empty;

    private static IReadOnlyList<ConstructionGraphEdge> GetNodeEdges(ConstructionGraphNode node)
    {
        var edgesObj = GetMemberValue(node, "Edges") ?? GetMemberValue(node, "_edges");
        if (edgesObj is IReadOnlyList<ConstructionGraphEdge> roList)
            return roList;

        if (edgesObj is IEnumerable enumerable)
        {
            var list = new List<ConstructionGraphEdge>();
            foreach (var item in enumerable)
            {
                if (item is ConstructionGraphEdge edge)
                    list.Add(edge);
            }
            return list;
        }

        return Array.Empty<ConstructionGraphEdge>();
    }

    private string? GetNodeEntityId(ConstructionGraphNode node, EntityUid? uid)
    {
        var entityObj = GetMemberValue(node, "Entity") ?? GetMemberValue(node, "_entity");
        if (entityObj == null)
            return null;

        if (entityObj is string str)
            return string.IsNullOrEmpty(str) ? null : str;

        var directId = GetStringMember(entityObj, "Id") ?? GetStringMember(entityObj, "Prototype");
        if (!string.IsNullOrEmpty(directId))
            return directId;

        var getIdMethod = entityObj.GetType().GetMethod("GetId", BindingFlags.Public | BindingFlags.Instance);
        if (getIdMethod != null)
        {
            try
            {
                var parameters = getIdMethod.GetParameters();
                var args = new object?[parameters.Length];
                for (int i = 0; i < parameters.Length; i++)
                {
                    var pType = parameters[i].ParameterType;
                    if (pType == typeof(EntityUid?) || pType == typeof(EntityUid))
                        args[i] = uid;
                    else if (pType == typeof(IEntityManager))
                        args[i] = _entMan;
                    else if (pType == typeof(IPrototypeManager))
                        args[i] = _protoMan;
                    else if (pType == typeof(IComponentFactory))
                        args[i] = _compFactory;
                    else if (pType.Name == "GraphNodeEntityArgs")
                        args[i] = CreateGraphNodeEntityArgs(pType, uid);
                    else
                        args[i] = null;
                }

                var res = getIdMethod.Invoke(entityObj, args)?.ToString();
                if (!string.IsNullOrEmpty(res))
                    return res;
            }
            catch
            {
                // Ignore if node entity requires server-only state
            }
        }

        return null;
    }

    private object? CreateGraphNodeEntityArgs(Type pType, EntityUid? uid)
    {
        foreach (var ctor in pType.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
        {
            var cParams = ctor.GetParameters();
            var cArgs = new object?[cParams.Length];
            bool ok = true;
            for (int i = 0; i < cParams.Length; i++)
            {
                var t = cParams[i].ParameterType;
                if (t == typeof(EntityUid?) || t == typeof(EntityUid))
                    cArgs[i] = uid ?? EntityUid.Invalid;
                else if (t == typeof(IEntityManager))
                    cArgs[i] = _entMan;
                else if (t == typeof(IPrototypeManager))
                    cArgs[i] = _protoMan;
                else if (t == typeof(IComponentFactory))
                    cArgs[i] = _compFactory;
                else if (t.IsValueType)
                    cArgs[i] = Activator.CreateInstance(t);
                else
                {
                    ok = false;
                    break;
                }
            }

            if (ok)
                return ctor.Invoke(cArgs);
        }

        return pType.IsValueType ? Activator.CreateInstance(pType) : null;
    }

    private IComponent? FindComponentByName(EntityUid uid, string componentTypeName)
    {
        foreach (var comp in _entMan.GetComponents(uid))
        {
            if (string.Equals(comp.GetType().Name, componentTypeName, StringComparison.OrdinalIgnoreCase))
                return comp;
        }
        return null;
    }

    private static object? GetMemberValue(object? obj, string name)
    {
        if (obj == null)
            return null;

        var type = obj.GetType();
        const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.IgnoreCase;

        var prop = type.GetProperty(name, flags);
        if (prop != null)
            return prop.GetValue(obj);

        var field = type.GetField(name, flags) ?? type.GetField("_" + name, flags);
        if (field != null)
            return field.GetValue(obj);

        return null;
    }

    private static string? GetStringMember(object? obj, string name)
    {
        var val = GetMemberValue(obj, name);
        if (val == null)
            return null;
        if (val is string s)
            return s;
        if (GetMemberValue(val, "Id") is string idStr)
            return idStr;
        return val.ToString();
    }

    private static int GetIntMember(object? obj, string name, int defaultValue = 0)
    {
        var val = GetMemberValue(obj, name);
        if (val == null)
            return defaultValue;
        if (val is int i)
            return i;
        try
        {
            return Convert.ToInt32(val);
        }
        catch
        {
            return defaultValue;
        }
    }

    private static float GetFloatMember(object? obj, string name, float defaultValue = 0f)
    {
        var val = GetMemberValue(obj, name);
        if (val == null)
            return defaultValue;
        if (val is float f)
            return f;
        if (val is double d)
            return (float)d;
        if (val is int i)
            return i;
        if (val is TimeSpan ts)
            return (float)ts.TotalSeconds;
        try
        {
            return Convert.ToSingle(val);
        }
        catch
        {
            return defaultValue;
        }
    }

    private static string? FirstElementAsString(object? collectionObj)
    {
        if (collectionObj is IEnumerable enumerable && collectionObj is not string)
        {
            foreach (var item in enumerable)
            {
                var s = ExtractIdString(item);
                if (!string.IsNullOrEmpty(s))
                    return s;
            }
        }
        return null;
    }
}