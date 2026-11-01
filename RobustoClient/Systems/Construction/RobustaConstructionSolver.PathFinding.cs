using Content.Shared.Construction;
using Content.Shared.Construction.Prototypes;

namespace RobustoClient.Systems.Construction;

public sealed partial class RobustaConstructionSolver
{
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
}
