using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
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
    string? ExpectedNextNode = null
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
        "BaseMachinePowered"
    };

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
    }

    public void EnsureCacheInitialized()
    {
        if (_cacheInitialized)
            return;

        _cacheInitialized = true;
        _entityToGraphNodes.Clear();
        _graphToConstructionProtos.Clear();
        _entityToConstructionProto.Clear();

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

        // Map EntityPrototypes that declare ConstructionComponent, Computer/Machine components, or are constructed from boards
        foreach (var entityProto in _protoMan.EnumeratePrototypes<EntityPrototype>())
        {
            if (TryGetConstructionMappingFromPrototype(entityProto, out var graphId, out var nodeName) &&
                TryGetGraph(graphId, out var graph) &&
                TryGetNodeByName(graph, nodeName, out var node))
            {
                RegisterEntityNodeMapping(entityProto.ID, graph, node);
            }

            if (machineGraph != null && machineNode != null)
            {
                if (_itemUtil.TryGetPrototypeComponent<MachineBoardComponent>(entityProto, "MachineBoard", out var machineBoard) &&
                    machineBoard != null)
                {
                    var machineProtoId = GetStringMember(machineBoard, "Prototype");
                    if (!string.IsNullOrEmpty(machineProtoId))
                        RegisterEntityNodeMapping(machineProtoId, machineGraph, machineNode);
                }

                if (!string.Equals(entityProto.ID, "MachineFrame", StringComparison.OrdinalIgnoreCase) &&
                    _itemUtil.HasPrototypeComponent(entityProto, "Machine"))
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
                return SolveBuildPlan(cProto, includeGhostStart: true);
        }

        // Case B: target is an in-world frame/unfinished structure
        if (!TryResolveGraphAndNode(target, out var graph, out var currentNode, out var explicitTargetNode) ||
            graph == null || currentNode == null)
        {
            return null;
        }

        var currentNodeName = GetNodeName(currentNode);
        var targetNodeName = explicitTargetNode;

        if (!string.IsNullOrEmpty(desiredEntityProtoId) &&
            _entityToGraphNodes.TryGetValue(desiredEntityProtoId, out var desiredNodes))
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

        if (string.IsNullOrEmpty(targetNodeName) ||
            string.Equals(targetNodeName, currentNodeName, StringComparison.OrdinalIgnoreCase))
        {
            targetNodeName = FindBestBuildTargetNode(graph, currentNode);
        }

        if (string.IsNullOrEmpty(targetNodeName) ||
            string.Equals(targetNodeName, currentNodeName, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var edgePath = FindShortestEdgePath(graph, currentNodeName, targetNodeName);
        if (edgePath == null || edgePath.Count == 0)
            return null;

        var steps = new List<ConstructionActionStep>();
        for (int i = 0; i < edgePath.Count; i++)
        {
            var (fromNode, edge, toNode) = edgePath[i];
            bool isFirstEdge = i == 0;
            AppendMachineFrameStepsIfNeeded(target, graph, fromNode, toNode, steps);
            AppendAnchorTransitionStepIfNeeded(target, isFirstEdge, graph, fromNode, edge, toNode, isDeconstruct: false, steps);
            ConvertEdgeSteps(edge, GetNodeName(toNode), steps);
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
    /// <param name="target">The entity whose construction graph should be traversed.</param>
    /// <param name="targetNodeName">The graph node to reach.</param>
    /// <returns>An ordered construction plan, or <see langword="null"/> when no path exists.</returns>
    public SolvedConstructionPlan? SolvePlanToNode(EntityUid target, string targetNodeName)
    {
        if (!_entMan.EntityExists(target) || string.IsNullOrWhiteSpace(targetNodeName))
            return null;

        EnsureCacheInitialized();
        if (!TryResolveGraphAndNode(target, out var graph, out var currentNode, out _) ||
            graph == null || currentNode == null)
            return null;

        var currentNodeName = GetNodeName(currentNode);
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

    public SolvedConstructionPlan? SolveBuildPlan(ConstructionPrototype constructionProto, bool includeGhostStart = true)
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
            AppendMachineFrameStepsIfNeeded(null, graph, fromNode, toNode, steps);
            AppendAnchorTransitionStepIfNeeded(null, isFirstEdge, graph, fromNode, edge, toNode, isDeconstruct: false, steps);
            ConvertEdgeSteps(edge, GetNodeName(toNode), steps);
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
            var panelComp = FindComponentByName(target, "WiresPanelComponent");
            if (panelComp != null &&
                GetMemberValue(panelComp, "Open") is false &&
                ShouldOpenWirePanelBeforeEdge(graph, currentNode, firstEdge, panelComp))
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

        // Walk EntityPrototype and its parents to see if any maps to a (Graph, Node) -> ConstructionPrototype
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var queue = new Queue<string>();
        queue.Enqueue(entityProtoId);
        visited.Add(entityProtoId);

        while (queue.Count > 0)
        {
            var curId = queue.Dequeue();
            if (_entityToConstructionProto.TryGetValue(curId, out var mappedId))
                return mappedId;

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

                    return cProtos[0].ID;
                }
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

        return null;
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

        // 3. Live-component fallback for Consoles (ComputerComponent) and Machines (MachineComponent)
        var protoIdStr = proto?.ID ?? string.Empty;
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
            FindComponentByName(target, "MachineComponent") != null &&
            TryFindMachineGraphAndNode(out var machineGraph, out var machineNode) &&
            machineGraph != null && machineNode != null)
        {
            graph = machineGraph;
            node = machineNode;
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
        if (!hasBoard && _container.TryGetContainer(target, "machine_board", out var mBoardCont) && mBoardCont.ContainedEntities.Count > 0)
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
            if (hasBoard || (spriteState != null && !spriteState.EndsWith("_0", StringComparison.OrdinalIgnoreCase)))
            {
                foreach (var candidate in candidates)
                {
                    if (string.Equals(GetNodeName(candidate.Node), "machineFrame", StringComparison.OrdinalIgnoreCase))
                        return candidate;
                }
            }
            else if (spriteState != null && spriteState.EndsWith("_0", StringComparison.OrdinalIgnoreCase))
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
                    name.Equals("machineFrame", StringComparison.OrdinalIgnoreCase) ||
                    name.Equals("girder", StringComparison.OrdinalIgnoreCase))
                {
                    return candidate;
                }
            }
        }

        return candidates[0];
    }

    private bool TryGetSpriteLayerZeroState(EntityUid uid, out string? stateName)
    {
        stateName = null;
        var spriteComp = FindComponentByName(uid, "SpriteComponent");
        if (spriteComp == null)
            return false;

        try
        {
            var layers = GetMemberValue(spriteComp, "AllLayers") as IEnumerable
                         ?? GetMemberValue(spriteComp, "_layers") as IEnumerable;
            if (layers != null)
            {
                foreach (var layer in layers)
                {
                    if (layer == null)
                        continue;

                    var stateObj = GetMemberValue(layer, "State");
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

        // 1. Check ConstructionPrototype recipes associated with this graph
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

            // Allow any reachable ConstructionPrototype target node if all were in NonTerminalBuildNodeNames
            foreach (var cProto in protos)
            {
                var tNode = GetStringMember(cProto, "TargetNode");
                if (string.IsNullOrEmpty(tNode) ||
                    string.Equals(tNode, currentName, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var path = FindShortestEdgePath(graph, currentName, tNode);
                if (path != null && path.Count > 0)
                    return tNode;
            }
        }

        // 2. Check well-known terminal node names on the graph (e.g. "machine", "computer", "wall", "airlock", "window")
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
                if (string.IsNullOrEmpty(edgeTargetName) || !visited.Add(edgeTargetName))
                    continue;

                if (!TryGetNodeByName(graph, edgeTargetName, out var nextNode))
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
            // Anchoring frames/assemblies/pipes that spawn unanchored (skipped automatically at runtime if already anchored)
            if (KnownAnchorableFrameNodes.Contains(fromName) ||
                IsPipeOrAtmosGraphOrNode(graph, fromNode, target))
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
        List<ConstructionActionStep> steps)
    {
        if (!string.Equals(graph.ID, "Machine", StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(GetNodeName(fromNode), "machineFrame", StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(GetNodeName(toNode), "machine", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        EntityUid? boardUid = null;
        if (target.HasValue &&
            _entMan.EntityExists(target.Value) &&
            _container.TryGetContainer(target.Value, "machine_board", out var boardContainer) &&
            boardContainer.ContainedEntities.Count > 0)
        {
            boardUid = boardContainer.ContainedEntities[0];
        }

        if (boardUid == null)
        {
            steps.Add(new ConstructionActionStep(
                ConstructionActionKind.InsertComponent,
                "Insert MachineBoard",
                PrototypeOrQualityOrTag: "MachineBoard",
                Amount: 1,
                DoAfterDelay: 0f,
                ExpectedNextNode: "machineFrame"));
            return;
        }

        var boardComp = FindComponentByName(boardUid.Value, "MachineBoardComponent");
        if (boardComp == null)
            return;

        if (GetMemberValue(boardComp, "StackRequirements") is IDictionary stackReqs)
        {
            foreach (DictionaryEntry entry in stackReqs)
            {
                var stackId = entry.Key?.ToString();
                var amount = entry.Value is int i ? i : 1;
                if (amount > 0 && !string.IsNullOrEmpty(stackId))
                {
                    steps.Add(new ConstructionActionStep(
                        ConstructionActionKind.AddMaterial,
                        $"Add {amount}x {stackId}",
                        PrototypeOrQualityOrTag: stackId,
                        Amount: amount,
                        DoAfterDelay: 0f,
                        ExpectedNextNode: "machineFrame"));
                }
            }
        }

        if (GetMemberValue(boardComp, "ComponentRequirements") is IDictionary compReqs)
        {
            foreach (DictionaryEntry entry in compReqs)
            {
                var compName = entry.Key?.ToString();
                var amt = GetIntMember(entry.Value, "Amount", 1);
                if (amt > 0 && !string.IsNullOrEmpty(compName))
                {
                    steps.Add(new ConstructionActionStep(
                        ConstructionActionKind.InsertComponent,
                        $"Insert {amt}x {compName}",
                        PrototypeOrQualityOrTag: compName,
                        Amount: amt,
                        DoAfterDelay: 0f,
                        ExpectedNextNode: "machineFrame"));
                }
            }
        }

        if (GetMemberValue(boardComp, "TagRequirements") is IDictionary tagReqs)
        {
            foreach (DictionaryEntry entry in tagReqs)
            {
                var tagId = entry.Key?.ToString();
                var amt = GetIntMember(entry.Value, "Amount", 1);
                if (amt > 0 && !string.IsNullOrEmpty(tagId))
                {
                    steps.Add(new ConstructionActionStep(
                        ConstructionActionKind.InsertTag,
                        $"Insert {amt}x [{tagId}]",
                        PrototypeOrQualityOrTag: tagId,
                        Amount: amt,
                        DoAfterDelay: 0f,
                        ExpectedNextNode: "machineFrame"));
                }
            }
        }
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

        // 3. PrototypeConstructionGraphStep
        if (typeName.Contains("PrototypeConstructionGraphStep", StringComparison.OrdinalIgnoreCase))
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
                var s = item?.ToString();
                if (!string.IsNullOrEmpty(s))
                    return s;
            }
        }
        return null;
    }
}