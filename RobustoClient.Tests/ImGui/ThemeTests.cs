using System;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Reflection;
using FluentAssertions;
using RobustoClient.ImGui;
using RobustoClient.ImGui.Theme;
using RobustoClient.UI.ImGui;
using Xunit;

namespace RobustoClient.Tests.ImGui;

/// <summary>
/// Guards the two properties that make the palette trustworthy: every built-in theme defines the
/// full token set, and the role pairings that carry text stay above WCAG AA.
/// </summary>
public class ThemeTests
{
    private static RobustaThemePalette Dark => ThemeDefaults.Dark();

    [Fact]
    public void LayoutScales_ArePositive()
    {
        // Every layout size is a ratio against ImGui's runtime metrics. A zero or negative ratio
        // would collapse a band or a row to nothing.
        foreach (var palette in RobustaImGuiTheme.Builtins)
        {
            var m = palette.Metrics;

            m.TitleRowScale.Should().BePositive($"{palette.Name} title row");
            m.TabRowScale.Should().BePositive($"{palette.Name} tab row");
            m.TogglePadScale.Should().BePositive($"{palette.Name} toggle pad");
            m.ButtonRowScale.Should().BePositive($"{palette.Name} button row");
            m.SliderPadScale.Should().BePositive($"{palette.Name} slider pad");
            m.CardTitleRowScale.Should().BePositive($"{palette.Name} card title row");
            m.CardButtonScale.Should().BePositive($"{palette.Name} card button");
            m.CardCollapseButtonScale.Should().BePositive($"{palette.Name} card collapse button");
            m.CloseButtonScale.Should().BePositive($"{palette.Name} close button");

            m.CardTitleGapScale.Should().BeGreaterThanOrEqualTo(0f);

            m.GridGapScale.Should().BeGreaterThanOrEqualTo(0f);
            m.CardPaddingScaleX.Should().BeGreaterThanOrEqualTo(0f);
            m.CardPaddingScaleY.Should().BeGreaterThanOrEqualTo(0f);
            m.WindowPaddingScale.Should().BeGreaterThanOrEqualTo(0f);
        }
    }

    [Theory]
    [InlineData(12f)]
    [InlineData(15.5f)]
    [InlineData(20f)]
    public void ResolvedLayout_GrowsWithFontAndRespectsFloors(float fontSize)
    {
        // Mirrors RobustaImGuiScale's arithmetic. ImGui's frame height is line height plus twice the
        // frame padding, so this is the same formula the runtime uses.
        var m = Dark.Metrics;
        float line = fontSize * 1.2f;
        float frame = line + m.FramePaddingY * 2f;

        float titleRow = line * m.TitleRowScale;
        float tabRow = MathF.Max(RobustaImGuiScale.MinTargetSize, line * m.TabRowScale);
        float close = MathF.Max(RobustaImGuiScale.MinTargetSize, frame * m.CloseButtonScale);

        titleRow.Should().BePositive();
        tabRow.Should().BeGreaterThanOrEqualTo(RobustaImGuiScale.MinTargetSize,
            "the tab row must stay a legal pointer target at every font size");
        close.Should().BeGreaterThanOrEqualTo(RobustaImGuiScale.MinTargetSize,
            "chrome buttons must stay a legal pointer target at every font size");
    }

    [Fact]
    public void ItemSpacing_IsSmallerThanReservedBands()
    {
        // The header reserves (height - ItemSpacing.Y) so the next band sits flush. Checked against
        // a representative font size rather than a stored pixel height.
        const float line = 15.5f * 1.2f;
        var m = Dark.Metrics;

        m.ItemSpacingY.Should().BeLessThan(line * m.TitleRowScale);
        m.ItemSpacingY.Should().BeLessThan(MathF.Max(RobustaImGuiScale.MinTargetSize, line * m.TabRowScale));
    }

    public static TheoryData<string> AllTokens()
    {
        var data = new TheoryData<string>();
        foreach (var token in ThemeColorTokens.All)
            data.Add(token);
        return data;
    }

    [Fact]
    public void TokenRegistry_CoversEveryColorProperty()
    {
        var properties = ThemeColorTokens.AllProperties().Select(p => p.Token).OrderBy(t => t);
        var declared = ThemeColorTokens.All.OrderBy(t => t);

        properties.Should().BeEquivalentTo(declared,
            "a ThemeColors property without a token would be unreachable from JSON");
    }

    [Theory]
    [MemberData(nameof(AllTokens))]
    public void EveryToken_ResolvesToAProperty(string token)
    {
        ThemeColorTokens.TryGetProperty(token, out var prop).Should().BeTrue();
        prop.PropertyType.Should().Be(typeof(Vector4));
    }

    [Fact]
    public void ThemeSectionHeadings_AreUnique()
    {
        // ImGui derives a widget id from its label, so two sections sharing a heading open and close
        // as one. The TEXT SIZES / TEXT COLORS split exists because both were once called "TEXT".
        var fixedSections = CheatMenuImGui.FixedThemeSections;
        var colourSections = ThemeColorTokens.Sections.Select(s => s.Section);

        fixedSections.Concat(colourSections)
            .Should().OnlyHaveUniqueItems("a duplicate heading is also a duplicate ImGui id");
    }

    [Fact]
    public void ThemeSectionHeadings_AreUpperCase()
    {
        // Matches the existing THEMES tab convention; the check also catches a stray space or case
        // change that would make two headings differ only by formatting.
        foreach (var heading in CheatMenuImGui.FixedThemeSections.Concat(ThemeColorTokens.Sections.Select(s => s.Section)))
        {
            heading.Should().Be(heading.ToUpperInvariant(), $"'{heading}' should be upper case");
            heading.Should().Be(heading.Trim());
        }
    }

