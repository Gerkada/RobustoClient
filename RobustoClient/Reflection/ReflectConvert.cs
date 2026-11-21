using System.Globalization;
using System.Reflection;
using Robust.Shared.Maths;
using RobustoClient.ImGui.Theme;

namespace RobustoClient.Reflection;

/// <summary>
/// Converts values between the shapes reflection hands back and the shapes callers expect.
/// </summary>
public static class ReflectConvert
{
    /// <summary>
    /// Converts a declarative value into <paramref name="target"/>, false when the two cannot be reconciled.
    /// </summary>
    public static bool TryCoerce(object? value, Type target, out object? coerced)
    {
        coerced = null;

        var underlying = Nullable.GetUnderlyingType(target);
        if (underlying != null)
        {
            if (value == null)
                return true;

            target = underlying;
        }

        if (value == null)
            return !target.IsValueType;

        if (target.IsInstanceOfType(value))
        {
            coerced = value;
            return true;
        }

        var text = value as string ?? Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;

        if (target == typeof(string))
        {
            coerced = text;
            return true;
        }

        if (target == typeof(bool))
        {
            if (!bool.TryParse(text, out var parsedBool))
                return false;

            coerced = parsedBool;
            return true;
        }

        if (target.IsEnum)
        {
            try
            {
                coerced = Enum.Parse(target, text, ignoreCase: true);
                return true;
            }
            catch (ArgumentException)
            {
                return false;
            }
        }

        // ProtoId<T>, EntProtoId and LocId all carry the identifier as a string on Id.
        if (target.GetProperty("Id", BindingFlags.Public | BindingFlags.Instance) != null)
        {
            try
            {
                coerced = Activator.CreateInstance(target, text);
                return true;
            }
            catch (Exception e) when (e is MissingMethodException or TargetInvocationException or ArgumentException)
            {
                return false;
            }
        }

        if (target == typeof(TimeSpan))
        {
            if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds))
                return false;

            coerced = TimeSpan.FromSeconds(seconds);
            return true;
        }

        if (target == typeof(Color))
        {
            if (!ThemeHex.TryParse(text, out var vector))
                return false;

            coerced = new Color(vector.X, vector.Y, vector.Z, vector.W);
            return true;
        }

        // FixedPoint2 is resolved by name: it lives in Content.Shared on Wizden and Content.Common on
        // GoobStation, and a static reference to it throws on the other build.
        if (IsFixedPoint2(target))
        {
            if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var amount))
                return false;

            // New is overloaded, so pick the float overload explicitly rather than by name alone.
            var newMethod = target.GetMethod("New", BindingFlags.Public | BindingFlags.Static, null, [typeof(float)], null);
            if (newMethod == null)
                return false;

            coerced = newMethod.Invoke(null, [(float)amount]);
            return coerced != null;
        }

        if (!IsNumeric(target))
            return false;

        if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
            return false;

        coerced = Convert.ChangeType(number, target, CultureInfo.InvariantCulture);
        return true;
    }

    /// <summary>
    /// Numeric view of a value, including <c>FixedPoint2</c> through its <c>Float()</c> method.
    /// </summary>
    public static bool TryToDouble(object? value, out double result)
    {
        result = 0d;

        switch (value)
        {
            case null:
            case bool:
            case string:
                return false;
            case float f:
                result = f;
                return true;
            case double d:
                result = d;
                return true;
            case int i:
                result = i;
                return true;
            case long l:
                result = l;
                return true;
            case short s:
                result = s;
                return true;
            case byte by:
                result = by;
                return true;
            case decimal m:
                result = (double)m;
                return true;
        }

        var floatMethod = Reflect.GetMethod(value.GetType(), "Float", Type.EmptyTypes);
        if (floatMethod == null)
            return false;

        try
        {
            if (floatMethod.Invoke(value, null) is float invoked)
            {
                result = invoked;
                return true;
            }
        }
        catch (TargetInvocationException)
        {
            return false;
        }

        return false;
    }

    /// <summary>True when both values are numbers, so a caller can compare them numerically.</summary>
    public static bool TryToDoublePair(object? left, object? right, out double leftValue, out double rightValue) =>
        TryToDouble(left, out leftValue) & TryToDouble(right, out rightValue);

    private static readonly ReflectOptions OptionalIdOptions = new() { WarnOnMissing = false };

    /// <summary>
    /// The identifier behind a <c>ProtoId&lt;T&gt;</c>, <c>EntProtoId</c> or <c>LocId</c>, or the value's own string form.
    /// </summary>
    public static string? TryGetId(object? value)
    {
        if (value == null)
            return null;

        if (value is string text)
            return text;

        if (Reflect.TryGet(value, "Id", out var id, OptionalIdOptions) && id is string idText)
            return idText;

        return value.ToString();
    }

    /// <summary>
    /// A damage specifier's total as a float. <c>DamageSpecifier.GetTotal()</c> returns
    /// <c>FixedPoint2</c>, which lives in a different assembly on Goob, so the value is read
    /// reflectively rather than through the typed member.
    /// </summary>
    public static float DamageTotal(object? damage)
    {
        if (damage == null)
            return 0f;

        var getTotal = Reflect.GetMethod(damage.GetType(), "GetTotal", Type.EmptyTypes);
        if (getTotal == null)
            return 0f;

        try
        {
            return ReflectConvert.TryToDouble(getTotal.Invoke(damage, null), out var amount)
                ? (float)amount
                : 0f;
        }
        catch (TargetInvocationException)
        {
            return 0f;
        }
    }

    private static bool IsFixedPoint2(Type type) =>
        type.FullName == "Content.Shared.FixedPoint.FixedPoint2";

    private static bool IsNumeric(Type type) =>
        type == typeof(byte) || type == typeof(sbyte) ||
        type == typeof(short) || type == typeof(ushort) ||
        type == typeof(int) || type == typeof(uint) ||
        type == typeof(long) || type == typeof(ulong) ||
        type == typeof(float) || type == typeof(double);
}
