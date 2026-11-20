using System.Collections;
using Content.Shared.Construction;
using Content.Shared.Construction.Prototypes;
using Robust.Shared.Prototypes;
using RobustoClient.Reflection;

namespace RobustoClient.Systems.Construction;

public sealed partial class RobustaConstructionSolver
{
    private IEnumerable<ConstructionGraphNode> EnumerateRawGraphNodes(ConstructionGraphPrototype graph)
    {
        var nodesDict = GetMemberValue(graph, "Nodes") as IDictionary ?? GetMemberValue(graph, "_nodes") as IDictionary;
        if (nodesDict != null)
        {
            foreach (var val in nodesDict.Values)
            {
                if (val is ConstructionGraphNode node)
                    yield return node;
            }
            yield break;
        }

        if (GetMemberValue(graph, "_graph") is IEnumerable graphList)
        {
            foreach (var val in graphList)
            {
                if (val is ConstructionGraphNode node)
                    yield return node;
            }
        }
    }

    private static string GetRawNodeName(ConstructionGraphNode node)
        => GetStringMember(node, "Name") ?? string.Empty;

    private static IReadOnlyList<ConstructionGraphEdge> GetRawNodeEdges(ConstructionGraphNode node)
    {
        var edgesObj = GetMemberValue(node, "Edges") ?? GetMemberValue(node, "_edges");
        if (edgesObj is IReadOnlyList<ConstructionGraphEdge> roList)
            return roList;

        if (edgesObj is IEnumerable enumerable)
        {
            var list = new List<ConstructionGraphEdge>();
            foreach (var item in enumerable)
            {
                if (item is ConstructionGraphEdge edge)
                    list.Add(edge);
            }
            return list;
        }

        return Array.Empty<ConstructionGraphEdge>();
    }

    private string? GetRawNodeEntityId(ConstructionGraphNode node, EntityUid? uid)
    {
        // Board nodes need a live uid, so they return null at cache-build time and are mapped by prototype id elsewhere.
        var id = node.Entity.GetId(uid, uid, new GraphNodeEntityArgs(_entMan));
        return string.IsNullOrEmpty(id) ? null : id;
    }

    private IComponent? FindComponentByName(EntityUid uid, string componentTypeName)
    {
        if (_entMan == null || !_entMan.EntityExists(uid))
            return null;

        foreach (var comp in _entMan.GetComponents(uid))
        {
            if (string.Equals(comp.GetType().Name, componentTypeName, StringComparison.OrdinalIgnoreCase))
                return comp;
        }
        return null;
    }

    private static object? GetMemberValue(object? obj, string name) =>
        Reflect.Get(obj, name, ReflectOptions.Any);

    private static string? GetStringMember(object? obj, string name) =>
        ReflectConvert.TryGetId(GetMemberValue(obj, name));

    private static int GetIntMember(object? obj, string name, int defaultValue = 0)
    {
        var val = GetMemberValue(obj, name);
        if (val == null)
            return defaultValue;
        if (val is int i)
            return i;
        try
        {
            return Convert.ToInt32(val);
        }
        catch
        {
            return defaultValue;
        }
    }

    private static float GetFloatMember(object? obj, string name, float defaultValue = 0f)
    {
        var val = GetMemberValue(obj, name);
        if (val == null)
            return defaultValue;
        if (val is float f)
            return f;
        if (val is double d)
            return (float)d;
        if (val is int i)
            return i;
        if (val is TimeSpan ts)
            return (float)ts.TotalSeconds;
        try
        {
            return Convert.ToSingle(val);
        }
        catch
        {
            return defaultValue;
        }
    }

    private static string? FirstElementAsString(object? collectionObj)
    {
        if (collectionObj is IEnumerable enumerable && collectionObj is not string)
        {
            foreach (var item in enumerable)
            {
                var s = ExtractIdString(item);
                if (!string.IsNullOrEmpty(s))
                    return s;
            }
        }
        return null;
    }
}