    [Fact]
    public void Sections_CoverEveryTokenExactlyOnce()
    {
        var fromSections = ThemeColorTokens.Sections.SelectMany(s => s.Tokens).ToList();

        fromSections.Should().OnlyHaveUniqueItems();
        fromSections.Should().BeEquivalentTo(ThemeColorTokens.All);
    }

    [Fact]
    public void Builtins_AreNamedDark()
    {
        Dark.Name.Should().Be("Dark");
        RobustaImGuiTheme.Builtins.Should().ContainSingle(t => t.Name == "Dark");
    }

    [Fact]
    public void DarkDefaults_MatchAuthoredPalette()
    {
        // Pins the palette the user authored and exported as new-dark-theme. If these drift, the
        // shipped Dark theme no longer matches the look it was tuned against.
        var dark = ThemeDefaults.Dark();
        var json = RobustaImGuiTheme.Builtins.First(t => t.Name == "Dark");

        var cases = new (string Token, Vector4 Expected)[]
        {
            ("window", Hex("#1A1A1A")),
            ("header", Hex("#1A1A1A")),
            ("tabStrip", Hex("#161616")),
            ("card", Hex("#161616")),
            ("primary", Hex("#8E8D91")),
            ("selectedBg", Hex("#444345")),
            ("border", Hex("#2A2A2A")),
            ("outline", Hex("#212121")),
            ("textPrimary", Hex("#D3D3D3")),
            ("scrollbarGrab", Hex("#272626")),
            ("tabSelectedBg", Hex("#080303"))
        };

        foreach (var (token, expected) in cases)
        {
            ThemeColorTokens.TryGetProperty(token, out var prop).Should().BeTrue(token);

            foreach (var palette in new[] { dark, json })
            {
                var actual = (Vector4)prop.GetValue(palette.Colors)!;
                actual.X.Should().BeApproximately(expected.X, 1f / 255f, $"{palette.Name}.{token} R");
                actual.Y.Should().BeApproximately(expected.Y, 1f / 255f, $"{palette.Name}.{token} G");
                actual.Z.Should().BeApproximately(expected.Z, 1f / 255f, $"{palette.Name}.{token} B");
            }
        }

        dark.Typography.RegularSize.Should().Be(14f);
        dark.Typography.CardTitleSize.Should().Be(18f);
        dark.Typography.TitleSize.Should().Be(15f);
        dark.Metrics.WindowRounding.Should().Be(0f);
        dark.Metrics.TitleRowScale.Should().Be(2f);
        dark.Metrics.TabRowScale.Should().Be(1.5f);

        // 15 * 2.3 lands the logo box on 34.5px, which snaps to 2x the 17px-wide art.
        dark.Metrics.LogoScale.Should().Be(2.3f);
        dark.DefaultWindowSize.Should().Be(new Vector2(380f, 500f));
    }

    [Fact]
    public void DarkDefaults_UseTheBoxfontTitleFace()
    {
        var dark = ThemeDefaults.Dark();
        var json = RobustaImGuiTheme.Builtins.First(t => t.Name == "Dark");

        foreach (var palette in new[] { dark, json })
        {
            palette.TitleFont.Should().Be(RobustaFontCatalog.DefaultTitleFont, palette.Name);
            palette.TextFont.Should().Be(RobustaFontCatalog.DefaultTextFont, palette.Name);
        }
    }

    [Fact]
    public void BuiltinJson_MetricKeysAllMapToProperties()
    {
        // ThemeJson only warns about an unrecognised key, so a renamed or misspelled metric silently
        // falls back to the compiled default and the file appears to do nothing. This catches that.
        var properties = typeof(ThemeMetrics)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(p => p.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var palette in RobustaImGuiTheme.Builtins)
        {
            var json = ReadBuiltinJson($"RobustoClient.Theme.{palette.Name}.json");

            var keys = json["metrics"]!.AsObject().Select(kv => kv.Key).ToList();
            keys.Should().NotBeEmpty();

            foreach (var key in keys)
            {
                properties.Should().Contain(key,
                    $"'{palette.Name}.{key}' must match a ThemeMetrics property or it is ignored");
            }
        }
    }

    private static System.Text.Json.Nodes.JsonObject ReadBuiltinJson(string resourceName)
    {
        using var stream = typeof(RobustaThemePalette).Assembly.GetManifestResourceStream(resourceName);
        stream.Should().NotBeNull($"'{resourceName}' must be embedded");

        using var reader = new StreamReader(stream!);
        return System.Text.Json.Nodes.JsonNode.Parse(reader.ReadToEnd())!.AsObject();
    }

    [Fact]
    public void DarkJson_MetricsAndTypographyMatchCompiledDefaults()
    {
        // ThemeJson reflects property names, so a key cased differently from the C# property is
        // silently ignored and the fallback wins. That makes the two representations disagree with
        // no error, which is exactly how a mistyped key survives review.
        var compiled = ThemeDefaults.Dark();
        var json = RobustaImGuiTheme.Builtins.First(t => t.Name == "Dark");

        json.Metrics.Should().BeEquivalentTo(compiled.Metrics,
            "Builtin/Dark.json must agree with ThemeDefaults.Dark() on every metric");
        json.Typography.Should().BeEquivalentTo(compiled.Typography,
            "Builtin/Dark.json must agree with ThemeDefaults.Dark() on every font size");
        json.DefaultWindowSize.Should().Be(compiled.DefaultWindowSize);
        json.MinWindowSize.Should().Be(compiled.MinWindowSize);
        json.TitleFont.Should().Be(compiled.TitleFont);
        json.TextFont.Should().Be(compiled.TextFont);
    }

