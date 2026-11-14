using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using Hexa.NET.ImGui;
using static Hexa.NET.ImGui.ImGui;
using RobustoClient.ImGui;
using RobustoClient.ImGui.Theme;

namespace RobustoClient.UI.ImGui;

public sealed partial class CheatMenuImGui
{
    private string _themeName = "";
    private string _themeStatus = "";

    /// <summary>
    /// Headings of the THEMES tab's own sections. Kept here so a test can prove they stay distinct
    /// from the colour section names: a duplicate label is also a duplicate ImGui id, which makes two
    /// sections open and close as one.
    /// </summary>
    internal static readonly string[] FixedThemeSections =
    [
        "FONTS", "WINDOW", "SIZES", "TEXT SIZES", "EXPORT / IMPORT"
    ];

    /// <summary>Displayed in the preset dropdown. Tracks the file name for exported themes, which
    /// carry whatever name was current when they were written.</summary>
    private string? _themeSelection;

    private List<string>? _cachedThemeNames;
    private bool _themePresetComboOpen;

    private void DrawThemesTab()
    {
        BeginWideSection("##ThemesScroll");

        DrawThemePresetPicker();
        DrawWindowSizeSection();
        DrawThemeFontSection();
        DrawThemeColorSections();
        DrawThemeMetricsSection();
        DrawThemeTypographySection();
        DrawThemeIoSection();

        EndWideSection();
    }

    /// <summary>
    /// Title and text face pickers. Both keys are stored on the palette, so they export with the
    /// theme and apply immediately; the atlas already holds every face.
    /// </summary>
    private void DrawThemeFontSection()
    {
        if (!CollapsingHeader(FixedThemeSections[0]))
            return;

        var theme = RobustaImGuiTheme.Current;

        RobustaImGuiWidgets.MutedText("Title");
        DrawFontCombo("##fontTitle", "Title", theme.TitleFont, isTitle: true);

        RobustaImGuiWidgets.MutedText("Text");
        DrawFontCombo("##fontText", "Text", theme.TextFont, isTitle: false);

        // A Latin-only face in the text role renders missing glyphs as tofu: 1.92 does not fall back
        // across fonts here, and names come from the server, so coverage cannot be assumed.
        if (!RobustaFontCatalog.HasBroadCoverage(theme.TextFont, RobustaFontCatalog.DefaultTextFont))
        {
            Spacing();
            RobustaImGuiWidgets.MutedText(
                "This text face covers Latin only; other scripts will render as boxes.");
        }

        Spacing();
    }

    /// <summary>Font picker that writes one palette key, cloning the palette like the size capture does.</summary>
    private void DrawFontCombo(string id, string label, string current, bool isTitle)
    {
        SetNextItemWidth(-1f);

        var fallbackKey = isTitle ? RobustaFontCatalog.DefaultTitleFont : RobustaFontCatalog.DefaultTextFont;
        if (!BeginCombo(id, RobustaFontCatalog.DisplayNameOf(current, fallbackKey)))
            return;

        foreach (var face in RobustaFontCatalog.Faces)
        {
            if (!Selectable(face.DisplayName, face.Key == current))
                continue;

            var theme = RobustaImGuiTheme.Current;

            RobustaImGuiTheme.ApplyPalette(new RobustaThemePalette
            {
                Name = theme.Name,
                Colors = theme.Colors,
                Metrics = theme.Metrics,
                Typography = theme.Typography,
                DefaultWindowSize = theme.DefaultWindowSize,
                MinWindowSize = theme.MinWindowSize,
                TitleFont = isTitle ? face.Key : theme.TitleFont,
                TextFont = isTitle ? theme.TextFont : face.Key,
                IsUserTheme = theme.IsUserTheme
            });

            _themeStatus = $"{label} font set to {face.DisplayName}. Export to keep it.";
        }

        EndCombo();
    }

