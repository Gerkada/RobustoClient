using System.Collections;

namespace RobustoClient.Systems.Construction;

public sealed partial class RobustaConstructionSolver
{
    public string? ResolveCurrentSecurityNode(
        in ConstructionTargetSnapshot snapshot,
        IndexedConstructionGraph? graph = null)
    {
        EnsureCacheInitialized();

        if (graph == null)
            TryFindAirlockGraphAndNode(out graph, out _);

        if (snapshot.IsSecurityWiresAccessible)
            return "airlock";

        var examine = snapshot.SecurityExamine;
        if (graph != null && !string.IsNullOrEmpty(examine))
        {
            foreach (var n in graph.Nodes.Values)
            {
                if (string.Equals(n.SecurityExamine, examine, StringComparison.OrdinalIgnoreCase))
                    return n.Name;
            }

            if (TryGetNodeByName(graph, examine, out var directSecNode))
                return directSecNode.Name;
        }

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

        return mapped;
    }

    public string? ResolveCurrentSecurityNode(
        EntityUid target,
        object? secComp = null,
        IndexedConstructionGraph? graph = null)
    {
        EnsureCacheInitialized();

        if (graph == null)
            TryFindAirlockGraphAndNode(out graph, out _);

        secComp ??= FindComponentByName(target, "WiresPanelSecurityComponent");
        if (secComp == null)
            return null;

        bool wiresAccessible = GetMemberValue(secComp, "WiresAccessible") is true;
        var examine = GetStringMember(secComp, "Examine");
        var snapshot = new ConstructionTargetSnapshot(
            IsSecurityWiresAccessible: wiresAccessible,
            SecurityExamine: examine);

        var resolved = ResolveCurrentSecurityNode(in snapshot, graph);
        if (!string.IsNullOrEmpty(resolved))
            return resolved;

        var secLevel = GetStringMember(secComp, "SecurityLevel");
        if (string.IsNullOrEmpty(secLevel) && _entMan != null && _entMan.EntityExists(target))
        {
            var proto = _entMan.GetComponent<MetaDataComponent>(target).EntityPrototype;
            if (proto != null && _itemUtil != null && _itemUtil.TryGetPrototypeComponent<object>(proto, "WiresPanelSecurity", out var protoSec))
                secLevel = GetStringMember(protoSec, "SecurityLevel");
        }

        if (!string.IsNullOrEmpty(secLevel) && (graph == null || TryGetNodeByName(graph, secLevel, out _)))
            return secLevel;

        return null;
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
                var nextInGraph = path[0].To.Name;
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

    private static bool ShouldOpenWirePanelBeforeEdge(
        IndexedConstructionGraph graph,
        IndexedGraphNode currentNode,
        IndexedGraphEdge firstEdge,
        in ConstructionTargetSnapshot snapshot)
    {
        if (!snapshot.HasWirePanel)
            return false;

        if (firstEdge.RequiredWirePanelOpen is { } requiredOpen)
            return requiredOpen;

        if (string.Equals(graph.Id, "Computer", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(currentNode.Name, "computer", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var openTool = string.IsNullOrEmpty(snapshot.WirePanelOpeningTool) ? "Screwing" : snapshot.WirePanelOpeningTool;
        if (FirstEdgeStepIsTool(firstEdge, openTool))
            return false;

        return true;
    }

    private static bool ShouldWeldDoorBeforeDeconstruct(
        in ConstructionTargetSnapshot snapshot,
        IndexedConstructionGraph graph,
        IndexedGraphNode currentNode,
        IndexedGraphEdge firstEdge)
    {
        if (snapshot.IsDoorWelded != false)
            return false;

        if (firstEdge.RequiredDoorWelded is { } requiredWelded)
            return requiredWelded;

        var graphId = graph.Id ?? string.Empty;
        var nodeName = currentNode.Name;
        return graphId.Contains("Airlock", StringComparison.OrdinalIgnoreCase) ||
               nodeName.Contains("airlock", StringComparison.OrdinalIgnoreCase);
    }

    private static bool ShouldCutInternalWiresBeforeEdge(
        in ConstructionTargetSnapshot snapshot,
        IndexedConstructionGraph graph,
        IndexedGraphNode currentNode,
        IndexedGraphEdge firstEdge)
    {
        if (firstEdge.RequiredAllWiresCut is { } requiredAllCut)
            return requiredAllCut;

        var graphId = graph.Id ?? string.Empty;
        var nodeName = currentNode.Name;

        if (string.Equals(graphId, "Computer", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(graphId, "Machine", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(graphId, "AirlockShuttle", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(nodeName, "computer", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(nodeName, "machine", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

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

        return snapshot.HasWirePanel && snapshot.HasWiresUi;
    }

    public bool HasWiresUserInterface(EntityUid target)
    {
        if (_entMan == null || !_entMan.EntityExists(target))
            return false;

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

        var proto = _entMan.GetComponent<MetaDataComponent>(target).EntityPrototype;
        if (proto != null &&
            (FindComponentByName(target, "AirlockComponent") != null ||
             FindComponentByName(target, "DoorBoltComponent") != null ||
             FindComponentByName(target, "WiresVisualsComponent") != null ||
             (_itemUtil != null && _itemUtil.HasPrototypeComponent(proto, "Wires")) ||
             (_itemUtil != null && _itemUtil.HasPrototypeComponent(proto, "WiresPanelSecurity"))))
        {
            return true;
        }

        return false;
    }
}
