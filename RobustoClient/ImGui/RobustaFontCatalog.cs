using System.Collections.Generic;

namespace RobustoClient.ImGui;

/// <summary>One selectable face: what the theme stores by <see cref="Key"/> and the loader bakes.</summary>
/// <param name="BoldKey">Face used for card titles and other bold runs of the same family.</param>
/// <param name="BroadCoverage">
/// Whether the face covers more than Latin. Text uses a narrow face at the user's risk because
/// ImGui 1.92 does not substitute glyphs across fonts, so a missing codepoint renders as tofu.
/// </param>
/// <param name="TitleOffsetEm">
/// Vertical nudge for the header title, in ems, so the letters sit optically centred in the title row.
/// ImGui centres the line box, but a row of capitals occupies only part of it: the ink centre sits at
/// <c>(ascent + descent - capHeight) / 2</c> above the baseline's line-box centre. The value here is
/// the negation of that, divided by unitsPerEm, read from each face's own <c>hhea</c> and <c>OS/2</c>
/// tables. Boxfont Round needs the largest correction because its cap height equals its ascent while
/// it still reserves 32% of the em below the baseline.
/// </param>
public readonly record struct RobustaFontFace(
    string Key,
    string DisplayName,
    string Path,
    string BoldKey,
    bool BroadCoverage,
    float TitleOffsetEm);

/// <summary>
/// The faces a theme may choose for its title and body text.
/// </summary>
/// <remarks>
/// The list is fixed at startup: <c>LoadFonts</c> bakes every entry into the atlas once, so switching
/// themes repoints the active font pointers instead of rebuilding the atlas mid-session. Adding a
/// face means a new entry here plus a resource, never a runtime rebuild.
/// </remarks>
public static class RobustaFontCatalog
{
    public const string DefaultTitleFont = "BoxfontRound";
    public const string DefaultTextFont = "RobotoMonoBold";

    private static readonly RobustaFontFace[] AllFaces =
    [
        new("RobotoMono", "Roboto Mono", "/Fonts/RobotoMono/RobotoMono-Regular.ttf",
            "RobotoMonoBold", true, -0.0330f),

        new("RobotoMonoBold", "Roboto Mono Bold", "/Fonts/RobotoMono/RobotoMono-Bold.ttf",
            "RobotoMonoBold", true, -0.0330f),

        new("NotoSans", "Noto Sans", "/Fonts/NotoSans/NotoSans-Regular.ttf",
            "NotoSansBold", true, -0.0310f),

        new("NotoSansBold", "Noto Sans Bold", "/Fonts/NotoSans/NotoSans-Bold.ttf",
            "NotoSansBold", true, -0.0310f),

        new("NotoSansDisplay", "Noto Sans Display", "/Fonts/NotoSansDisplay/NotoSansDisplay-Regular.ttf",
            "NotoSansDisplayBold", true, -0.0310f),

        new("NotoSansDisplayBold", "Noto Sans Display Bold", "/Fonts/NotoSansDisplay/NotoSansDisplay-Bold.ttf",
            "NotoSansDisplayBold", true, -0.0310f),

        new("BoxfontRound", "Boxfont Round", "/Fonts/Boxfont-round/Boxfont Round.ttf",
            "BoxfontRound", false, 0.1600f)
    ];

    /// <summary>Every selectable face, in the order the theme editor lists them.</summary>
    public static IReadOnlyList<RobustaFontFace> Faces => AllFaces;

    public static bool TryGet(string? key, out RobustaFontFace face)
    {
        if (!string.IsNullOrEmpty(key))
        {
            foreach (var candidate in AllFaces)
            {
                if (string.Equals(candidate.Key, key, StringComparison.OrdinalIgnoreCase))
                {
                    face = candidate;
                    return true;
                }
            }
        }

        face = default;
        return false;
    }

    /// <summary>Display name for a key, falling back to <paramref name="fallbackKey"/>'s name.</summary>
    public static string DisplayNameOf(string? key, string fallbackKey) =>
        TryGet(key, out var face) ? face.DisplayName
        : TryGet(fallbackKey, out var fallback) ? fallback.DisplayName
        : key ?? string.Empty;

    /// <summary>
    /// Whether the face is safe for body text. False for Latin-only display faces, which would tofu
    /// any Cyrillic or CJK player name in the ESP overlay and the inventory viewer.
    /// </summary>
    public static bool HasBroadCoverage(string? key, string fallbackKey) =>
        TryGet(key, out var face) ? face.BroadCoverage
        : !TryGet(fallbackKey, out var fallback) || fallback.BroadCoverage;

    /// <summary>Title centring nudge in ems for <paramref name="key"/>, falling back like the rest.</summary>
    public static float TitleOffsetOf(string? key, string fallbackKey) =>
        TryGet(key, out var face) ? face.TitleOffsetEm
        : TryGet(fallbackKey, out var fallback) ? fallback.TitleOffsetEm
        : 0f;
}
