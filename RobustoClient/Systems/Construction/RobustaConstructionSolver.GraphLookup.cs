using Content.Shared.Construction;
using Content.Shared.Construction.Prototypes;

namespace RobustoClient.Systems.Construction;

public sealed partial class RobustaConstructionSolver
{
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
}
