namespace RobustoClient.Systems.Construction;

public sealed partial class RobustaConstructionSolver
{
    private void AppendAnchorTransitionStepIfNeeded(
        in ConstructionTargetSnapshot snapshot,
        bool isFirstEdge,
        IndexedConstructionGraph graph,
        IndexedGraphNode fromNode,
        IndexedGraphEdge edge,
        IndexedGraphNode toNode,
        bool isDeconstruct,
        List<ConstructionActionStep> steps)
    {
        var fromName = fromNode.Name;

        if (EdgeContainsToolStep(edge, "Anchoring"))
            return;

        if (steps.Count > 0 &&
            steps[^1].Kind == ConstructionActionKind.UseTool &&
            string.Equals(steps[^1].PrototypeOrQualityOrTag, "Anchoring", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (!isDeconstruct && string.Equals(fromName, "start", StringComparison.OrdinalIgnoreCase))
            return;

        if (edge.RequiredAnchored is { } requiredAnchored)
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

        if (isDeconstruct)
        {
            if (IsTerminalDeconstructNode(graph, toNode) &&
                NeedsUnanchorBeforeDeconstruct(snapshot, isFirstEdge, graph, fromNode, edge))
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
        else if (KnownAnchorableFrameNodes.Contains(fromName))
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

    private void AppendPostBuildPipeAnchorStepIfNeeded(
        in ConstructionTargetSnapshot snapshot,
        IndexedConstructionGraph graph,
        IndexedGraphNode finalNode,
        List<ConstructionActionStep> steps)
    {
        if (!IsPipeOrAtmosGraphOrNode(graph, finalNode, snapshot.PrototypeId))
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

    private bool NeedsUnanchorBeforeDeconstruct(
        in ConstructionTargetSnapshot snapshot,
        bool isFirstEdge,
        IndexedConstructionGraph graph,
        IndexedGraphNode fromNode,
        IndexedGraphEdge edge)
    {
        var fromName = fromNode.Name;

        if ((string.Equals(graph.Id, "Machine", StringComparison.OrdinalIgnoreCase) &&
             string.Equals(fromName, "missingWires", StringComparison.OrdinalIgnoreCase)) ||
            (string.Equals(graph.Id, "Computer", StringComparison.OrdinalIgnoreCase) &&
             string.Equals(fromName, "frameUnsecured", StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        if (string.Equals(graph.Id, "Grille", StringComparison.OrdinalIgnoreCase) ||
            fromName.Contains("grille", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (KnownAnchorableFrameNodes.Contains(fromName))
            return true;

        if (IsPipeOrAtmosGraphOrNode(graph, fromNode, snapshot.PrototypeId))
            return true;

        if (fromNode.IsAnchorable &&
            (EdgeContainsToolStep(edge, "Welding") ||
             EdgeContainsToolStep(edge, "Screwing") ||
             EdgeContainsToolStep(edge, "Prying")))
        {
            return true;
        }

        return false;
    }

    private static bool IsPipeOrAtmosGraphOrNode(
        IndexedConstructionGraph graph,
        IndexedGraphNode node,
        string? targetProtoId)
    {
        var graphId = graph.Id ?? string.Empty;
        var nodeName = node.Name;
        var nodeEntId = node.EntityId ?? string.Empty;
        var protoId = targetProtoId ?? string.Empty;

        foreach (var kw in PipeAndAtmosKeywords)
        {
            if (graphId.Contains(kw, StringComparison.OrdinalIgnoreCase) ||
                nodeName.Contains(kw, StringComparison.OrdinalIgnoreCase) ||
                nodeEntId.Contains(kw, StringComparison.OrdinalIgnoreCase) ||
                protoId.Contains(kw, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