    /// <summary>
    /// Shows the live window size and lets it be captured as the theme's default, so F4 opens the
    /// menu at the size the author chose rather than a hardcoded constant.
    /// </summary>
    private void DrawWindowSizeSection()
    {
        if (!CollapsingHeader(FixedThemeSections[1]))
            return;

        var current = _windowSize;
        var theme = RobustaImGuiTheme.Current;

        RobustaImGuiWidgets.MutedText(
            $"Current: {current.X:0} x {current.Y:0}");
        RobustaImGuiWidgets.MutedText(
            $"Theme default: {theme.DefaultWindowSize.X:0} x {theme.DefaultWindowSize.Y:0}");
        RobustaImGuiWidgets.MutedText(
            $"Theme minimum: {theme.MinWindowSize.X:0} x {theme.MinWindowSize.Y:0}");

        Spacing();
        if (RobustaImGuiWidgets.AccentButton("Capture current size as default", new Vector2(-1f, RowHeight())))
        {
            var updated = new RobustaThemePalette
            {
                Name = theme.Name,
                Colors = theme.Colors,
                Metrics = theme.Metrics,
                Typography = theme.Typography,
                DefaultWindowSize = current,
                MinWindowSize = theme.MinWindowSize,
                TitleFont = theme.TitleFont,
                TextFont = theme.TextFont,
                IsUserTheme = theme.IsUserTheme
            };

            RobustaImGuiTheme.ApplyPalette(updated);
            _themeStatus = $"Default size set to {current.X:0} x {current.Y:0}. Export to keep it.";
        }

        Spacing();
        RobustaImGuiWidgets.MutedText("Window size is stored per theme and exported with it.");
        Spacing();
    }

    private void DrawThemePresetPicker()
    {
        RobustaImGuiWidgets.MutedText("Preset");
        SetNextItemWidth(-1f);

        // Seeded from the config, not from the active palette: a restored exported theme carries the
        // name of whatever theme was active when it was written, so the file name is the honest label.
        if (string.IsNullOrEmpty(_themeSelection))
        {
            _themeSelection = string.IsNullOrEmpty(RobustaConfig.DefaultTheme)
                ? RobustaImGuiTheme.Current.Name
                : RobustaConfig.DefaultTheme;
        }

        var selection = _themeSelection;

        if (BeginCombo("##themePreset", selection))
        {
            if (!_themePresetComboOpen || _cachedThemeNames == null)
            {
                _themePresetComboOpen = true;
                _cachedThemeNames = AvailableThemeNames();
            }

            foreach (var name in _cachedThemeNames)
            {
                if (!Selectable(name, name == selection))
                    continue;

                if (!ApplyTheme(name))
                    continue;

                _themeSelection = name;
                RobustaConfig.DefaultTheme = name;
                MarkConfigDirty();
                _themeStatus = $"Switched to {name}.";
            }

            EndCombo();
        }
        else
        {
            _themePresetComboOpen = false;
        }

        Spacing();
        if (!string.IsNullOrEmpty(_themeStatus))
        {
            RobustaImGuiWidgets.MutedText(_themeStatus);
            Spacing();
        }
    }

    /// <summary>Built-in names first, then every exported file by its name without extension.</summary>
    private static List<string> AvailableThemeNames()
    {
        var names = RobustaImGuiTheme.Builtins.Select(theme => theme.Name).ToList();

        if (!Directory.Exists(RobustaImGuiTheme.ThemesDirectory))
            return names;

        foreach (var path in Directory.EnumerateFiles(RobustaImGuiTheme.ThemesDirectory, "*.json"))
        {
            var name = Path.GetFileNameWithoutExtension(path);
            if (!names.Contains(name))
                names.Add(name);
        }

        return names;
    }

    /// <summary>Activates a built-in theme or an exported file. False when neither exists.</summary>
    private static bool ApplyTheme(string name)
    {
        if (RobustaImGuiTheme.Builtins.Any(theme => string.Equals(theme.Name, name, StringComparison.OrdinalIgnoreCase)))
            return RobustaImGuiTheme.SetTheme(name);

        if (RobustaImGuiTheme.ResolveThemeFilePath(name) is not { } path || !File.Exists(path))
            return false;

        RobustaImGuiTheme.ApplyPalette(ThemeJson.LoadFile(path, RobustaImGuiTheme.Current));
        return true;
    }