    [Fact]
    public void EveryColorToken_IsReachable()
    {
        // A token with no consumer is a colour picker that silently does nothing. Splitting the
        // tokens into "feeds an ImGui style slot" and "read at draw time" makes that impossible to
        // ship unnoticed: both sets must together cover the registry exactly once.
        var styled = ThemeStyleMap.StyleSlots.Select(s => s.Token).ToList();
        var drawOnly = ThemeStyleMap.DrawOnly.ToList();

        styled.Should().OnlyHaveUniqueItems("a token should be listed once, with all its slots");
        drawOnly.Should().OnlyHaveUniqueItems();

        var covered = styled.Concat(drawOnly).ToList();
        covered.Should().OnlyHaveUniqueItems("a token must not be both style-driven and draw-only");
        covered.Should().BeEquivalentTo(ThemeColorTokens.All,
            "every token must be reachable from ApplyStyle or from drawing code");
    }

    [Fact]
    public void StyleSlots_ResolveToRealTokensAndProperties()
    {
        foreach (var (token, slots) in ThemeStyleMap.StyleSlots)
        {
            ThemeColorTokens.TryGetProperty(token, out _).Should().BeTrue($"'{token}' is not a colour token");
            slots.Should().NotBeEmpty($"'{token}' claims a style slot but lists none");
        }

        foreach (var token in ThemeStyleMap.DrawOnly)
            ThemeColorTokens.TryGetProperty(token, out _).Should().BeTrue($"'{token}' is not a colour token");
    }

    [Fact]
    public void Logo_EmbeddedResourceIsAValidPng()
    {
        // The header logo is embedded so it cannot fail on a VFS path or load order. This checks the
        // resource actually made it into the assembly and is a decodable PNG, which is the failure
        // that made the icon invisible before.
        const string resourceName = "RobustoClient.Assets.ClownLogo.png";

        using var stream = typeof(ThemeTests).Assembly
            .GetType()!
            .Assembly
            .GetManifestResourceStream(resourceName);

        // The resource lives in the mod assembly, not the test assembly.
        stream.Should().BeNull("the logo belongs to RobustoClient, not the test assembly");

        using var logo = typeof(RobustaThemePalette).Assembly.GetManifestResourceStream(resourceName);
        logo.Should().NotBeNull($"'{resourceName}' must be embedded in RobustoClient.dll");

        var bytes = new byte[logo!.Length];
        logo.ReadExactly(bytes);

        bytes.Length.Should().BeGreaterThan(64);
        bytes[0].Should().Be(0x89);
        bytes[1].Should().Be((byte)'P');
        bytes[2].Should().Be((byte)'N');
        bytes[3].Should().Be((byte)'G');

        // IHDR width and height are big-endian 32-bit values at offsets 16 and 20.
        int width = (bytes[16] << 24) | (bytes[17] << 16) | (bytes[18] << 8) | bytes[19];
        int height = (bytes[20] << 24) | (bytes[21] << 16) | (bytes[22] << 8) | bytes[23];

        width.Should().BePositive();
        height.Should().BePositive();
    }

    [Fact]
    public void DefaultLogoScale_KeepsTheArtAtTwoTimesSourceSize()
    {
        // LogoBox is TitleSize * LogoScale, snapped to whole multiples of the 17px-wide art. At title
        // 15 the old 1.5 scale gives a 22.5px box, which snaps to 1x and collapses the logo to 17x9.
        // That is the "logo got smaller" regression; the default must hold it at 2x (34x18), which is
        // also the size it had before the title shrank.
        var dark = ThemeDefaults.Dark();
        float box = dark.Typography.TitleSize * dark.Metrics.LogoScale;

        RobustaImGuiLogo.SnapMultiple(box, 17f).Should().Be(2);
    }

    [Theory]
    [InlineData(13.0f, 17f, 1)]   // LogoScale 0.5 at title 26
    [InlineData(26.0f, 17f, 2)]   // 1.0
    [InlineData(33.8f, 17f, 2)]   // 1.3, the default
    [InlineData(52.0f, 17f, 3)]   // 2.0
    [InlineData(65.0f, 17f, 4)]   // 2.5
    [InlineData(104.0f, 17f, 6)]  // 4.0
    public void SnapMultiple_QuantisesToWholeMultiples(float desiredWidth, float artWidth, int expected)
    {
        // Nearest sampling is only crisp at whole-number scales, so the drawn size is quantised.
        // These are the sizes the slider actually produces at TitleSize 26 with the 17px-wide art.
        RobustaImGuiLogo.SnapMultiple(desiredWidth, artWidth).Should().Be(expected);
    }

    [Theory]
    [InlineData(0f, 17f)]
    [InlineData(5f, 0f)]
    [InlineData(5f, -1f)]
    public void SnapMultiple_NeverReturnsLessThanOne(float desiredWidth, float artWidth)
    {
        // A zero multiple would draw nothing, and a non-positive art width must not divide by zero.
        RobustaImGuiLogo.SnapMultiple(desiredWidth, artWidth).Should().Be(1);
    }

    [Fact]
    public void SnapMultiple_NonFiniteWidthFallsBackToOne()
    {
        RobustaImGuiLogo.SnapMultiple(float.NaN, 17f).Should().Be(1);
        RobustaImGuiLogo.SnapMultiple(float.PositiveInfinity, 17f).Should().Be(1);
    }

