using System.IO;
using System.Numerics;
using Hexa.NET.ImGui;
using RobustoClient.ImGui.Theme;
using static Hexa.NET.ImGui.ImGui;

namespace RobustoClient.ImGui;

/// <summary>
/// Owns every visual setting for every Robusta window: colours, spacing metrics and font sizes all
/// come from the active <see cref="RobustaThemePalette"/>, which is loaded from JSON.
/// </summary>
public static partial class RobustaImGuiTheme
{
    private const string DarkResource = "RobustoClient.Theme.Dark.json";

    /// <summary>Active palette. Replaced wholesale by <see cref="SetTheme"/> or <see cref="ApplyPalette"/>.</summary>
    public static RobustaThemePalette Current { get; private set; } = ThemeDefaults.Dark();

    /// <summary>Set when the palette changed and ImGui style needs re-applying before the next frame.</summary>
    public static bool StyleDirty { get; private set; }

    public static ThemeColors Colors => Current.Colors;
    public static ThemeMetrics Metrics => Current.Metrics;
    public static ThemeTypography Typography => Current.Typography;

    private static readonly List<RobustaThemePalette> BuiltinThemes = new();
    private static bool _builtinsLoaded;

    /// <summary>Themes shipped inside the assembly, loaded on first access.</summary>
    public static IReadOnlyList<RobustaThemePalette> Builtins
    {
        get
        {
            EnsureBuiltins();
            return BuiltinThemes;
        }
    }

    private static void EnsureBuiltins()
    {
        if (_builtinsLoaded)
            return;

        _builtinsLoaded = true;

        // Each theme parses on top of its compiled-in default, so a truncated or hand-edited
        // resource still produces a complete palette.
        BuiltinThemes.Add(ThemeJson.LoadEmbedded(DarkResource, ThemeDefaults.Dark()));
    }

    /// <summary>
    /// Installs a palette and schedules a style refresh for the next frame.
    /// </summary>
    /// <remarks>
    /// The palette is copied. <see cref="Builtins"/> hands out shared instances and the theme editor
    /// mutates whatever palette is active through reflection, so without the copy an edit would
    /// rewrite the built-in Dark theme in place and re-selecting it would not restore anything.
    /// </remarks>
    public static void ApplyPalette(RobustaThemePalette palette)
    {
        Current = palette.IsUserTheme ? palette : palette.Copy();
        StyleDirty = true;

        // Fonts are already baked, so a theme switch only repoints the active faces.
        ResolveFonts();
    }

    /// <summary>Activates a built-in theme by name. False when no such theme exists.</summary>
    public static bool SetTheme(string name)
    {
        EnsureBuiltins();

        foreach (var theme in BuiltinThemes)
        {
            if (!string.Equals(theme.Name, name, StringComparison.OrdinalIgnoreCase))
                continue;

            ApplyPalette(theme);
            return true;
        }

        MarseyLogger.Warn($"Theme '{name}' not found; keeping '{Current.Name}'.");
        return false;
    }

    /// <summary>Folder holding user themes, alongside config and the native cache.</summary>
    public static string ThemesDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "RobustoClient",
        "Themes");

    /// <summary>
    /// Applies the theme named by <see cref="RobustaConfig.DefaultTheme"/>: the user file when one
    /// exists, otherwise the built-in theme of that name.
    /// </summary>
    public static void RestoreSelectedTheme()
    {
        var name = RobustaConfig.DefaultTheme;
        if (string.IsNullOrWhiteSpace(name))
            return;

        var path = Path.Combine(ThemesDirectory, name + ".json");
        if (File.Exists(path))
            ApplyPalette(ThemeJson.LoadFile(path, Current));
        else
            SetTheme(name);
    }

    /// <summary>Marks the style as applied. Called by the frame loop after <see cref="ApplyStyle"/>.</summary>
    public static void ClearStyleDirty() => StyleDirty = false;

    /// <summary>Full setup: font atlas plus style.</summary>
    public static void Apply()
    {
        LoadFonts();
        ApplyStyle();
        StyleDirty = false;
    }
}
