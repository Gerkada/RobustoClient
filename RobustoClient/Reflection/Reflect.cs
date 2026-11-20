using System.Collections.Concurrent;
using System.Reflection;

namespace RobustoClient.Reflection;

/// <summary>
/// Member access for types not known at compile time, with one shared cache.
/// </summary>

public static class Reflect
{
    private static readonly ConcurrentDictionary<(Type Type, string Member, ReflectOptions Options), MemberInfo?> Members = new();
    private static readonly ConcurrentDictionary<(Type Type, string Method, string Signature, ReflectOptions Options), MethodInfo?> Methods = new();
    private static readonly ConcurrentDictionary<string, Type?> Types = new();
    private static readonly ConcurrentDictionary<string, byte> Warned = new();

    // ---- Read ---------------------------------------------------------------------------------

    /// <summary>Reads a member path, warning once when a segment is missing.</summary>
    public static bool TryGet(object? target, string member, out object? value, ReflectOptions? options = null)
    {
        value = null;
        if (target == null || string.IsNullOrWhiteSpace(member))
            return false;

        var opts = options ?? ReflectOptions.Default;
        object? current = target;

        foreach (var segment in member.Split('.'))
        {
            if (current == null)
                return false;

            var info = ResolveMember(current.GetType(), segment, opts);
            if (info == null)
            {
                WarnOnce(current.GetType(), member, segment, opts);
                return false;
            }

            current = info switch
            {
                PropertyInfo property => property.GetValue(current),
                FieldInfo field => field.GetValue(current),
                _ => null
            };
        }

        value = current;
        return true;
    }

    public static object? Get(object? target, string member, ReflectOptions? options = null) =>
        TryGet(target, member, out var value, options) ? value : null;

    public static T? Get<T>(object? target, string member, ReflectOptions? options = null) =>
        Get(target, member, options) is T typed ? typed : default;

    /// <summary>Reads a member and renders it as a string, unwrapping <c>Id</c> when present.</summary>
    public static string? GetString(object? target, string member, ReflectOptions? options = null) =>
        ReflectConvert.TryGetId(Get(target, member, options));

    /// <summary>First member in <paramref name="members"/> that exists, for renamed upstream fields.</summary>
    public static object? GetFirst(object? target, params string[] members)
    {
        foreach (var member in members)
        {
            if (TryGet(target, member, out var value))
                return value;
        }

        return null;
    }

    public static T? GetFirst<T>(object? target, params string[] members) =>
        GetFirst(target, members) is T typed ? typed : default;

    // ---- Write --------------------------------------------------------------------------------

