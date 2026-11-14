using System.Numerics;

namespace RobustoClient.ImGui.Theme;

/// <summary>A complete, immutable look for every Robusta window.</summary>
public sealed class RobustaThemePalette
{
    public required string Name { get; init; }
    public required ThemeColors Colors { get; init; }
    public required ThemeMetrics Metrics { get; init; }
    public required ThemeTypography Typography { get; init; }

    /// <summary>Size the cheat menu opens at on first use.</summary>
    public Vector2 DefaultWindowSize { get; init; } = new(450f, 600f);

    /// <summary>Lower bound the user can resize to. Wide enough for the longest tab label.</summary>
    public Vector2 MinWindowSize { get; init; } = new(380f, 420f);

    /// <summary>
    /// Face for the window title, keyed into <see cref="RobustaFontCatalog"/>. The title only ever
    /// draws the product name, so a Latin-only display face is safe here.
    /// </summary>
    public string TitleFont { get; init; } = RobustaFontCatalog.DefaultTitleFont;

    /// <summary>Face for every other string in every Robusta window.</summary>
    public string TextFont { get; init; } = RobustaFontCatalog.DefaultTextFont;

    /// <summary>True when loaded from a user file rather than a built-in resource.</summary>
    public bool IsUserTheme { get; init; }

    /// <summary>
    /// Independent copy, so the theme editor can mutate the active palette without rewriting a
    /// shared built-in instance. The section objects are immutable apart from reflection writes, so
    /// they are rebuilt rather than shared.
    /// </summary>
    public RobustaThemePalette Copy() => new()
    {
        Name = Name,
        Colors = Colors.Copy(),
        Metrics = Metrics.Copy(),
        Typography = Typography.Copy(),
        DefaultWindowSize = DefaultWindowSize,
        MinWindowSize = MinWindowSize,
        TitleFont = TitleFont,
        TextFont = TextFont,
        IsUserTheme = IsUserTheme
    };
}