    [Theory]
    [InlineData(12f)]
    [InlineData(15.5f)]
    [InlineData(20f)]
    public void CompactRows_HugTheTextHeight(float fontSize)
    {
        // Toggle and slider rows are one line of text plus small padding, not a multiple of the
        // taller frame height. This is the property that made them shrink.
        var m = Dark.Metrics;
        float line = fontSize * 1.2f;
        float frame = line + m.FramePaddingY * 2f;

        float toggle = line * (1f + 2f * m.TogglePadScale);
        float slider = line * (1f + 2f * m.SliderPadScale);

        toggle.Should().BeLessThan(frame, "a toggle row must not be taller than a standard control");
        slider.Should().BeLessThan(frame, "a slider row is now a single line, so it must not exceed a control");

        (toggle - line).Should().BeLessThan(line * 0.3f, "padding must stay small relative to the text");
        (slider - line).Should().BeLessThan(line * 0.3f, "padding must stay small relative to the text");
    }

    [Fact]
    public void ToggleAndSliderRows_AreTheSameHeight()
    {
        // Both are one-line rows with identical padding, so they should line up in a card.
        var m = Dark.Metrics;
        m.TogglePadScale.Should().Be(m.SliderPadScale);
    }

    [Fact]
    public void CardButton_IsTallerThanAStandardControl()
    {
        // Card buttons were reported as too small, so CardButtonScale exists to size them
        // independently of the Binds tab's ButtonRowScale. Guard that it actually makes them bigger
        // and keeps them above the pointer-target floor.
        var m = Dark.Metrics;
        float frame = 15.5f * 1.2f + m.FramePaddingY * 2f;

        float cardButton = frame * m.CardButtonScale;
        float standardRow = frame * m.ButtonRowScale;

        cardButton.Should().BeGreaterThan(standardRow, "card buttons are meant to be larger");
        cardButton.Should().BeGreaterThanOrEqualTo(RobustaImGuiScale.MinTargetSize);
    }

    [Fact]
    public void CardCollapseButton_RespectsPointerTargetFloor()
    {
        // The collapse control scales with the card title row but must never fall below the 24px
        // pointer-target minimum, which is what makes the floor explicit rather than implicit.
        var m = Dark.Metrics;
        float frame = 15.5f * 1.2f + m.FramePaddingY * 2f;
        float titleRow = frame * m.CardTitleRowScale;

        MathF.Max(RobustaImGuiScale.MinTargetSize, titleRow * m.CardCollapseButtonScale)
            .Should().BeGreaterThanOrEqualTo(RobustaImGuiScale.MinTargetSize);
    }

    [Fact]
    public void WindowSize_IsPositiveAndAboveMinimum()
    {
        foreach (var palette in RobustaImGuiTheme.Builtins)
        {
            palette.DefaultWindowSize.X.Should().BeGreaterThan(0f, palette.Name);
            palette.DefaultWindowSize.Y.Should().BeGreaterThan(0f, palette.Name);
            palette.MinWindowSize.X.Should().BeGreaterThan(0f, palette.Name);
            palette.MinWindowSize.Y.Should().BeGreaterThan(0f, palette.Name);

            palette.DefaultWindowSize.X.Should().BeGreaterThanOrEqualTo(palette.MinWindowSize.X, palette.Name);
            palette.DefaultWindowSize.Y.Should().BeGreaterThanOrEqualTo(palette.MinWindowSize.Y, palette.Name);
        }
    }

    [Theory]
    [MemberData(nameof(AllTokens))]
    public void BuiltinJson_MatchesCompiledDefaults(string token)
    {
        // The JSON files and the C# fallbacks are two representations of the same palette; if they
        // drift, one of them is lying about what the default look is.
        ThemeColorTokens.TryGetProperty(token, out var prop).Should().BeTrue();

        foreach (var (builtin, fallback) in new[]
                 {
                     (RobustaImGuiTheme.Builtins.First(t => t.Name == "Dark"), ThemeDefaults.Dark())
                 })
        {
            var expected = (Vector4)prop.GetValue(fallback.Colors)!;
            var actual = (Vector4)prop.GetValue(builtin.Colors)!;

            actual.X.Should().BeApproximately(expected.X, 1f / 255f, $"{builtin.Name}.{token} R");
            actual.Y.Should().BeApproximately(expected.Y, 1f / 255f, $"{builtin.Name}.{token} G");
            actual.Z.Should().BeApproximately(expected.Z, 1f / 255f, $"{builtin.Name}.{token} B");
            actual.W.Should().BeApproximately(expected.W, 1f / 255f, $"{builtin.Name}.{token} A");
        }
    }

    [Theory]
    [InlineData("textPrimary", "card")]
    [InlineData("textPrimary", "header")]
    [InlineData("textPrimary", "tabStrip")]
    [InlineData("textMuted", "card")]
    [InlineData("textMuted", "tabStrip")]
    [InlineData("textMuted", "window")]
    [InlineData("primaryText", "card")]
    [InlineData("primaryText", "tabStrip")]
    [InlineData("secondary", "card")]
    [InlineData("success", "card")]
    [InlineData("warning", "card")]
    [InlineData("danger", "card")]
    [InlineData("info", "card")]
    [InlineData("onPrimary", "primary")]
    [InlineData("onSecondary", "secondary")]
    public void DarkTextRoles_MeetWcagAa(string foreground, string background)
    {
        AssertContrastAtLeast(Dark, foreground, background, 4.5);
    }

