using System.Collections;
using System.Reflection;
using Content.Shared.Construction;
using Content.Shared.Construction.Prototypes;
using Robust.Shared.Prototypes;

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
        var entityObj = GetMemberValue(node, "Entity") ?? GetMemberValue(node, "_entity");
        if (entityObj == null)
            return null;

        if (entityObj is string str)
            return string.IsNullOrEmpty(str) ? null : str;

        var directId = GetStringMember(entityObj, "Id") ?? GetStringMember(entityObj, "Prototype");
        if (!string.IsNullOrEmpty(directId))
            return directId;

        var getIdMethod = entityObj.GetType().GetMethod("GetId", BindingFlags.Public | BindingFlags.Instance);
        if (getIdMethod != null)
        {
            try
            {
                var parameters = getIdMethod.GetParameters();
                var args = new object?[parameters.Length];
                for (int i = 0; i < parameters.Length; i++)
                {
                    var pType = parameters[i].ParameterType;
                    if (pType == typeof(EntityUid?) || pType == typeof(EntityUid))
                        args[i] = uid;
                    else if (pType == typeof(IEntityManager))
                        args[i] = _entMan;
                    else if (pType == typeof(IPrototypeManager))
                        args[i] = _protoMan;
                    else if (pType == typeof(IComponentFactory))
                        args[i] = _compFactory;
                    else if (pType.Name == "GraphNodeEntityArgs")
                        args[i] = CreateGraphNodeEntityArgs(pType, uid);
                    else
                        args[i] = null;
                }

                var res = getIdMethod.Invoke(entityObj, args)?.ToString();
                if (!string.IsNullOrEmpty(res))
                    return res;
            }
            catch
            {
                // Ignore if node entity requires server-only state
            }
        }

        return null;
    }

    private object? CreateGraphNodeEntityArgs(Type pType, EntityUid? uid)
    {
        foreach (var ctor in pType.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
        {
            var cParams = ctor.GetParameters();
            var cArgs = new object?[cParams.Length];
            bool ok = true;
            for (int i = 0; i < cParams.Length; i++)
            {
                var t = cParams[i].ParameterType;
                if (t == typeof(EntityUid?) || t == typeof(EntityUid))
                    cArgs[i] = uid ?? EntityUid.Invalid;
                else if (t == typeof(IEntityManager))
                    cArgs[i] = _entMan;
                else if (t == typeof(IPrototypeManager))
                    cArgs[i] = _protoMan;
                else if (t == typeof(IComponentFactory))
                    cArgs[i] = _compFactory;
                else if (t.IsValueType)
                    cArgs[i] = Activator.CreateInstance(t);
                else
                {
                    ok = false;
                    break;
                }
            }

            if (ok)
                return ctor.Invoke(cArgs);
        }

        return pType.IsValueType ? Activator.CreateInstance(pType) : null;
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

    private static object? GetMemberValue(object? obj, string name)
    {
        if (obj == null)
            return null;

        var type = obj.GetType();
        const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.IgnoreCase;

        var prop = type.GetProperty(name, flags);
        if (prop != null)
            return prop.GetValue(obj);

        var field = type.GetField(name, flags) ?? type.GetField("_" + name, flags);
        if (field != null)
            return field.GetValue(obj);

        return null;
    }

    private static string? GetStringMember(object? obj, string name)
    {
        var val = GetMemberValue(obj, name);
        if (val == null)
            return null;
        if (val is string s)
            return s;
        if (GetMemberValue(val, "Id") is string idStr)
            return idStr;
        return val.ToString();
    }

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