    private void DrawThemeColorSections()
    {
        var colors = RobustaImGuiTheme.Current.Colors;

        foreach (var (section, tokens) in ThemeColorTokens.Sections)
        {
            if (!CollapsingHeader(section))
                continue;

            // Edits are previewed live but never written until the user exports or saves.
            foreach (var token in tokens)
            {
                if (!ThemeColorTokens.TryGetProperty(token, out var prop))
                    continue;

                var value = (Vector4)prop.GetValue(colors)!;
                if (ColorEdit4($"##{token}", ref value, ImGuiColorEditFlags.NoInputs))
                {
                    prop.SetValue(colors, value);

                    // Same contract as DrawFloatSetting: mutate the live palette, then mark the style
                    // dirty so RenderFrameCore re-applies it next frame. Without this, any colour that
                    // reaches the screen through an ImGuiCol slot stays stale until something else
                    // happens to dirty the style.
                    RobustaImGuiTheme.ApplyPalette(RobustaImGuiTheme.Current);
                }

                SameLine();
                RobustaImGuiWidgets.MutedText(token);
            }

            Spacing();
        }
    }

    private void DrawThemeMetricsSection()
    {
        if (!CollapsingHeader(FixedThemeSections[2]))
            return;

        var metrics = RobustaImGuiTheme.Current.Metrics;
        DrawFloatSetting("Title row", "##mTitleRow", metrics.TitleRowScale, 1f, 3f, metrics, nameof(metrics.TitleRowScale));
        DrawFloatSetting("Tab row", "##mTabRow", metrics.TabRowScale, 1f, 3f, metrics, nameof(metrics.TabRowScale));
        DrawFloatSetting("Logo scale", "##mLogo", metrics.LogoScale, 0.5f, 4f, metrics, nameof(metrics.LogoScale));
        DrawFloatSetting("Grid gap scale", "##mGap", metrics.GridGapScale, 0.1f, 2f, metrics, nameof(metrics.GridGapScale));
        DrawFloatSetting("Card padding X", "##mCardX", metrics.CardPaddingScaleX, 0.1f, 2f, metrics, nameof(metrics.CardPaddingScaleX));
        DrawFloatSetting("Card padding Y", "##mCardY", metrics.CardPaddingScaleY, 0.1f, 2f, metrics, nameof(metrics.CardPaddingScaleY));
        DrawFloatSetting("Card button", "##mCardBtn", metrics.CardButtonScale, 0.6f, 2.5f, metrics, nameof(metrics.CardButtonScale));
        DrawFloatSetting("Card title gap", "##mCardGap", metrics.CardTitleGapScale, 0.02f, 0.6f, metrics, nameof(metrics.CardTitleGapScale));
        DrawFloatSetting("Card collapse btn", "##mCardCollapse", metrics.CardCollapseButtonScale, 0.5f, 2.5f, metrics, nameof(metrics.CardCollapseButtonScale));
        DrawFloatSetting("Toggle pad", "##mToggle", metrics.TogglePadScale, 0.02f, 0.4f, metrics, nameof(metrics.TogglePadScale));
        DrawFloatSetting("Slider pad", "##mSlider", metrics.SliderPadScale, 0.02f, 0.4f, metrics, nameof(metrics.SliderPadScale));
        DrawFloatSetting("Window rounding", "##mRound", metrics.WindowRounding, 0f, 24f, metrics, nameof(metrics.WindowRounding));

        Spacing();
        RobustaImGuiWidgets.MutedText(
            $"Resolved: header {RobustaImGuiScale.HeaderHeight:0.#}px " +
            $"(title {RobustaImGuiScale.TitleRowHeight:0.#}, tabs {RobustaImGuiScale.TabRowHeight:0.#}), " +
            $"logo {DescribeLogoSize()}, " +
            $"card btn {RobustaImGuiScale.CardButton:0.#}px, " +
            $"slider row {RobustaImGuiScale.SliderRow:0.#}px");
        Spacing();
    }

    /// <summary>Human-readable logo size for the metrics readout, matching what the header draws.</summary>
    private static string DescribeLogoSize()
    {
        if (!RobustaImGuiLogo.IsAvailable)
            return "hidden";

        float box = RobustaImGuiScale.LogoBox;
        int multiple = RobustaImGuiLogo.PixelPerfectMultiple(box);
        var source = RobustaImGuiLogo.SourceSize;
        return $"{source.X * multiple:0}x{source.Y * multiple:0} ({multiple}x)";
    }