    [Fact]
    public void DarkLineTokens_AreDecorativeHairlines()
    {
        // Both line tokens are authored as whisper-subtle hairlines in the current Dark palette:
        // border #2A2A2A on window #1A1A1A is 1.21:1, outline #212121 on card #161616 is 1.12:1.
        // That is below the 3:1 in WCAG 1.4.11, which governs boundaries a user must perceive to
        // operate a control. This theme separates surfaces by tone and spacing instead, so the lines
        // are decoration. The ratios are pinned so an edit cannot make them *less* visible silently.
        AssertContrastAtLeast(Dark, "border", "window", 1.15);
        AssertContrastAtLeast(Dark, "border", "card", 1.2);
        AssertContrastAtLeast(Dark, "outline", "card", 1.1);
    }

    private static void AssertContrastAtLeast(
        RobustaThemePalette palette,
        string foreground,
        string background,
        double minimum)
    {
        ThemeColorTokens.TryGetProperty(foreground, out var fgProp).Should().BeTrue();
        ThemeColorTokens.TryGetProperty(background, out var bgProp).Should().BeTrue();

        var fg = (Vector4)fgProp.GetValue(palette.Colors)!;
        var bg = (Vector4)bgProp.GetValue(palette.Colors)!;

        Contrast(fg, bg).Should().BeGreaterThanOrEqualTo(minimum,
            $"{palette.Name}: '{foreground}' on '{background}' must reach {minimum}:1");
    }

    /// <summary>WCAG 2.1 relative luminance contrast ratio.</summary>
    internal static double Contrast(Vector4 a, Vector4 b)
    {
        double la = Luminance(a);
        double lb = Luminance(b);
        if (la < lb)
            (la, lb) = (lb, la);
        return (la + 0.05) / (lb + 0.05);
    }

    private static Vector4 Hex(string text)
    {
        ThemeHex.TryParse(text, out var c).Should().BeTrue(text);
        return c;
    }

    internal static double Luminance(Vector4 c)
    {
        static double Channel(float v)
        {
            double d = v;
            return d <= 0.03928 ? d / 12.92 : Math.Pow((d + 0.055) / 1.055, 2.4);
        }

        return 0.2126 * Channel(c.X) + 0.7152 * Channel(c.Y) + 0.0722 * Channel(c.Z);
    }

    [Fact]
    public void TabLabels_FitMinimumWindow()
    {
        // The tab row splits the header into equal shares. Labels are allowed to shrink toward the
        // button width, but never below the legibility floor, so this pins the window minimum to the
        // label set: raise a label's length and this fails rather than the label clipping.
        // Fonts are not loaded in a headless test, so approximate the monospace advance at 0.62 em.
        var tabs = CheatMenuImGui.Tabs;
        tabs.Should().NotBeEmpty();

        foreach (var palette in RobustaImGuiTheme.Builtins)
        {
            float fontSize = palette.Typography.RegularSize;
            float advance = fontSize * 0.62f;

            float margin = palette.Metrics.ItemSpacingX;
            float bracket = advance;
            float usable = palette.MinWindowSize.X - (margin + bracket) * 2f;
            float tabWidth = usable / tabs.Length;
            float textRoom = tabWidth - palette.Metrics.ItemInnerSpacingX * 2f;

            foreach (var (label, _) in tabs)
            {
                float natural = label.Length * advance;
                float scale = natural > textRoom ? textRoom / natural : 1f;

                scale.Should().BeGreaterThanOrEqualTo(
                    CheatMenuImGui.MinTabTextScale,
                    $"{palette.Name}: '{label}' needs {natural:0.0}px of a {textRoom:0.0}px button, " +
                    $"so it would shrink to {scale:0.00}x");
            }
        }
    }

    [Fact]
    public void TabLabels_StayReadableAtDefaultWindow()
    {
        // At the size the menu opens at, labels may shrink slightly but must stay legible. The tab
        // row inherits the frame-wide text size, so raising RegularSize eats into the label width;
        // this pins a floor rather than demanding an exact fit, which a 14px text face cannot meet
        // in a 380px window. TabLabels_FitMinimumWindow covers the hard legibility limit.
        const float minimumScale = 0.9f;

        var tabs = CheatMenuImGui.Tabs;

        foreach (var palette in RobustaImGuiTheme.Builtins)
        {
            float advance = palette.Typography.RegularSize * 0.62f;
            float usable = palette.DefaultWindowSize.X - (palette.Metrics.ItemSpacingX + advance) * 2f;
            float textRoom = usable / tabs.Length - palette.Metrics.ItemInnerSpacingX * 2f;

            foreach (var (label, _) in tabs)
            {
                float natural = label.Length * advance;
                float scale = natural > textRoom ? textRoom / natural : 1f;

                scale.Should().BeGreaterThanOrEqualTo(
                    minimumScale,
                    $"{palette.Name}: '{label}' would shrink to {scale:0.00}x at the default window size");
            }
        }
    }

    [Fact]
    public void TabLabels_AreAscii()
    {
        // RobotoMono has no glyph for the astronomical and box-drawing symbols, and ImGui 1.92 does
        // not substitute across fonts, so a non-ASCII label renders as tofu. Anything decorative
        // must be drawn, not typed.
        foreach (var (label, _) in CheatMenuImGui.Tabs)
        {
            label.Should().MatchRegex("^[ -~]+$",
                "tab labels are rendered as text, so they must stay printable ASCII");
        }
    }
}


