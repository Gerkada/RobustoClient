using System.Numerics;

namespace RobustoClient.ImGui.Theme;

/// <summary>
/// Parses <c>#RRGGBB</c> and <c>#RRGGBBAA</c> into an ImGui colour vector.
/// ImGui consumes sRGB components directly, so no gamma conversion happens here.
/// </summary>
public static class ThemeHex
{
    /// <summary>Parses a hex colour. Returns false rather than throwing on malformed input.</summary>
    public static bool TryParse(string? text, out Vector4 color)
    {
        color = Vector4.One;
        if (string.IsNullOrWhiteSpace(text))
            return false;

        var clean = text.AsSpan().Trim();
        if (clean.Length > 0 && clean[0] == '#')
            clean = clean[1..];

        if (clean.Length is not (6 or 8))
            return false;

        foreach (var c in clean)
        {
            if (!Uri.IsHexDigit(c))
                return false;
        }

        byte alpha = clean.Length == 8 ? Pair(clean[6..8]) : (byte)255;

        color = new Vector4(
            Pair(clean[..2]) / 255f,
            Pair(clean[2..4]) / 255f,
            Pair(clean[4..6]) / 255f,
            alpha / 255f);

        return true;
    }

    /// <summary>Formats a colour as <c>#RRGGBB</c>, or <c>#RRGGBBAA</c> when it is not fully opaque.</summary>
    public static string Format(Vector4 color)
    {
        byte ToByte(float v) => (byte)Math.Clamp(MathF.Round(v * 255f), 0f, 255f);

        var rgb = $"#{ToByte(color.X):X2}{ToByte(color.Y):X2}{ToByte(color.Z):X2}";
        return ToByte(color.W) == 255 ? rgb : rgb + $"{ToByte(color.W):X2}";
    }

    private static byte Pair(ReadOnlySpan<char> s) => (byte)((HexValue(s[0]) << 4) | HexValue(s[1]));

    private static int HexValue(char c) => c switch
    {
        >= '0' and <= '9' => c - '0',
        >= 'a' and <= 'f' => c - 'a' + 10,
        >= 'A' and <= 'F' => c - 'A' + 10,
        _ => 0
    };
}
