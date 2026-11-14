using Hexa.NET.ImGui;
using static Hexa.NET.ImGui.ImGui;
using RobustoClient.ImGui.Theme;

namespace RobustoClient.ImGui;

/// <summary>
/// Resolves the active theme's unitless layout ratios against ImGui's runtime font measurements.
/// Call these instead of hardcoding pixel sizes: raising <c>Typography.RegularSize</c> then scales
/// every row, gap and band proportionally instead of clipping content.
/// </summary>
/// <remarks>
/// Only valid while an ImGui context exists and a font is pushed; these read live context state.
/// </remarks>
public static class RobustaImGuiScale
{
    /// <summary>Minimum pointer target. An accessibility floor (WCAG 2.5.8), so it does not scale.</summary>
    public const float MinTargetSize = 24f;

    private static ThemeMetrics M => RobustaImGuiTheme.Metrics;

    public static float LineHeight => GetTextLineHeight();
    public static float FrameHeight => GetFrameHeight();
    public static float FrameHeightWithSpacing => GetFrameHeightWithSpacing();

    // Bands
    /// <summary>Title row of the header. Holds the logo, product name and window controls.</summary>
    public static float TitleRowHeight => LineHeight * M.TitleRowScale;

    /// <summary>Tab row of the header, directly beneath the title row.</summary>
    public static float TabRowHeight => MathF.Max(MinTargetSize, LineHeight * M.TabRowScale);

    /// <summary>Whole header band: both rows. This is what the header actually occupies.</summary>
    public static float HeaderHeight => TitleRowHeight + TabRowHeight;

    // Rows
    /// <summary>One line of text plus small vertical padding, so the row hugs its label.</summary>
    public static float ToggleRow => LineHeight * (1f + 2f * M.TogglePadScale);

    public static float ButtonRow => FrameHeight * M.ButtonRowScale;

    /// <summary>Single-line slider row: label, value and bar side by side.</summary>
    public static float SliderRow => LineHeight * (1f + 2f * M.SliderPadScale);

    public static float CardTitleRow => FrameHeight * M.CardTitleRowScale;

    /// <summary>Button height inside widget cards.</summary>
    public static float CardButton => FrameHeight * M.CardButtonScale;

    /// <summary>Gap between a card's title hairline and its first control.</summary>
    public static float CardTitleGap => FrameHeight * M.CardTitleGapScale;

    /// <summary>Card collapse control size, never below the pointer-target minimum.</summary>
    public static float CardCollapseButton =>
        MathF.Max(MinTargetSize, CardTitleRow * M.CardCollapseButtonScale);

    // Gaps and padding
    public static float GridGap => FrameHeight * M.GridGapScale;
    public static float CardPaddingX => FrameHeight * M.CardPaddingScaleX;
    public static float CardPaddingY => FrameHeight * M.CardPaddingScaleY;
    public static float WindowPadding => FrameHeight * M.WindowPaddingScale;

    // Chrome
    public static float CloseButtonSize =>
        MathF.Max(MinTargetSize, FrameHeight * M.CloseButtonScale);

    /// <summary>
    /// Square box the header logo is fitted into. Tracks the title font size so the logo and the
    /// wordmark scale together.
    /// </summary>
    public static float LogoBox => RobustaImGuiTheme.Typography.TitleSize * M.LogoScale;
}
