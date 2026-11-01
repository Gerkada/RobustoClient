using System.Collections;
using Content.Shared.Construction;

namespace RobustoClient.Systems.Construction;

public sealed partial class RobustaConstructionSolver
{
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

    private bool TryGetEdgeDoorWeldedCondition(ConstructionGraphEdge edge, out bool requiredWelded)
    {
        requiredWelded = true;
        foreach (var cond in EnumerateEdgeConditions(edge))
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

    private bool TryGetEdgeAllWiresCutCondition(ConstructionGraphEdge edge, out bool requiredAllCut)
    {
        requiredAllCut = true;
        foreach (var cond in EnumerateEdgeConditions(edge))
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
}
