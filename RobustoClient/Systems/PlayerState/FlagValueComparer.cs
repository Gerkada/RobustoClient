using System.Collections;
using System.Globalization;
using RobustoClient.Reflection;

namespace RobustoClient.Systems.PlayerState;

/// <summary>
/// Compares a value read from a component against a flag definition's expectation.
/// </summary>
internal static class FlagValueComparer
{
    public static bool Matches(object? actual, FlagComparison op, object? expected)
    {
        switch (op)
        {
            case FlagComparison.IsNull:
                return actual == null;
            case FlagComparison.IsNotNull:
                return actual != null;
            case FlagComparison.Contains:
                return Contains(actual, expected);
        }

        if (actual == null)
            return false;

        if (op is FlagComparison.Equals or FlagComparison.NotEquals)
        {
            bool equal = AreEqual(actual, expected);
            return op == FlagComparison.Equals ? equal : !equal;
        }

        if (!ReflectConvert.TryToDoublePair(actual, expected, out double left, out double right))
            return false;

        return op switch
        {
            FlagComparison.GreaterThan => left > right,
            FlagComparison.GreaterOrEqual => left >= right,
            FlagComparison.LessThan => left < right,
            FlagComparison.LessOrEqual => left <= right,
            _ => false
        };
    }

    private static bool AreEqual(object actual, object? expected)
    {
        if (expected == null)
            return false;

        // Numeric before type coercion: an int field against "5.0f" must not fail on formatting.
        if (ReflectConvert.TryToDoublePair(actual, expected, out double left, out double right))
            return Math.Abs(left - right) < 1e-6;

        if (actual is string text)
        {
            var other = expected as string ?? Convert.ToString(expected, CultureInfo.InvariantCulture);
            return string.Equals(text, other, StringComparison.Ordinal);
        }

        if (actual is bool actualBool)
        {
            if (expected is bool expectedBool)
                return actualBool == expectedBool;

            return expected is string boolText &&
                   bool.TryParse(boolText, out var parsed) &&
                   actualBool == parsed;
        }

        if (actual.GetType().IsEnum && expected is string name)
            return string.Equals(actual.ToString(), name, StringComparison.OrdinalIgnoreCase);

        // ProtoId<T>, EntProtoId and LocId all expose the identifier on Id, so compare those rather
        // than the wrapper, whose Equals is type-strict.
        if (Reflect.TryGet(actual, "Id", out var actualId) &&
            actualId is string idText)
        {
            var other = expected as string
                        ?? (Reflect.TryGet(expected, "Id", out var expectedId)
                            ? expectedId as string
                            : null);

            return other != null && string.Equals(idText, other, StringComparison.OrdinalIgnoreCase);
        }

        return actual.Equals(expected);
    }

    private static bool Contains(object? actual, object? expected)
    {
        if (actual == null || expected == null)
            return false;

        if (actual is string haystack)
            return expected is string needle &&
                   haystack.Contains(needle, StringComparison.OrdinalIgnoreCase);

        if (actual is not IEnumerable sequence)
            return false;

        foreach (var item in sequence)
        {
            if (AreEqual(item!, expected))
                return true;
        }

        return false;
    }
}
