namespace RobustoClient.Reflection;

/// <summary>
/// Lookup flags for <see cref="Reflect"/>, a record so it can key the member cache.
/// </summary>
public sealed record ReflectOptions
{
    /// <summary>Include non-public members.</summary>
    public bool NonPublic { get; init; }

    /// <summary>Match member names case-insensitively.</summary>
    public bool IgnoreCase { get; init; }

    /// <summary>Fall back to the <c>_name</c> field when <c>name</c> is missing.</summary>
    public bool UnderscoreFallback { get; init; } = true;

    /// <summary>Let <see cref="Reflect.TrySet"/> write a get-only property's backing field.</summary>
    public bool BackingFields { get; init; } = true;

    /// <summary>Log once when a member cannot be found, so a rename upstream is visible.</summary>
    public bool WarnOnMissing { get; init; } = true;

    public static readonly ReflectOptions Default = new();

    /// <summary>Public and non-public, case-insensitive. The usual choice for upstream members.</summary>
    public static readonly ReflectOptions Any = new() { NonPublic = true, IgnoreCase = true };
}
