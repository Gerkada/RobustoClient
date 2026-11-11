using System.Collections;
using Content.Shared.Construction;

namespace RobustoClient.Systems.Construction;

public sealed partial class RobustaConstructionSolver
{
    private static bool TryGetRawEdgeAnchoredCondition(ConstructionGraphEdge edge, out bool requiredAnchored)
    {
        requiredAnchored = false;
        foreach (var cond in EnumerateRawEdgeConditions(edge))
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

    private static bool TryGetRawEdgeWirePanelCondition(ConstructionGraphEdge edge, out bool requiredOpen)
    {
        requiredOpen = true;
        foreach (var cond in EnumerateRawEdgeConditions(edge))
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

    private static bool TryGetRawEdgeDoorWeldedCondition(ConstructionGraphEdge edge, out bool requiredWelded)
    {
        requiredWelded = true;
        foreach (var cond in EnumerateRawEdgeConditions(edge))
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

    private static bool TryGetRawEdgeAllWiresCutCondition(ConstructionGraphEdge edge, out bool requiredAllCut)
    {
        requiredAllCut = true;
        foreach (var cond in EnumerateRawEdgeConditions(edge))
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

    private static IEnumerable<object> EnumerateRawEdgeConditions(ConstructionGraphEdge edge)
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

    private static bool FirstEdgeStepIsTool(IndexedGraphEdge edge, string toolQuality)
    {
        return edge.Steps.Count > 0 &&
               edge.Steps[0].Kind == ConstructionActionKind.UseTool &&
               string.Equals(edge.Steps[0].PrototypeOrQualityOrTag, toolQuality, StringComparison.OrdinalIgnoreCase);
    }

    private static bool EdgeContainsToolStep(IndexedGraphEdge edge, string toolQuality)
    {
        foreach (var s in edge.Steps)
        {
            if (s.Kind == ConstructionActionKind.UseTool &&
                string.Equals(s.PrototypeOrQualityOrTag, toolQuality, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }
}