    private void DrawThemeTypographySection()
    {
        if (!CollapsingHeader(FixedThemeSections[3]))
            return;

        var typography = RobustaImGuiTheme.Current.Typography;
        DrawFloatSetting("Regular", "##tReg", typography.RegularSize, 10f, 24f, typography, nameof(typography.RegularSize));
        DrawFloatSetting("Monospace", "##tMono", typography.MonoSize, 10f, 24f, typography, nameof(typography.MonoSize));
        DrawFloatSetting("Title", "##tTitle", typography.TitleSize, 12f, 40f, typography, nameof(typography.TitleSize));
        DrawFloatSetting("Card title", "##tCard", typography.CardTitleSize, 10f, 24f, typography, nameof(typography.CardTitleSize));

        Spacing();
        RobustaImGuiWidgets.MutedText("Sizes apply on the next frame.");
        Spacing();
    }

    /// <summary>
    /// Slider bound to a float property on a theme section. Writes back through reflection so adding
    /// a setting needs no new UI code.
    /// </summary>
    private static void DrawFloatSetting<T>(
        string label,
        string id,
        float current,
        float min,
        float max,
        T section,
        string propertyName) where T : class
    {
        RobustaImGuiWidgets.MutedText(label);
        SetNextItemWidth(-1f);

        float value = current;
        if (!SliderFloat(id, ref value, min, max, "%.1f"))
            return;

        typeof(T).GetProperty(propertyName)?.SetValue(section, value);
        RobustaImGuiTheme.ApplyPalette(RobustaImGuiTheme.Current);
    }

    private void DrawThemeIoSection()
    {
        if (!CollapsingHeader(FixedThemeSections[4]))
            return;

        RobustaImGuiWidgets.MutedText("Themes folder");
        RobustaImGuiWidgets.MutedText(RobustaImGuiTheme.ThemesDirectory);
        Spacing();

        RobustaImGuiWidgets.MutedText("Name");
        SetNextItemWidth(-1f);
        InputTextWithHint("##themeName", "my-theme", ref _themeName, 64);

        float half = (GetContentRegionAvail().X - 8f) * 0.5f;
        var size = new Vector2(half, RowHeight());

        if (RobustaImGuiWidgets.AccentButton("Export", size))
            ExportCurrentTheme();

        SameLine();
        if (RobustaImGuiWidgets.AccentButton("Import", size))
            ImportTheme();

        Spacing();
        RobustaImGuiWidgets.MutedText("Edits are preview-only. Export writes them to disk; closing the menu discards them.");
    }

    private void ExportCurrentTheme()
    {
        if (RobustaImGuiTheme.ResolveThemeFilePath(_themeName) is not { } path)
        {
            _themeStatus = "Enter a name to export under.";
            return;
        }

        try
        {
            ThemeJson.SaveFile(path, RobustaImGuiTheme.Current);
            _themeName = Path.GetFileNameWithoutExtension(path);
            _themeSelection = _themeName;
            _cachedThemeNames = null;
            _themeStatus = $"Exported {_themeName}.";
        }
        catch (Exception ex)
        {
            _themeStatus = $"Export failed: {ex.Message}";
            MarseyLogger.Warn($"[CheatMenu] Theme export failed: {ex.Message}");
        }
    }

    private void ImportTheme()
    {
        if (RobustaImGuiTheme.ResolveThemeFilePath(_themeName) is not { } path)
        {
            _themeStatus = "Enter the name of a theme to import.";
            return;
        }

        if (!File.Exists(path))
        {
            _themeStatus = $"No theme named {_themeName} in the themes folder.";
            return;
        }

        var imported = ThemeJson.LoadFile(path, RobustaImGuiTheme.Current);
        RobustaImGuiTheme.ApplyPalette(imported);
        _themeName = Path.GetFileNameWithoutExtension(path);
        _themeSelection = _themeName;
        _themeStatus = $"Imported {_themeName}.";
    }
}