    /// <summary>
    /// Writes a member: a settable property, then a field, then a get-only property's backing field.
    /// </summary>
    public static bool TrySet(object? target, string member, object? value, ReflectOptions? options = null)
    {
        if (target == null || string.IsNullOrWhiteSpace(member))
            return false;

        var opts = options ?? ReflectOptions.Default;
        var type = target.GetType();

        if (ResolveMember(type, member, opts) is PropertyInfo property && property.CanWrite)
        {
            try
            {
                property.SetValue(target, value);
                return true;
            }
            catch (ArgumentException)
            {
                return false;
            }
        }

        if (ResolveMember(type, member, opts) is FieldInfo field)
        {
            try
            {
                field.SetValue(target, value);
                return true;
            }
            catch (ArgumentException)
            {
                return false;
            }
        }

        if (!opts.BackingFields)
            return false;

        var backing = type.GetField($"<{member}>k__BackingField", BindingFlags.NonPublic | BindingFlags.Instance);
        if (backing == null)
            return false;

        try
        {
            backing.SetValue(target, value);
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    // ---- Invoke -------------------------------------------------------------------------------

    public static bool TryInvoke(object? target, string method, out object? result, params object?[] args)
    {
        result = null;
        if (target == null)
            return false;

        var info = GetMethod(target.GetType(), method, ReflectOptions.Any);
        return info != null && TryInvokeCore(target, info, args, out result);
    }

    /// <summary>Invokes the overload whose parameter types match <paramref name="parameterTypes"/>.</summary>
    public static bool TryInvoke(
        object? target,
        string method,
        Type[] parameterTypes,
        out object? result,
        params object?[] args)
    {
        result = null;
        if (target == null)
            return false;

        var info = GetMethod(target.GetType(), method, parameterTypes, ReflectOptions.Any);
        return info != null && TryInvokeCore(target, info, args, out result);
    }

    /// <summary>
    /// Invokes a method whose arguments are supplied by parameter type from <paramref name="context"/>.
    /// </summary>
    public static bool TryInvokeInjected(
        object? target,
        string method,
        ReflectContext context,
        out object? result)
    {
        result = null;
        if (target == null)
            return false;

        var info = GetMethod(target.GetType(), method, ReflectOptions.Any);
        if (info == null)
            return false;

        var parameters = info.GetParameters();
        if (!context.CanResolveAll(parameters))
            return false;

        var args = new object?[parameters.Length];
        for (int i = 0; i < parameters.Length; i++)
            args[i] = context.Resolve(parameters[i].ParameterType);

        return TryInvokeCore(target, info, args, out result);
    }

    private static bool TryInvokeCore(object target, MethodInfo info, object?[] args, out object? result)
    {
        result = null;

        try
        {
            result = info.Invoke(target, args);
            return true;
        }
        catch (TargetInvocationException)
        {
            return false;
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (TargetParameterCountException)
        {
            return false;
        }
    }

    // ---- Members ------------------------------------------------------------------------------

    public static MemberInfo? GetMemberInfo(Type? type, string member, ReflectOptions? options = null) =>
        type == null ? null : ResolveMember(type, member, options ?? ReflectOptions.Default);

    public static PropertyInfo? GetProperty(Type? type, string member, ReflectOptions? options = null) =>
        GetMemberInfo(type, member, options) as PropertyInfo;

    public static FieldInfo? GetField(Type? type, string member, ReflectOptions? options = null) =>
        GetMemberInfo(type, member, options) as FieldInfo;

    public static MethodInfo? GetMethod(Type? type, string method, ReflectOptions? options = null) =>
        type == null ? null : ResolveMethod(type, method, null, options ?? ReflectOptions.Default);

    public static MethodInfo? GetMethod(
        Type? type,
        string method,
        Type[] parameterTypes,
        ReflectOptions? options = null) =>
        type == null ? null : ResolveMethod(type, method, parameterTypes, options ?? ReflectOptions.Default);

    /// <summary>Whether any of <paramref name="members"/> exists on the type.</summary>
    public static bool HasAnyMember(Type? type, IEnumerable<string> members, ReflectOptions? options = null)
    {
        if (type == null)
            return false;

        var opts = options ?? ReflectOptions.Default;
        foreach (var member in members)
        {
            if (ResolveMember(type, member, opts) != null)
                return true;
        }

        return false;
    }

    public static IEnumerable<MemberInfo> EnumerateMembers(Type type, ReflectOptions? options = null)
    {
        var flags = BindingFlagsFor(options ?? ReflectOptions.Default);

        foreach (var property in type.GetProperties(flags))
            yield return property;

        foreach (var field in type.GetFields(flags))
            yield return field;
    }

    // ---- Types --------------------------------------------------------------------------------

    /// <summary>
    /// Resolves a type by name, trying the assembly-qualified form first and then every loaded assembly.
    /// </summary>
    public static Type? FindType(string typeName, string? assemblyName = null)
    {
        if (string.IsNullOrWhiteSpace(typeName))
            return null;

        var key = assemblyName == null ? typeName : $"{typeName}, {assemblyName}";
        return Types.GetOrAdd(key, _ => ResolveType(typeName, assemblyName));
    }

    public static bool TryFindType(string typeName, out Type? type, string? assemblyName = null)
    {
        type = FindType(typeName, assemblyName);
        return type != null;
    }

    private static Type? ResolveType(string typeName, string? assemblyName)
    {
        if (assemblyName != null)
            return Type.GetType($"{typeName}, {assemblyName}", false);

        var direct = Type.GetType(typeName, false);
        if (direct != null)
            return direct;

        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            try
            {
                var found = assembly.GetType(typeName, false);
                if (found != null)
                    return found;
            }
            catch (Exception e) when (e is FileNotFoundException or TypeLoadException or BadImageFormatException)
            {
                // A dynamic or partially loaded assembly is not a reason to fail the lookup.
            }
        }

        return null;
    }

    // ---- Lookup -------------------------------------------------------------------------------

    private static BindingFlags BindingFlagsFor(ReflectOptions options)
    {
        var flags = BindingFlags.Instance;
        flags |= options.NonPublic ? BindingFlags.Public | BindingFlags.NonPublic : BindingFlags.Public;

        if (options.IgnoreCase)
            flags |= BindingFlags.IgnoreCase;

        return flags;
    }

    private static MemberInfo? ResolveMember(Type type, string member, ReflectOptions options) =>
        Members.GetOrAdd((type, member, options), key =>
        {
            var flags = BindingFlagsFor(key.Options);

            var property = key.Type.GetProperty(key.Member, flags);
            if (property != null)
                return property;

            var field = key.Type.GetField(key.Member, flags);
            if (field != null)
                return field;

            if (!key.Options.UnderscoreFallback || key.Member.StartsWith('_'))
                return null;

            return key.Type.GetField($"_{key.Member}", flags);
        });

    private static MethodInfo? ResolveMethod(
        Type type,
        string method,
        Type[]? parameterTypes,
        ReflectOptions options)
    {
        var signature = parameterTypes == null
            ? string.Empty
            : string.Join(",", parameterTypes.Select(t => t.FullName ?? t.Name));

        return Methods.GetOrAdd((type, method, signature, options), key =>
        {
            var flags = BindingFlagsFor(key.Options);

            if (parameterTypes != null)
                return key.Type.GetMethod(key.Method, flags, null, parameterTypes, null);

            // GetMethod(name, flags) throws on overloads, so take the first by name instead.
            return key.Type.GetMethods(flags).FirstOrDefault(m => m.Name == key.Method);
        });
    }

    private static void WarnOnce(Type type, string member, string segment, ReflectOptions options)
    {
        if (!options.WarnOnMissing)
            return;

        if (!Warned.TryAdd($"{type.FullName}.{member}", 0))
            return;

        MarseyLogger.Warn(
            $"[Reflect] {type.Name} has no member '{segment}' for path '{member}', the lookup will keep failing.");
    }
}
