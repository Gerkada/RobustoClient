using Content.Shared.Construction;
using Content.Shared.Construction.Components;
using Content.Shared.Construction.Prototypes;
using Robust.Shared.Prototypes;

namespace RobustoClient.Systems.Construction;

public sealed partial class RobustaConstructionSolver
{
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
}
