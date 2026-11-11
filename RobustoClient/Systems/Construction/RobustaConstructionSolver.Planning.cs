using Content.Shared.Construction.Components;
using Content.Shared.Construction.Prototypes;

namespace RobustoClient.Systems.Construction;

public sealed partial class RobustaConstructionSolver
{
    public SolvedConstructionPlan? SolveBuildPlan(EntityUid target, string? desiredEntityProtoId = null)
    {
        if (_entMan == null || !_entMan.EntityExists(target))
            return null;

        EnsureCacheInitialized();
        var snapshot = CreateTargetSnapshot(target, desiredEntityProtoId);
        return SolveBuildPlan(in snapshot, desiredEntityProtoId);
    }

    public SolvedConstructionPlan? SolveBuildPlan(
        in ConstructionTargetSnapshot snapshot,
        string? desiredEntityProtoId = null)
    {
        EnsureCacheInitialized();

        if (!string.IsNullOrEmpty(snapshot.GhostRecipeId) &&
            _recipesById.TryGetValue(snapshot.GhostRecipeId, out var ghostRecipe))
        {
            return SolveBuildPlan(
                ghostRecipe,
                includeGhostStart: true,
                desiredEntityProtoId: desiredEntityProtoId,
                machineBoard: snapshot.MachineBoard);
        }

        if (!TryResolveGraphAndNode(in snapshot, out var graph, out var currentNode, out var explicitTargetNode) ||
            graph == null || currentNode == null)
        {
            return null;
        }

        var currentNodeName = currentNode.Name;
        var targetNodeName = explicitTargetNode;
        int desiredPipeLayer = GetDesiredPipeLayer(desiredEntityProtoId, null);

        if (!string.IsNullOrEmpty(desiredEntityProtoId))
        {
            if (_entityToGraphNodes.TryGetValue(desiredEntityProtoId, out var desiredNodes))
            {
                foreach (var (dGraph, dNode) in desiredNodes)
                {
                    if (string.Equals(dGraph.Id, graph.Id, StringComparison.OrdinalIgnoreCase))
                    {
                        targetNodeName = dNode.Name;
                        break;
                    }
                }
            }
            else if (TryExtractPipeLayerSuffix(desiredEntityProtoId, out var baseDesiredId, out _) &&
                     _entityToGraphNodes.TryGetValue(baseDesiredId, out var baseNodes))
            {
                foreach (var (dGraph, dNode) in baseNodes)
                {
                    if (string.Equals(dGraph.Id, graph.Id, StringComparison.OrdinalIgnoreCase))
                    {
                        targetNodeName = dNode.Name;
                        break;
                    }
                }
            }
        }

        if (string.IsNullOrEmpty(targetNodeName) ||
            string.Equals(targetNodeName, currentNodeName, StringComparison.OrdinalIgnoreCase))
        {
            if (IsPipeOrAtmosGraphOrNode(graph, currentNode, snapshot.PrototypeId))
            {
                int curLayer = snapshot.PipeLayer;
                bool isAnchored = snapshot.IsAnchored;
                bool canScrewdriver = ShouldUseScrewdriverForPipeLayer(graph, currentNode, desiredEntityProtoId);
                if (!isAnchored || (canScrewdriver && desiredPipeLayer != curLayer && !string.IsNullOrEmpty(desiredEntityProtoId)))
                {
                    var layerSteps = new List<ConstructionActionStep>();
                    if (canScrewdriver && !string.IsNullOrEmpty(desiredEntityProtoId) && desiredPipeLayer != curLayer)
                        AppendPipeLayerScrewdriverSteps(curLayer, desiredPipeLayer, layerSteps);
                    if (!isAnchored)
                        AppendPostBuildPipeAnchorStepIfNeeded(in snapshot, graph, currentNode, layerSteps);

                    if (layerSteps.Count > 0)
                    {
                        TryExtractPipeLayerSuffix(currentNodeName, out var baseCurNode, out _);
                        var finalNodeLabel = desiredPipeLayer > 0 ? $"{baseCurNode}Alt{desiredPipeLayer}" : baseCurNode;
                        return new SolvedConstructionPlan(
                            graph.Id,
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
        IndexedGraphNode finalBuiltNode = currentNode;
        bool needPostBuildPipeAnchor = false;

        if (edgePath != null && edgePath.Count > 0)
        {
            for (int i = 0; i < edgePath.Count; i++)
            {
                var (fromNode, edge, toNode) = edgePath[i];
                bool isFirstEdge = i == 0;
                AppendAnchorTransitionStepIfNeeded(in snapshot, isFirstEdge, graph, fromNode, edge, toNode, isDeconstruct: false, steps);
                AppendMachineFrameStepsIfNeeded(in snapshot, graph, fromNode, toNode, steps);
                AppendEdgeSteps(edge, toNode.Name, steps);
            }
            finalBuiltNode = edgePath[^1].To;
            needPostBuildPipeAnchor = true;
        }
        else
        {
            if (!IsPipeOrAtmosGraphOrNode(graph, currentNode, snapshot.PrototypeId))
                return null;

            TryExtractPipeLayerSuffix(currentNodeName, out var baseCurNode, out _);
            TryExtractPipeLayerSuffix(targetNodeName, out var baseTargetNode, out _);
            if (!string.Equals(baseCurNode, baseTargetNode, StringComparison.OrdinalIgnoreCase))
                return null;

            bool isTargetAnchored = snapshot.IsAnchored;
            bool canScrewdriver = ShouldUseScrewdriverForPipeLayer(graph, currentNode, desiredEntityProtoId);
            if (!canScrewdriver && isTargetAnchored)
                return null;

            needPostBuildPipeAnchor = !isTargetAnchored;
        }

        if (ShouldUseScrewdriverForPipeLayer(graph, finalBuiltNode, desiredEntityProtoId) &&
            !string.IsNullOrEmpty(desiredEntityProtoId))
        {
            int builtLayer = (edgePath == null || edgePath.Count == 0)
                ? snapshot.PipeLayer
                : GetDeclaredPipeLayer(finalBuiltNode.EntityId);

            if (desiredPipeLayer != builtLayer)
            {
                AppendPipeLayerScrewdriverSteps(builtLayer, desiredPipeLayer, steps);
                TryExtractPipeLayerSuffix(finalBuiltNode.Name, out var baseBuiltNode, out _);
                targetNodeName = desiredPipeLayer > 0 ? $"{baseBuiltNode}Alt{desiredPipeLayer}" : baseBuiltNode;
            }
        }

        if (needPostBuildPipeAnchor)
            AppendPostBuildPipeAnchorStepIfNeeded(in snapshot, graph, finalBuiltNode, steps);

        if (steps.Count == 0)
            return null;

        return new SolvedConstructionPlan(
            graph.Id,
            currentNodeName,
            targetNodeName,
            IsDeconstruct: false,
            Steps: steps);
    }

    public SolvedConstructionPlan? SolvePlanToNode(
        EntityUid target,
        string targetNodeName,
        string? startNodeOverride = null)
    {
        if (_entMan == null || !_entMan.EntityExists(target) || string.IsNullOrWhiteSpace(targetNodeName))
            return null;

        EnsureCacheInitialized();
        var snapshot = CreateTargetSnapshot(target, null);
        return SolvePlanToNode(in snapshot, targetNodeName, startNodeOverride);
    }

    public SolvedConstructionPlan? SolvePlanToNode(
        in ConstructionTargetSnapshot snapshot,
        string targetNodeName,
        string? startNodeOverride = null)
    {
        if (string.IsNullOrWhiteSpace(targetNodeName))
            return null;

        EnsureCacheInitialized();
        if (!TryResolveGraphAndNode(in snapshot, out var graph, out var currentNode, out _) ||
            graph == null || currentNode == null)
        {
            return null;
        }

        if (!string.IsNullOrWhiteSpace(startNodeOverride) &&
            TryGetNodeByName(graph, startNodeOverride, out var overrideNode))
        {
            currentNode = overrideNode;
        }

        var currentNodeName = currentNode.Name;
        if (string.Equals(currentNodeName, targetNodeName, StringComparison.OrdinalIgnoreCase))
            return new SolvedConstructionPlan(graph.Id, currentNodeName, targetNodeName, false, []);

        var edgePath = FindShortestEdgePath(graph, currentNodeName, targetNodeName);
        if (edgePath == null || edgePath.Count == 0)
            return null;

        var steps = new List<ConstructionActionStep>();
        for (var i = 0; i < edgePath.Count; i++)
        {
            var (fromNode, edge, toNode) = edgePath[i];
            AppendAnchorTransitionStepIfNeeded(
                in snapshot,
                i == 0,
                graph,
                fromNode,
                edge,
                toNode,
                isDeconstruct: false,
                steps);
            AppendEdgeSteps(edge, toNode.Name, steps);
        }

        return new SolvedConstructionPlan(graph.Id, currentNodeName, targetNodeName, false, steps);
    }

    public SolvedConstructionPlan? SolveBuildPlan(
        ConstructionPrototype constructionProto,
        bool includeGhostStart = true,
        string? desiredEntityProtoId = null)
    {
        EnsureCacheInitialized();
        var recipe = BuildIndexedRecipe(constructionProto);
        if (recipe == null)
            return null;

        var machineBoard = ExtractMachineBoardRequirements(null, desiredEntityProtoId, alreadyHasBoard: false);
        return SolveBuildPlan(recipe, includeGhostStart, desiredEntityProtoId, machineBoard);
    }

    public SolvedConstructionPlan? SolveBuildPlan(
        IndexedConstructionRecipe recipe,
        bool includeGhostStart = true,
        string? desiredEntityProtoId = null,
        MachineBoardRequirements? machineBoard = null)
    {
        EnsureCacheInitialized();

        var graphId = recipe.GraphId;
        var targetNodeName = recipe.TargetNode;
        var startNodeName = recipe.StartNode;

        if (string.IsNullOrEmpty(graphId) ||
            string.IsNullOrEmpty(targetNodeName) ||
            !TryGetGraph(graphId, out var graph))
        {
            return null;
        }

        if (string.Equals(graph.Id, "Machine", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(targetNodeName, "machine", StringComparison.OrdinalIgnoreCase) &&
            TryGetNodeByName(graph, "machine", out _) &&
            (!string.IsNullOrEmpty(desiredEntityProtoId) || machineBoard != null || TryFindAvailableMachineBoard(null, null, out _, out _)))
        {
            targetNodeName = "machine";
        }

        if (string.IsNullOrEmpty(startNodeName))
            startNodeName = graph.StartNode ?? "start";

        if (!TryGetNodeByName(graph, startNodeName, out _))
            startNodeName = "start";

        var edgePath = FindShortestEdgePath(graph, startNodeName, targetNodeName);
        if (edgePath == null || edgePath.Count == 0)
            return null;

        var steps = new List<ConstructionActionStep>();
        if (includeGhostStart)
        {
            var firstNodeAfterGhost = edgePath[0].To.Name;
            float initialDoAfter = SumEdgeDoAfter(edgePath[0].Edge);
            steps.Add(new ConstructionActionStep(
                ConstructionActionKind.StartGhostConstruction,
                $"Start construction ({recipe.Id})",
                PrototypeOrQualityOrTag: recipe.Id,
                Amount: 1,
                DoAfterDelay: initialDoAfter,
                ExpectedNextNode: firstNodeAfterGhost));
        }

        var snapshot = new ConstructionTargetSnapshot(
            PrototypeId: desiredEntityProtoId,
            GraphId: graph.Id,
            CurrentNode: startNodeName,
            IsAnchored: false,
            MachineBoard: machineBoard);

        for (int i = 0; i < edgePath.Count; i++)
        {
            var (fromNode, edge, toNode) = edgePath[i];
            bool isFirstEdge = i == 0;
            AppendAnchorTransitionStepIfNeeded(in snapshot, isFirstEdge, graph, fromNode, edge, toNode, isDeconstruct: false, steps);
            AppendMachineFrameStepsIfNeeded(in snapshot, graph, fromNode, toNode, steps);
            AppendEdgeSteps(edge, toNode.Name, steps);
        }

        var finalNode = edgePath[^1].To;

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

        AppendPostBuildPipeAnchorStepIfNeeded(in snapshot, graph, finalNode, steps);

        return new SolvedConstructionPlan(
            graph.Id,
            startNodeName,
            targetNodeName,
            IsDeconstruct: false,
            Steps: steps);
    }

    public SolvedConstructionPlan? SolveDeconstructPlan(EntityUid target)
    {
        if (_entMan == null || !_entMan.EntityExists(target))
            return null;

        EnsureCacheInitialized();
        var snapshot = CreateTargetSnapshot(target, null);
        return SolveDeconstructPlan(in snapshot);
    }

    public SolvedConstructionPlan? SolveDeconstructPlan(in ConstructionTargetSnapshot snapshot)
    {
        EnsureCacheInitialized();

        if (!TryResolveGraphAndNode(in snapshot, out var graph, out var currentNode, out _) ||
            graph == null || currentNode == null)
        {
            return null;
        }

        var currentNodeName = currentNode.Name;
        if (string.IsNullOrEmpty(currentNodeName))
            return null;

        var candidateTargets = GetDeconstructCandidateNodes(in snapshot, graph, currentNodeName);
        foreach (var targetNodeName in candidateTargets)
        {
            if (string.Equals(targetNodeName, currentNodeName, StringComparison.OrdinalIgnoreCase))
                continue;

            var edgePath = FindShortestEdgePath(graph, currentNodeName, targetNodeName);
            if (edgePath == null || edgePath.Count == 0)
                continue;

            var steps = new List<ConstructionActionStep>();
            var firstEdge = edgePath[0].Edge;

            if (ShouldWeldDoorBeforeDeconstruct(in snapshot, graph, currentNode, firstEdge))
            {
                steps.Add(new ConstructionActionStep(
                    ConstructionActionKind.UseTool,
                    "Weld door shut (Welding)",
                    PrototypeOrQualityOrTag: "Welding",
                    Amount: 1,
                    DoAfterDelay: 1f,
                    ExpectedNextNode: null));
            }

            if (ShouldOpenWirePanelBeforeEdge(graph, currentNode, firstEdge, in snapshot) && !snapshot.IsWirePanelOpen)
            {
                var openTool = string.IsNullOrEmpty(snapshot.WirePanelOpeningTool) ? "Screwing" : snapshot.WirePanelOpeningTool;
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
                if (ShouldCutInternalWiresBeforeEdge(in snapshot, graph, fromNode, edge))
                {
                    steps.Add(new ConstructionActionStep(
                        ConstructionActionKind.CutWires,
                        "Cut internal wires (Cutting)",
                        PrototypeOrQualityOrTag: "Cutting",
                        Amount: 1,
                        DoAfterDelay: 0.5f,
                        ExpectedNextNode: null));
                }

                if (string.Equals(graph.Id, "Machine", StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(fromNode.Name, "machineFrame", StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(toNode.Name, "missingWires", StringComparison.OrdinalIgnoreCase) &&
                    (!isFirstEdge || snapshot.HasMachineFrameBoard))
                {
                    steps.Add(new ConstructionActionStep(
                        ConstructionActionKind.UseTool,
                        "Pry out board and parts (Prying)",
                        PrototypeOrQualityOrTag: "Prying",
                        Amount: 1,
                        DoAfterDelay: 0f,
                        ExpectedNextNode: null));
                }

                AppendAnchorTransitionStepIfNeeded(in snapshot, isFirstEdge, graph, fromNode, edge, toNode, isDeconstruct: true, steps);
                AppendEdgeSteps(edge, toNode.Name, steps);
            }

            if (steps.Count == 0)
                continue;

            return new SolvedConstructionPlan(
                graph.Id,
                currentNodeName,
                targetNodeName,
                IsDeconstruct: true,
                Steps: steps);
        }

        return null;
    }

    private ConstructionTargetSnapshot CreateTargetSnapshot(EntityUid target, string? desiredEntityProtoId)
    {
        string? ghostRecipeId = null;
        var ghostComp = FindComponentByName(target, "ConstructionGhostComponent");
        if (ghostComp != null)
        {
            var cProto = GetMemberValue(ghostComp, "Prototype") as ConstructionPrototype;
            ghostRecipeId = cProto?.ID ?? GetStringMember(ghostComp, "Prototype");
            if (cProto != null && !string.IsNullOrEmpty(ghostRecipeId) && !_recipesById.ContainsKey(ghostRecipeId))
            {
                if (BuildIndexedRecipe(cProto) is { } indexed)
                    RegisterIndexedRecipe(indexed);
            }
        }

        TryResolveGraphAndNode(target, out var graph, out var node, out var explicitTargetNode);

        var protoId = _entMan.GetComponent<MetaDataComponent>(target).EntityPrototype?.ID;
        bool isAnchored = _entMan.GetComponent<TransformComponent>(target).Anchored;
        int pipeLayer = DetectEntityPipeLayer(target, out _);

        string? deconstructionNode = null;
        var constructionComp = FindComponentByName(target, "ConstructionComponent");
        if (constructionComp != null)
            deconstructionNode = GetStringMember(constructionComp, "DeconstructionNode");

        bool? isDoorWelded = null;
        var weldableComp = FindComponentByName(target, "WeldableComponent");
        if (weldableComp != null)
            isDoorWelded = GetMemberValue(weldableComp, "IsWelded") is true;

        var panelComp = FindComponentByName(target, "WiresPanelComponent");
        bool hasWirePanel = panelComp != null;
        bool isWirePanelOpen = panelComp == null || GetMemberValue(panelComp, "Open") is not false;
        var wirePanelOpeningTool = (panelComp != null ? GetStringMember(panelComp, "OpeningTool") : null) ?? "Screwing";
        bool hasWiresUi = hasWirePanel && HasWiresUserInterface(target);

        var secComp = FindComponentByName(target, "WiresPanelSecurityComponent");
        bool isSecurityWiresAccessible = secComp == null || GetMemberValue(secComp, "WiresAccessible") is not false;
        var securityExamine = secComp != null ? GetStringMember(secComp, "Examine") : null;
        if (!isSecurityWiresAccessible && string.IsNullOrEmpty(securityExamine))
            securityExamine = ResolveCurrentSecurityNode(target, secComp, graph);

        bool hasMachineFrameBoard = HasMachineFrameBoard(target);
        MachineBoardRequirements? machineBoard = null;
        if (string.Equals(graph?.Id, "Machine", StringComparison.OrdinalIgnoreCase) ||
            !string.IsNullOrEmpty(ghostRecipeId))
        {
            machineBoard = ExtractMachineBoardRequirements(target, desiredEntityProtoId, hasMachineFrameBoard);
        }

        return new ConstructionTargetSnapshot(
            PrototypeId: protoId,
            GraphId: graph?.Id,
            CurrentNode: node?.Name,
            ExplicitTargetNode: explicitTargetNode,
            DeconstructionNode: deconstructionNode,
            GhostRecipeId: ghostRecipeId,
            IsAnchored: isAnchored,
            PipeLayer: pipeLayer,
            IsDoorWelded: isDoorWelded,
            HasWirePanel: hasWirePanel,
            IsWirePanelOpen: isWirePanelOpen,
            WirePanelOpeningTool: wirePanelOpeningTool,
            HasWiresUi: hasWiresUi,
            IsSecurityWiresAccessible: isSecurityWiresAccessible,
            SecurityExamine: securityExamine,
            HasMachineFrameBoard: hasMachineFrameBoard,
            MachineBoard: machineBoard);
    }
}
