using Content.Shared.Construction;
using Content.Shared.Construction.Prototypes;

namespace RobustoClient.Systems.Construction;

public sealed partial class RobustaConstructionSolver
{
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
            int builtLayer = (edgePath == null || edgePath.Count == 0)
                ? DetectEntityPipeLayer(target, out _)
                : GetDeclaredPipeLayer(GetNodeEntityId(finalBuiltNode, target));

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
}
