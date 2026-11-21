using System.Reflection;

namespace RobustoClient.Reflection;

/// <summary>
/// Values a reflected method or constructor can be given by parameter type.
/// </summary>
public sealed class ReflectContext
{
    private readonly List<(Type Type, object? Value)> _values = new();

    public ReflectContext Add<T>(T value)
    {
        _values.Add((typeof(T), value));
        return this;
    }

    public ReflectContext Add(object? value)
    {
        if (value != null)
            _values.Add((value.GetType(), value));

        return this;
    }

    /// <summary>The value for <paramref name="parameterType"/>, or null when nothing matches.</summary>
    public object? Resolve(Type parameterType)
    {
        foreach (var (type, value) in _values)
        {
            if (type == parameterType)
                return value;
        }

        var underlying = Nullable.GetUnderlyingType(parameterType);
        if (underlying != null)
        {
            foreach (var (type, value) in _values)
            {
                if (type == underlying)
                    return value;
            }
        }
        else if (parameterType.IsValueType)
        {
            // The caller holds EntityUid? and the method wants EntityUid.
            foreach (var (type, value) in _values)
            {
                if (Nullable.GetUnderlyingType(type) == parameterType)
                    return value;
            }
        }

        foreach (var (type, value) in _values)
        {
            if (parameterType.IsAssignableFrom(type))
                return value;
        }

        return null;
    }

    /// <summary>Whether every parameter of a method can be supplied from this context.</summary>
    public bool CanResolveAll(ParameterInfo[] parameters)
    {
        foreach (var parameter in parameters)
        {
            if (Resolve(parameter.ParameterType) == null)
                return false;
        }

        return true;
    }
}
