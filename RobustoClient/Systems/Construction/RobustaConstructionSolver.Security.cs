using System.Collections;
using Content.Shared.Construction;
using Content.Shared.Construction.Prototypes;

namespace RobustoClient.Systems.Construction;

public sealed partial class RobustaConstructionSolver
{
    public string? ResolveCurrentSecurityNode(
        EntityUid target,
        object? secComp = null,
        ConstructionGraphPrototype? graph = null)
    {
        EnsureCacheInitialized();

        if (graph == null)
            TryFindAirlockGraphAndNode(out graph, out _);

        secComp ??= FindComponentByName(target, "WiresPanelSecurityComponent");
        if (secComp == null)
            return null;

        if (GetMemberValue(secComp, "WiresAccessible") is true)
            return "airlock";

        var examine = GetStringMember(secComp, "Examine");
        if (graph != null && !string.IsNullOrEmpty(examine))
        {
            // 1. Check node actions if present on the graph
            foreach (var n in EnumerateGraphNodes(graph))
            {
                var actions = GetMemberValue(n, "Actions") as IEnumerable
                              ?? GetMemberValue(n, "_actions") as IEnumerable;
                if (actions == null)
                    continue;

                foreach (var act in actions)
                {
                    if (act == null)
                        continue;

                    var actExamine = GetStringMember(act, "Examine");
                    if (string.Equals(actExamine, examine, StringComparison.OrdinalIgnoreCase))
                        return GetNodeName(n);
                }
            }
        }

        // 2. Map standard locale IDs, adapting to the actual nodes present in the Airlock graph
        var mapped = GetSecurityRemovalTarget(examine);
        if (!string.IsNullOrEmpty(mapped))
        {
            if (graph == null || TryGetNodeByName(graph, mapped, out _))
                return mapped;

            if (string.Equals(examine, "wires-panel-component-on-examine-security-level5", StringComparison.OrdinalIgnoreCase) &&
                TryGetNodeByName(graph, "maxSecurityUnfinished", out _))
            {
                return "maxSecurityUnfinished";
            }

            if ((string.Equals(examine, "wires-panel-component-on-examine-security-level6", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(examine, "wires-panel-component-on-examine-security-level7", StringComparison.OrdinalIgnoreCase)) &&
                TryGetNodeByName(graph, "maxSecurity", out _))
            {
                return "maxSecurity";
            }
        }

        // 3. Fallback to the component/prototype's SecurityLevel field
        var secLevel = GetStringMember(secComp, "SecurityLevel");
        if (string.IsNullOrEmpty(secLevel) && _entMan.EntityExists(target))
        {
            var proto = _entMan.GetComponent<MetaDataComponent>(target).EntityPrototype;
            if (proto != null && _itemUtil.TryGetPrototypeComponent<object>(proto, "WiresPanelSecurity", out var protoSec))
                secLevel = GetStringMember(protoSec, "SecurityLevel");
        }

        if (!string.IsNullOrEmpty(secLevel) && (graph == null || TryGetNodeByName(graph, secLevel, out _)))
            return secLevel;

        return mapped;
    }

    public string? ResolveNextSecurityNode(EntityUid target, string currentNodeName)
    {
        if (string.IsNullOrEmpty(currentNodeName))
            return null;

        EnsureCacheInitialized();

        if (TryFindAirlockGraphAndNode(out var graph, out _) && graph != null)
        {
            var path = FindShortestEdgePath(graph, currentNodeName, "airlock");
            if (path != null && path.Count > 0)
            {
                var nextInGraph = GetNodeName(path[0].To);
                if (!string.IsNullOrEmpty(nextInGraph))
                    return nextInGraph;
            }
        }

        return GetNextSecurityNode(currentNodeName);
    }

    public static string? GetSecurityRemovalTarget(string? examine)
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

    public static string? GetNextSecurityNode(string node)
    {
        return node switch
        {
            "medSecurity" => "medSecurityUnfinished",
            "highSecurity" => "highSecurityUnfinished",
            "maxSecurity" => "highSecurity",
            "maxSecurityUnfinished" => "airlock",
            "superMaxSecurity" => "superMaxSecurityUnfinished",
            "superMaxSecurityUnfinished" => "maxSecurity",
            "medSecurityUnfinished" or "highSecurityUnfinished" => "airlock",
            _ => null
        };
    }

    private bool ShouldOpenWirePanelBeforeEdge(
        ConstructionGraphPrototype graph,
        ConstructionGraphNode currentNode,
        ConstructionGraphEdge firstEdge,
        IComponent panelComp)
    {
        if (TryGetEdgeWirePanelCondition(firstEdge, out bool requiredOpen))
            return requiredOpen;

        // Consoles (Computer graph) do not require opening a wire panel to deconstruct;
        // their first deconstruction step operates directly on the monitor/frame.
        if (string.Equals(graph.ID, "Computer", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(GetNodeName(currentNode), "computer", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var openTool = GetStringMember(panelComp, "OpeningTool") ?? "Screwing";
        if (FirstEdgeStepIsTool(firstEdge, openTool))
            return false;

        return true;
    }

    private bool ShouldWeldDoorBeforeDeconstruct(
        EntityUid target,
        ConstructionGraphPrototype graph,
        ConstructionGraphNode currentNode,
        ConstructionGraphEdge firstEdge)
    {
        if (TryGetEdgeDoorWeldedCondition(firstEdge, out bool requiredWelded))
            return requiredWelded;

        var weldableComp = FindComponentByName(target, "WeldableComponent");
        if (weldableComp == null)
            return false;

        if (GetMemberValue(weldableComp, "IsWelded") is true)
            return false;

        var graphId = graph.ID ?? string.Empty;
        var nodeName = GetNodeName(currentNode);
        if (graphId.Contains("Airlock", StringComparison.OrdinalIgnoreCase) ||
            nodeName.Contains("airlock", StringComparison.OrdinalIgnoreCase) ||
            FindComponentByName(target, "AirlockComponent") != null)
        {
            return true;
        }

        return false;
    }

    private bool ShouldCutInternalWiresBeforeEdge(
        EntityUid target,
        ConstructionGraphPrototype graph,
        ConstructionGraphNode currentNode,
        ConstructionGraphEdge firstEdge,
        IComponent? panelComp)
    {
        if (TryGetEdgeAllWiresCutCondition(firstEdge, out bool requiredAllCut))
            return requiredAllCut;

        var graphId = graph.ID ?? string.Empty;
        var nodeName = GetNodeName(currentNode);

        // Consoles and standard MachineFrames do not require cutting all hacking wires to deconstruct
        if (string.Equals(graphId, "Computer", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(graphId, "Machine", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(graphId, "AirlockShuttle", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(nodeName, "computer", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(nodeName, "machine", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        // Airlocks and Windoors require all internal wires in the wire panel to be cut when leaving the main door node
        if (string.Equals(nodeName, "airlock", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(nodeName, "glassAirlock", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(nodeName, "windoor", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(nodeName, "windoorSecure", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (string.Equals(graphId, "Airlock", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(graphId, "Windoor", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (panelComp != null && HasWiresUserInterface(target))
        {
            return true;
        }

        return false;
    }

    public bool HasWiresUserInterface(EntityUid target)
    {
        var uiComp = FindComponentByName(target, "UserInterfaceComponent");
        if (uiComp == null)
            return false;

        if (GetMemberValue(uiComp, "ClientOpenInterfaces") is IDictionary openDict)
        {
            foreach (DictionaryEntry entry in openDict)
            {
                var keyType = entry.Key?.GetType().Name ?? string.Empty;
                if (keyType.Contains("WiresUiKey", StringComparison.OrdinalIgnoreCase))
                    return true;
            }
        }

        if (GetMemberValue(uiComp, "Interfaces") is IDictionary interfacesDict)
        {
            foreach (DictionaryEntry entry in interfacesDict)
            {
                var keyType = entry.Key?.GetType().Name ?? string.Empty;
                if (keyType.Contains("WiresUiKey", StringComparison.OrdinalIgnoreCase))
                    return true;
            }
        }

        // Prototype fallback if Interfaces dictionary is private/inaccessible
        var proto = _entMan.GetComponent<MetaDataComponent>(target).EntityPrototype;
        if (proto != null &&
            (FindComponentByName(target, "AirlockComponent") != null ||
             FindComponentByName(target, "DoorBoltComponent") != null ||
             FindComponentByName(target, "WiresVisualsComponent") != null ||
             _itemUtil.HasPrototypeComponent(proto, "Wires") ||
             _itemUtil.HasPrototypeComponent(proto, "WiresPanelSecurity")))
        {
            return true;
        }

        return false;
    }
}
