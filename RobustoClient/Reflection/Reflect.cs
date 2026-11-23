using System.Collections.Concurrent;
using System.IO;
using System.Linq;
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
    private static readonly ConcurrentDictionary<string, Type> TypeByName = new();
    private static readonly HashSet<Assembly> IndexedAssemblies = new();
    private static readonly object IndexLock = new();

    // Missing-member warnings fire from per-entity loops, so each one is logged once per type and path.
    private static readonly ConcurrentDictionary<string, byte> Warned = new();

    // Assembly count at the last index pass. Assemblies only ever load, so a changed count is the
    // cheap signal that new ones arrived.
    private static int _indexedAssemblyCount = -1;

    private static readonly ReflectOptions ProbeOptions = ReflectOptions.Any with { WarnOnMissing = false };

    #region Read

    /// <summary>Reads a member path, warning once when a segment is missing.</summary>
    public static bool TryGet(object? target, string member, out object? value, ReflectOptions? options = null)
    {
        value = null;
        if (target == null || string.IsNullOrWhiteSpace(member))
            return false;

        var opts = options ?? ReflectOptions.Default;
        if (!member.Contains('.'))
            return TryGetSegment(target, member, member, opts, out value);

        object? current = target;
        foreach (var segment in member.Split('.'))
        {
            if (current == null || !TryGetSegment(current, member, segment, opts, out current))
                return false;
        }

        value = current;
        return true;
    }

    private static bool TryGetSegment(
        object current,
        string fullPath,
        string segment,
        ReflectOptions opts,
        out object? value)
    {
        value = null;
        var type = current.GetType();
        var info = ResolveMember(type, segment, opts);
        if (info == null)
        {
            WarnOnce(type, fullPath, segment, opts);
            return false;
        }

        value = info switch
        {
            PropertyInfo property => property.GetValue(current),
            FieldInfo field => field.GetValue(current),
            _ => null
        };
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
    public static object? GetFirst(object? target, params string[] members) =>
        GetFirst(target, ProbeOptions, members);

    public static object? GetFirst(object? target, ReflectOptions options, params string[] members)
    {
        foreach (var member in members)
        {
            if (TryGet(target, member, out var value, options))
                return value;
        }

        return null;
    }

    public static T? GetFirst<T>(object? target, params string[] members) =>
        GetFirst(target, members) is T typed ? typed : default;

    public static T? GetFirst<T>(object? target, ReflectOptions options, params string[] members) =>
        GetFirst(target, options, members) is T typed ? typed : default;

    #endregion

    #region Write

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

    #endregion

    #region Invoke

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

    #endregion

    #region Members

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

    #endregion

    #region Types

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

    /// <summary>
    /// Indexes every loaded assembly by simple type name.
    /// </summary>
    /// <remarks>
    /// Call once at boot, before anything resolves a type by name. The index is what makes
    /// <see cref="FindTypeByName"/> a dictionary read instead of an assembly scan. Calling it again
    /// indexes only the assemblies that loaded since the last pass.
    /// </remarks>
    public static void BuildTypeIndex() => IndexAssemblies(log: true);

    /// <summary>
    /// Resolves a type from its simple name using the index.
    /// </summary>
    /// <remarks>
    /// This is the fork-safe form. <c>GasTileHeatBlurOverlay</c> survives a namespace or assembly move that
    /// a full name, and certainly a <c>typeof</c>, would not, and an absent type answers null instead of
    /// throwing on first touch.
    /// </remarks>
    public static Type? FindTypeByName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return null;

        if (TypeByName.TryGetValue(name, out var type))
            return type;

        // A miss may mean an assembly loaded since the last pass. Index the new ones and re-check:
        // another thread may have completed the pass this one skipped.
        IndexAssemblies(log: false);

        return TypeByName.TryGetValue(name, out type) ? type : null;
    }

    public static bool TryFindTypeByName(string name, out Type? type)
    {
        type = FindTypeByName(name);
        return type != null;
    }

    /// <summary>
    /// Indexes assemblies that are not yet indexed, by simple type name.
    /// </summary>
    /// <remarks>
    /// The count check is the fast path: assemblies only ever load, so an unchanged count means the index
    /// is current and the lock is never taken. A pass only walks assemblies the set has not seen, which
    /// keeps a rescan after a late load proportional to what arrived rather than to the process.
    /// </remarks>
    private static void IndexAssemblies(bool log)
    {
        var loaded = AppDomain.CurrentDomain.GetAssemblies();
        if (loaded.Length == Volatile.Read(ref _indexedAssemblyCount))
            return;

        lock (IndexLock)
        {
            // Another thread may have indexed while this one waited for the lock.
            if (loaded.Length == _indexedAssemblyCount)
                return;

            var added = 0;

            foreach (var assembly in loaded)
            {
                if (!IndexedAssemblies.Add(assembly))
                    continue;

                foreach (var type in TypesIn(assembly))
                {
                    if (type.Name.Length == 0)
                        continue;

                    if (TypeByName.TryGetValue(type.Name, out var existing))
                    {
                        if (Prefer(type, existing))
                            TypeByName[type.Name] = type;

                        continue;
                    }

                    TypeByName[type.Name] = type;
                    added++;
                }
            }

            _indexedAssemblyCount = loaded.Length;

            if (log && added > 0)
                MarseyLogger.Info($"[Reflect] Indexed {added} types by name.");
        }
    }

    /// <summary>
    /// Whether <paramref name="candidate"/> should replace <paramref name="existing"/> for a shared simple name.
    /// </summary>
    private static bool Prefer(Type candidate, Type existing)
    {
        if (candidate == existing)
            return false;

        try
        {
            return existing.IsAssignableFrom(candidate);
        }
        catch (Exception e) when (e is TypeLoadException or FileNotFoundException or BadImageFormatException)
        {
            // A type whose base could not be loaded cannot be preferred over a working one.
            return false;
        }
    }

    private static IEnumerable<Type> TypesIn(Assembly assembly)
    {
        // A dynamic assembly is emitted at runtime and may not outlive its types, so it is not indexed.
        if (assembly.IsDynamic)
            return [];

        try
        {
            return assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException e)
        {
            // A partly loaded assembly still answers for the types that did load.
            return e.Types.OfType<Type>();
        }
        catch (Exception e) when (e is FileNotFoundException or TypeLoadException or BadImageFormatException)
        {
            return [];
        }
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

    #endregion

    #region Lookup

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

    #endregion
}
