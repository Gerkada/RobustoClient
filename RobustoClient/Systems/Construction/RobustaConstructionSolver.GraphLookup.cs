namespace RobustoClient.Systems.Construction;

public sealed partial class RobustaConstructionSolver
{
    private bool TryGetGraph(string graphId, out IndexedConstructionGraph graph)
    {
        if (_indexedGraphs.TryGetValue(graphId, out graph!))
            return true;

        graph = default!;
        return false;
    }

    private static bool TryGetNodeByName(
        IndexedConstructionGraph graph,
        string nodeName,
        out IndexedGraphNode node)
    {
        if (graph.Nodes.TryGetValue(nodeName, out node!))
            return true;

        foreach (var n in graph.Nodes.Values)
        {
            if (string.Equals(n.Name, nodeName, StringComparison.OrdinalIgnoreCase))
            {
                node = n;
                return true;
            }
        }

        node = default!;
        return false;
    }

    private bool TryFindComputerGraphAndNode(
        out IndexedConstructionGraph? compGraph,
        out IndexedGraphNode? compNode)
    {
        compGraph = null;
        compNode = null;

        if (!TryGetGraph("Computer", out var g))
        {
            foreach (var candidate in _indexedGraphs.Values)
            {
                foreach (var n in candidate.Nodes.Values)
                {
                    if (string.Equals(n.EntityId, "ComputerFrame", StringComparison.OrdinalIgnoreCase))
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

        foreach (var n in g.Nodes.Values)
        {
            if (n.IsBoardNodeEntity)
            {
                compNode = n;
                return true;
            }
        }

        return false;
    }

    private bool TryFindMachineGraphAndNode(
        out IndexedConstructionGraph? machineGraph,
        out IndexedGraphNode? machineNode)
    {
        machineGraph = null;
        machineNode = null;

        if (!TryGetGraph("Machine", out var g))
        {
            foreach (var candidate in _indexedGraphs.Values)
            {
                foreach (var n in candidate.Nodes.Values)
                {
                    if (string.Equals(n.EntityId, "MachineFrame", StringComparison.OrdinalIgnoreCase))
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

        foreach (var n in g.Nodes.Values)
        {
            if (n.IsBoardNodeEntity)
            {
                machineNode = n;
                return true;
            }
        }

        return false;
    }

    private bool TryFindAirlockGraphAndNode(
        out IndexedConstructionGraph? airlockGraph,
        out IndexedGraphNode? airlockNode)
    {
        airlockGraph = null;
        airlockNode = null;

        if (!TryGetGraph("Airlock", out var g))
        {
            foreach (var candidate in _indexedGraphs.Values)
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

    private static string? GetInferredFramePrototypeForNode(string graphId, string nodeName)
    {
        if (string.Equals(graphId, "Computer", StringComparison.OrdinalIgnoreCase) &&
            (string.Equals(nodeName, "frameUnsecured", StringComparison.OrdinalIgnoreCase) ||
             string.Equals(nodeName, "boardUnsecured", StringComparison.OrdinalIgnoreCase) ||
             string.Equals(nodeName, "missingWires", StringComparison.OrdinalIgnoreCase) ||
             string.Equals(nodeName, "monitorMissing", StringComparison.OrdinalIgnoreCase) ||
             string.Equals(nodeName, "monitorUnsecured", StringComparison.OrdinalIgnoreCase)))
        {
            return "ComputerFrame";
        }

        if (string.Equals(graphId, "Machine", StringComparison.OrdinalIgnoreCase) &&
            (string.Equals(nodeName, "machineFrame", StringComparison.OrdinalIgnoreCase) ||
             string.Equals(nodeName, "missingWires", StringComparison.OrdinalIgnoreCase)))
        {
            return "MachineFrame";
        }

        if (string.Equals(graphId, "Airlock", StringComparison.OrdinalIgnoreCase) &&
            (string.Equals(nodeName, "assembly", StringComparison.OrdinalIgnoreCase) ||
             string.Equals(nodeName, "wired", StringComparison.OrdinalIgnoreCase) ||
             string.Equals(nodeName, "electronics", StringComparison.OrdinalIgnoreCase)))
        {
            return "AirlockAssembly";
        }

        return null;
    }
}