/// <summary>
/// Covers hex parsing, JSON round-tripping and the fallback behaviour that keeps a malformed
/// theme file from producing an unusable UI.
/// </summary>
public class ThemeJsonTests
{
    [Theory]
    [InlineData("#0E1016", 0x0E, 0x10, 0x16, 255)]
    [InlineData("0E1016", 0x0E, 0x10, 0x16, 255)]
    [InlineData("#FFFFFFFF", 255, 255, 255, 255)]
    [InlineData("#00000000", 0, 0, 0, 0)]
    public void TryParse_ReadsSixAndEightDigitHex(string text, int r, int g, int b, int a)
    {
        ThemeHex.TryParse(text, out var color).Should().BeTrue();

        MathF.Round(color.X * 255f).Should().Be(r);
        MathF.Round(color.Y * 255f).Should().Be(g);
        MathF.Round(color.Z * 255f).Should().Be(b);
        MathF.Round(color.W * 255f).Should().Be(a);
    }

    [Theory]
    [InlineData("#GGGGGG")]
    [InlineData("#12345")]
    [InlineData("#1234567")]
    [InlineData("not-a-colour")]
    [InlineData("")]
    [InlineData(null)]
    public void TryParse_RejectsInvalidInput(string? text)
    {
        ThemeHex.TryParse(text, out _).Should().BeFalse();
    }

    [Fact]
    public void Format_OmitsAlphaWhenOpaque()
    {
        ThemeHex.TryParse("#0E1016FF", out var color).Should().BeTrue();
        ThemeHex.Format(color).Should().Be("#0E1016");
    }

    [Fact]
    public void Format_KeepsAlphaWhenTranslucent()
    {
        ThemeHex.TryParse("#00000000", out var color).Should().BeTrue();
        ThemeHex.Format(color).Should().Be("#00000000");
    }

    [Theory]
    [InlineData("#0E1016")]
    [InlineData("#0F1420F2")]
    public void ParseThenFormat_PreservesColour(string hex)
    {
        ThemeHex.TryParse(hex, out var color).Should().BeTrue();
        ThemeHex.Format(color).Should().Be(hex);
    }

    [Fact]
    public void WriteThenParse_RoundTripsEveryToken()
    {
        var original = ThemeDefaults.Dark();
        var json = ThemeJson.Write(original);

        var parsed = ThemeJson.Parse(json, ThemeDefaults.Dark());

        parsed.Name.Should().Be(original.Name);
        foreach (var token in ThemeColorTokens.All)
        {
            ThemeColorTokens.TryGetProperty(token, out var prop).Should().BeTrue();
            var expected = (Vector4)prop.GetValue(original.Colors)!;
            var actual = (Vector4)prop.GetValue(parsed.Colors)!;

            actual.X.Should().BeApproximately(expected.X, 1f / 255f, $"token '{token}' R");
            actual.Y.Should().BeApproximately(expected.Y, 1f / 255f, $"token '{token}' G");
            actual.Z.Should().BeApproximately(expected.Z, 1f / 255f, $"token '{token}' B");
            actual.W.Should().BeApproximately(expected.W, 1f / 255f, $"token '{token}' A");
        }
    }

    [Fact]
    public void WriteThenParse_RoundTripsMetricsAndTypography()
    {
        var original = ThemeDefaults.Dark();
        var parsed = ThemeJson.Parse(ThemeJson.Write(original), ThemeDefaults.Dark());

        parsed.Metrics.GridGapScale.Should().Be(original.Metrics.GridGapScale);
        parsed.Metrics.TitleRowScale.Should().Be(original.Metrics.TitleRowScale);
        parsed.Metrics.TabRowScale.Should().Be(original.Metrics.TabRowScale);
        parsed.Typography.RegularSize.Should().Be(original.Typography.RegularSize);
    }

    [Fact]
    public void EveryMetric_RoundTripsThroughJson()
    {
        // Export and import must be lossless for every metric, including ones added later. This is
        // reflection-driven like the serializer itself, so a new metric is covered automatically
        // instead of needing its own test.
        var palette = ThemeDefaults.Dark();
        var metrics = palette.Metrics;

        var props = typeof(ThemeMetrics)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.PropertyType == typeof(float))
            .ToList();

        props.Should().NotBeEmpty();

        // Give each property a distinct value so a copy that silently drops one is visible.
        float next = 1.11f;
        foreach (var prop in props)
        {
            prop.SetValue(metrics, next);
            next = MathF.Round(next + 0.11f, 2);
        }

        var parsed = ThemeJson.Parse(ThemeJson.Write(palette), ThemeDefaults.Dark());

