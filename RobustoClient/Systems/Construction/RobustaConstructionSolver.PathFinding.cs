namespace RobustoClient.Systems.Construction;

public sealed partial class RobustaConstructionSolver
{
    public bool DoesEdgeReplaceOrDeleteEntity(EntityUid currentTarget, string graphId, string? expectedNextNode)
    {
        if (string.IsNullOrEmpty(expectedNextNode) || _entMan == null || !_entMan.EntityExists(currentTarget))
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

        if (string.Equals(graph.Id, "Airlock", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(currentProtoId, "AirlockAssembly", StringComparison.OrdinalIgnoreCase))
        {
            var toNodeName = toNode.Name;
            if (string.Equals(toNodeName, "airlock", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(toNodeName, "glassAirlock", StringComparison.OrdinalIgnoreCase) ||
                toNodeName.Contains("Security", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        if (toNode.IsBoardNodeEntity)
            return true;

        var nextProtoId = toNode.EntityId
                          ?? GetInferredFramePrototypeForNode(graph.Id, toNode.Name);

        if (!string.IsNullOrEmpty(nextProtoId) &&
            !string.IsNullOrEmpty(currentProtoId) &&
            !string.Equals(currentProtoId, nextProtoId, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return false;
    }

    private string? FindBestBuildTargetNode(IndexedConstructionGraph graph, IndexedGraphNode currentNode)
    {
        var currentName = currentNode.Name;

        if (_graphToConstructionProtos.TryGetValue(graph.Id, out var protos))
        {
            string? bestTarget = null;
            int bestDist = int.MaxValue;

            foreach (var recipe in protos)
            {
                var tNode = recipe.TargetNode;
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

        foreach (var node in graph.Nodes.Values)
        {
            var nodeName = node.Name;
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
        in ConstructionTargetSnapshot snapshot,
        IndexedConstructionGraph graph,
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

        TryAdd(snapshot.DeconstructionNode);
        TryAdd(graph.StartNode);

        foreach (var primary in PrimaryDeconstructTargets)
            TryAdd(primary);

        foreach (var n in graph.Nodes.Values)
        {
            if (string.IsNullOrEmpty(n.EntityId) && n.Edges.Count == 0)
                TryAdd(n.Name);
        }

        foreach (var fallback in FallbackDeconstructTargets)
            TryAdd(fallback);

        return result;
    }

    private List<(IndexedGraphNode From, IndexedGraphEdge Edge, IndexedGraphNode To)>? FindShortestEdgePath(
        IndexedConstructionGraph graph,
        string startNodeName,
        string targetNodeName)
    {
        if (!TryGetNodeByName(graph, startNodeName, out var startNode) ||
            !TryGetNodeByName(graph, targetNodeName, out var targetNode))
        {
            return null;
        }

        var queue = new Queue<IndexedGraphNode>();
        var cameFrom = new Dictionary<string, (IndexedGraphNode From, IndexedGraphEdge Edge)>(StringComparer.OrdinalIgnoreCase);
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var startName = startNode.Name;
        queue.Enqueue(startNode);
        visited.Add(startName);

        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            var currentName = current.Name;

            if (string.Equals(currentName, targetNodeName, StringComparison.OrdinalIgnoreCase))
            {
                var path = new List<(IndexedGraphNode From, IndexedGraphEdge Edge, IndexedGraphNode To)>();
                var traceNode = targetNode;
                var traceName = traceNode.Name;

                while (!string.Equals(traceName, startName, StringComparison.OrdinalIgnoreCase))
                {
                    if (!cameFrom.TryGetValue(traceName, out var prev))
                        return null;

                    path.Add((prev.From, prev.Edge, traceNode));
                    traceNode = prev.From;
                    traceName = traceNode.Name;
                }

                path.Reverse();
                return path;
            }

            foreach (var edge in current.Edges)
            {
                var edgeTargetName = edge.TargetNode;
                if (string.IsNullOrEmpty(edgeTargetName))
                    continue;

                if (!TryGetNodeByName(graph, edgeTargetName, out var nextNode))
                    continue;

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

    private static float SumEdgeDoAfter(IndexedGraphEdge edge)
    {
        float total = 0f;
        foreach (var s in edge.Steps)
            total += s.DoAfterDelay;
        return total;
    }

    private static bool IsTerminalDeconstructNode(IndexedConstructionGraph graph, IndexedGraphNode toNode)
    {
        var toName = toNode.Name;
        if (string.IsNullOrEmpty(toName))
            return false;

        if (string.Equals(toName, "start", StringComparison.OrdinalIgnoreCase))
            return true;

        if (!string.IsNullOrEmpty(graph.StartNode) &&
            string.Equals(toName, graph.StartNode, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (PrimaryDeconstructTargets.Contains(toName, StringComparer.OrdinalIgnoreCase))
            return true;

        return string.IsNullOrEmpty(toNode.EntityId) && toNode.Edges.Count == 0;
    }
}
