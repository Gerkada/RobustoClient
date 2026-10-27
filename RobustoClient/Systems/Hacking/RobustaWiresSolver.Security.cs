using Content.Shared.Construction.Prototypes;
using Content.Shared.Wires;
using Robust.Shared.Prototypes;
using RobustoClient.Systems.Construction;

namespace RobustoClient.Systems.Hacking;

public sealed partial class RobustaWiresSolver
{
    [Dependency] private readonly IPrototypeManager _prototypes = default!;
    [Dependency] private readonly RobustaAutoConstructSystem _construction = default!;

    private static string? GetSecurityRemovalTarget(string? examine)
    {
        return examine switch
        {
            "wires-panel-component-on-examine-security-level1" => "medSecurityUnfinished",
            "wires-panel-component-on-examine-security-level2" => "medSecurity",
            "wires-panel-component-on-examine-security-level3" => "highSecurityUnfinished",
            "wires-panel-component-on-examine-security-level4" => "highSecurity",
            "wires-panel-component-on-examine-security-level5" => "maxSecurity",
            "wires-panel-component-on-examine-security-level6" => "superMaxSecurityUnfinished",
            "wires-panel-component-on-examine-security-level7" => "superMaxSecurity",
            _ => null
        };
    }

    private static string? GetNextSecurityNode(string node)
    {
        return node switch
        {
            "medSecurity" => "medSecurityUnfinished",
            "highSecurity" => "highSecurityUnfinished",
            "maxSecurity" => "highSecurity",
            "superMaxSecurity" => "superMaxSecurityUnfinished",
            "superMaxSecurityUnfinished" => "maxSecurity",
            "medSecurityUnfinished" or "highSecurityUnfinished" => "airlock",
            _ => null
        };
    }

    private void AdvancePanelSecurity(ActiveSolve solve, WiresPanelSecurityComponent security)
    {
        // SecurityLevel is the map-init node; Examine is updated after every actual transition.
        if (solve.RemovingSecurity)
        {
            if (_construction.IsBusy)
            {
                solve.Elapsed = 0f;
                return;
            }

            if (security.Examine == solve.SecurityExamine)
            {
                FailSolve("security removal did not change the replicated security state");
                return;
            }

            solve.RemovingSecurity = false;
            solve.ToolRequested = false;
        }

        if (_construction.IsBusy || !_prototypes.TryIndex<ConstructionGraphPrototype>("Airlock", out var graph))
        {
            FailSolve("construction executor is busy or the Airlock graph is unavailable");
            return;
        }

        var nodeName = GetSecurityRemovalTarget(security.Examine);
        if (nodeName == null || !graph.Nodes.TryGetValue(nodeName, out var node))
        {
            FailSolve($"unsupported panel security state: {security.Examine}");
            return;
        }

        var nextNode = GetNextSecurityNode(nodeName);
        var edge = node.Edges.FirstOrDefault(candidate => candidate.Target == nextNode);
        var toolStep = edge?.Steps.Count == 1
            ? edge.Steps[0] as Content.Shared.Construction.Steps.ToolConstructionGraphStep
            : null;
        if (edge == null || toolStep == null)
        {
            FailSolve("security graph does not have a tool-only removal edge");
            return;
        }

        var step = new ConstructionActionStep(ConstructionActionKind.UseTool,
            $"Remove panel security: {nodeName}", toolStep.Tool.ToString(), DoAfterDelay: toolStep.DoAfter);
        solve.SecurityExamine = security.Examine;
        solve.RemovingSecurity = true;
        solve.ToolRequested = false;
        solve.Elapsed = 0f;
        _construction.StartJob(solve.Target, new SolvedConstructionPlan(graph.ID, nodeName, edge.Target, true, [step]));
    }
}