        foreach (var prop in props)
        {
            var expected = (float)prop.GetValue(metrics)!;
            var actual = (float)prop.GetValue(parsed.Metrics)!;

            actual.Should().BeApproximately(expected, 0.01f, $"{prop.Name} must survive export and import");
        }
    }

    [Fact]
    public void LogoScale_IsExportedAndHonoured()
    {
        // The logo size is a user-facing theme setting, so it has to be present in the exported JSON
        // under the exact name the importer looks for, and missing keys must fall back rather than
        // resetting the whole section.
        var palette = ThemeDefaults.Dark();

        var exported = ThemeJson.Write(palette);
        exported.Should().Contain("\"LogoScale\"");

        var parsed = ThemeJson.Parse(exported, ThemeDefaults.Dark());
        parsed.Metrics.LogoScale.Should().BeApproximately(palette.Metrics.LogoScale, 0.01f);

        // An older theme file without the key keeps the fallback instead of zeroing it.
        var legacy = ThemeJson.Parse("""{ "name": "Legacy", "metrics": { "TitleRowScale": 2.0 } }""",
            ThemeDefaults.Dark());
        legacy.Metrics.LogoScale.Should().BeApproximately(ThemeDefaults.Dark().Metrics.LogoScale, 0.01f);
    }

    [Fact]
    public void WindowSize_RoundTripsThroughJson()
    {
        var original = ThemeDefaults.Dark();

        var custom = new RobustaThemePalette
        {
            Name = original.Name,
            Colors = original.Colors,
            Metrics = original.Metrics,
            Typography = original.Typography,
            DefaultWindowSize = new Vector2(412f, 533f),
            MinWindowSize = new Vector2(301f, 277f)
        };

        var parsed = ThemeJson.Parse(ThemeJson.Write(custom), ThemeDefaults.Dark());

        parsed.DefaultWindowSize.Should().Be(new Vector2(412f, 533f));
        parsed.MinWindowSize.Should().Be(new Vector2(301f, 277f));
    }

    [Fact]
    public void Parse_RoundTripsFontKeys()
    {
        // The font choices are theme data like colours and sizes, so exporting and re-importing must
        // not silently drop them back to the defaults.
        var original = ThemeDefaults.Dark();
        var custom = new RobustaThemePalette
        {
            Name = original.Name,
            Colors = original.Colors,
            Metrics = original.Metrics,
            Typography = original.Typography,
            TitleFont = "NotoSansDisplayBold",
            TextFont = "NotoSans"
        };

        var parsed = ThemeJson.Parse(ThemeJson.Write(custom), ThemeDefaults.Dark());

        parsed.TitleFont.Should().Be("NotoSansDisplayBold");
        parsed.TextFont.Should().Be("NotoSans");
    }

    [Fact]
    public void Parse_MissingFontKeys_KeepsFallback()
    {
        var fallback = ThemeDefaults.Dark();
        var parsed = ThemeJson.Parse("""{ "name": "Sparse" }""", fallback);

        parsed.TitleFont.Should().Be(fallback.TitleFont);
        parsed.TextFont.Should().Be(fallback.TextFont);
    }

    [Theory]
    [InlineData("\"defaultWindowSize\": [0, 420]")]
    [InlineData("\"defaultWindowSize\": [360]")]
    [InlineData("\"defaultWindowSize\": \"360x420\"")]
    [InlineData("\"defaultWindowSize\": [-5, -5]")]
    public void Parse_MalformedWindowSize_KeepsFallback(string fragment)
    {
        var fallback = ThemeDefaults.Dark();
        var json = $$"""
            {
              "name": "Broken",
              {{fragment}},
              "colors": {},
              "metrics": {},
              "typography": {}
            }
            """;

        var parsed = ThemeJson.Parse(json, fallback);

        parsed.DefaultWindowSize.Should().Be(fallback.DefaultWindowSize);
    }

    [Fact]
    public void Parse_UnknownToken_KeepsFallbackAndDoesNotThrow()
    {
        var json = """{ "name": "X", "colors": { "notARealToken": "#FF0000" } }""";

        var parsed = ThemeJson.Parse(json, ThemeDefaults.Dark());

        parsed.Name.Should().Be("X");
        parsed.Colors.Card.Should().Be(ThemeDefaults.Dark().Colors.Card);
    }

    [Fact]
    public void Parse_MissingTokens_FallBackToDefault()
    {
        var json = """{ "name": "Partial", "colors": { "card": "#123456" } }""";
        var fallback = ThemeDefaults.Dark();

        var parsed = ThemeJson.Parse(json, fallback);

        ThemeHex.Format(parsed.Colors.Card).Should().Be("#123456");
        parsed.Colors.TextPrimary.Should().Be(fallback.Colors.TextPrimary);
        parsed.Metrics.GridGapScale.Should().Be(fallback.Metrics.GridGapScale);
    }

    [Fact]
    public void Parse_MalformedJson_ReturnsFallback()
    {
        var fallback = ThemeDefaults.Dark();

        var parsed = ThemeJson.Parse("{ this is not json ", fallback);

        parsed.Should().BeSameAs(fallback);
    }

    [Fact]
    public void Parse_InvalidColour_KeepsDefault()
    {
        var json = """{ "name": "Bad", "colors": { "card": "not-a-colour" } }""";
        var fallback = ThemeDefaults.Dark();

        var parsed = ThemeJson.Parse(json, fallback);

        parsed.Colors.Card.Should().Be(fallback.Colors.Card);
    }
}


/// <summary>
/// Guards the theme-selectable font faces: the catalogue is the single source of truth for what the
/// theme editor offers and what the loader bakes, so a bad entry silently costs a face.
/// </summary>
public class FontCatalogTests
{
    [Fact]
    public void Catalogue_KeysAreUnique()
    {
        // Keys are what theme JSON stores and what the loader keys its baked atlas on. A duplicate
        // would make one face unreachable and shadow the other.
        RobustaFontCatalog.Faces.Select(f => f.Key).Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public void Catalogue_EveryFaceHasAUsableSource()
    {
        foreach (var face in RobustaFontCatalog.Faces)
        {
            face.DisplayName.Should().NotBeNullOrWhiteSpace(face.Key);

            // Every face comes from the game's resources, so the path must look like one.
            face.Path.Should().StartWith("/", face.Key);
            face.Path.Should().EndWith(".ttf", face.Key);
        }
    }

    [Fact]
    public void Catalogue_DefaultsExist()
    {
        RobustaFontCatalog.TryGet(RobustaFontCatalog.DefaultTitleFont, out var title).Should().BeTrue();
        RobustaFontCatalog.TryGet(RobustaFontCatalog.DefaultTextFont, out var text).Should().BeTrue();

        // Body text must stay broadly covered: it renders server-supplied names, and 1.92 does not
        // substitute glyphs across fonts.
        text.BroadCoverage.Should().BeTrue();
        title.BroadCoverage.Should().BeFalse("the default title face is a Latin-only display face");
    }

    [Fact]
    public void Catalogue_BoldKeysResolve()
    {
        foreach (var face in RobustaFontCatalog.Faces)
            RobustaFontCatalog.TryGet(face.BoldKey, out _).Should().BeTrue($"{face.Key} -> {face.BoldKey}");
    }

    [Fact]
    public void Catalogue_UnknownKeyFallsBack()
    {
        RobustaFontCatalog.TryGet("NoSuchFace", out _).Should().BeFalse();

        RobustaFontCatalog.DisplayNameOf("NoSuchFace", RobustaFontCatalog.DefaultTitleFont)
            .Should().Be(RobustaFontCatalog.DisplayNameOf(RobustaFontCatalog.DefaultTitleFont, ""));
    }

    [Fact]
    public void TitleOffset_IsDerivedFromFontMetrics()
    {
        // The header centres the line box, so an all-caps title drifts by
        // (ascent + descent - capHeight) / 2. Boxfont Round has the worst of it: its cap height equals
        // its ascent while it still reserves 32% of the em below the baseline. These values are read
        // from the faces' own hhea and OS/2 tables; a bad edit here is what puts the title off-centre.
        RobustaFontCatalog.TryGet("BoxfontRound", out var boxfont).Should().BeTrue();
        boxfont.TitleOffsetEm.Should().BeApproximately(0.1600f, 0.002f);

        // A face with ordinary Latin metrics needs almost no correction.
        RobustaFontCatalog.TryGet("RobotoMono", out var roboto).Should().BeTrue();
        MathF.Abs(roboto.TitleOffsetEm).Should().BeLessThan(0.05f);
    }

    [Fact]
    public void TitleOffset_UnknownKeyFallsBackToTheDefaultFace()
    {
        RobustaFontCatalog.TitleOffsetOf("NoSuchFace", RobustaFontCatalog.DefaultTitleFont)
            .Should().Be(RobustaFontCatalog.TitleOffsetOf(RobustaFontCatalog.DefaultTitleFont, ""));
    }

    [Fact]
    public void TitleFace_CoversTheTitleGlyphs()
    {
        // The title is drawn as text, so every letter of the product name must exist in the face.
        // A missing codepoint renders as tofu, and nothing else catches that until it is on screen.
        // The face is game content, so the test reads it from the submodule rather than an embed.
        RobustaFontCatalog.TryGet(RobustaFontCatalog.DefaultTitleFont, out var title).Should().BeTrue();

        var path = Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..",
            "space-station-14", "Resources", title.Path.TrimStart('/').Replace('/', Path.DirectorySeparatorChar));

        File.Exists(path).Should().BeTrue($"'{path}' must exist; a moved upstream font is a real break");

        var bytes = File.ReadAllBytes(path);
        foreach (var c in "ROBUSTO")
            CmapContains(bytes, c).Should().BeTrue($"'{c}' must be mapped in {title.DisplayName}");
    }

    private static int U16(byte[] b, int i) => (b[i] << 8) | b[i + 1];

    private static long U32(byte[] b, int i) =>
        ((long)b[i] << 24) | ((long)b[i + 1] << 16) | ((long)b[i + 2] << 8) | b[i + 3];

    /// <summary>Minimal cmap format-4 lookup: is this codepoint mapped to a real glyph.</summary>
    private static bool CmapContains(byte[] ttf, char codepoint)
    {
        int tableCount = U16(ttf, 4);
        int cmap = -1;

        for (int i = 0; i < tableCount; i++)
        {
            int record = 12 + i * 16;
            if (U32(ttf, record) == 0x636D6170) // 'cmap'
                cmap = (int)U32(ttf, record + 8);
        }

        if (cmap < 0)
            return false;

        int subtableCount = U16(ttf, cmap + 2);
        for (int i = 0; i < subtableCount; i++)
        {
            int entry = cmap + 4 + i * 8;
            int platform = U16(ttf, entry);
            int encoding = U16(ttf, entry + 2);

            // Windows Unicode BMP or full repertoire.
            if (platform != 3 || (encoding != 1 && encoding != 10))
                continue;

            int sub = cmap + (int)U32(ttf, entry + 4);
            if (U16(ttf, sub) != 4)
                continue;

            int segCount = U16(ttf, sub + 6) / 2;
            int endCodes = sub + 14;
            int startCodes = endCodes + segCount * 2 + 2;
            int idDeltas = startCodes + segCount * 2;
            int idRangeOffsets = idDeltas + segCount * 2;

            for (int s = 0; s < segCount; s++)
            {
                int end = U16(ttf, endCodes + s * 2);
                int start = U16(ttf, startCodes + s * 2);
                if (codepoint > end || codepoint < start)
                    continue;

                int delta = (short)U16(ttf, idDeltas + s * 2);
                int rangeOffsetPos = idRangeOffsets + s * 2;
                int rangeOffset = U16(ttf, rangeOffsetPos);

                int glyph;
                if (rangeOffset == 0)
                {
                    glyph = (codepoint + delta) & 0xFFFF;
                }
                else
                {
                    int pos = rangeOffsetPos + rangeOffset + (codepoint - start) * 2;
                    if (pos < 0 || pos + 1 >= ttf.Length)
                        return false;

                    glyph = U16(ttf, pos);
                    if (glyph != 0)
                        glyph = (glyph + delta) & 0xFFFF;
                }

                return glyph != 0;
            }
        }

        return false;
    }
}

