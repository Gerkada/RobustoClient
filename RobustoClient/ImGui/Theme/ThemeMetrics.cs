namespace RobustoClient.ImGui.Theme;

/// <summary>
/// Style inputs and layout ratios for every Robusta window. Absolute values are ImGui style inputs;
/// the unitless ratios are multiplied by ImGui's runtime font measurements in
/// <see cref="RobustaImGuiScale"/>, so changing a font size scales the whole interface.
/// </summary>
public sealed class ThemeMetrics
{
    // Rounding (absolute: not proportional to anything)
    public float WindowRounding { get; init; } = 10f;
    public float ChildRounding { get; init; } = 8f;
    public float FrameRounding { get; init; } = 6f;
    public float PopupRounding { get; init; } = 8f;
    public float ScrollbarRounding { get; init; } = 6f;
    public float GrabRounding { get; init; } = 6f;

    // ImGui style inputs the layout ratios resolve against, so they stay absolute.
    public float FramePaddingX { get; init; } = 8f;
    public float FramePaddingY { get; init; } = 5f;
    public float CellPaddingX { get; init; } = 8f;
    public float CellPaddingY { get; init; } = 5f;

    public float ItemSpacingX { get; init; } = 8f;
    public float ItemSpacingY { get; init; } = 7f;
    public float ItemInnerSpacingX { get; init; } = 6f;
    public float ItemInnerSpacingY { get; init; } = 4f;

    // Borders
    public float WindowBorderSize { get; init; } = 1f;
    public float ChildBorderSize { get; init; } = 1f;
    public float FrameBorderSize { get; init; } = 1f;
    public float PopupBorderSize { get; init; } = 1f;

    public float ScrollbarSize { get; init; } = 10f;
    public float IndentSpacing { get; init; } = 16f;

    // ---- Layout ratios, unitless ----------------------------------------------------------

    /// <summary>Toggle row vertical padding, as a multiple of <c>GetTextLineHeight()</c>.</summary>
    public float TogglePadScale { get; init; } = 0.11f;

    /// <summary>Button row height, as a multiple of <c>GetFrameHeight()</c>.</summary>
    public float ButtonRowScale { get; init; } = 1.0f;

    /// <summary>Slider row vertical padding, as a multiple of <c>GetTextLineHeight()</c>.</summary>
    public float SliderPadScale { get; init; } = 0.11f;

    /// <summary>Card title row, as a multiple of <c>GetFrameHeight()</c>.</summary>
    public float CardTitleRowScale { get; init; } = 1.0f;

    /// <summary>Button height inside widget cards, as a multiple of <c>GetFrameHeight()</c>.</summary>
    public float CardButtonScale { get; init; } = 1.15f;

    /// <summary>Gap between a card's title hairline and its first control.</summary>
    public float CardTitleGapScale { get; init; } = 0.125f;

    /// <summary>Card collapse control size, as a multiple of the card title row.</summary>
    public float CardCollapseButtonScale { get; init; } = 1.0f;

    /// <summary>Header title row height, as a multiple of <c>GetTextLineHeight()</c>.</summary>
    public float TitleRowScale { get; init; } = 1.85f;

    /// <summary>Header tab row height, deliberately smaller than <see cref="TitleRowScale"/>.</summary>
    public float TabRowScale { get; init; } = 1.30f;

    /// <summary>Gap between stacked cards, as a multiple of <c>GetFrameHeight()</c>.</summary>
    public float GridGapScale { get; init; } = 0.31f;

    /// <summary>Card horizontal padding, as a multiple of <c>GetFrameHeight()</c>.</summary>
    public float CardPaddingScaleX { get; init; } = 0.50f;

    /// <summary>Card vertical padding, as a multiple of <c>GetFrameHeight()</c>.</summary>
    public float CardPaddingScaleY { get; init; } = 0.35f;

    /// <summary>Window content padding, as a multiple of <c>GetFrameHeight()</c>.</summary>
    public float WindowPaddingScale { get; init; } = 0.75f;

    /// <summary>Header icon button size, as a multiple of <c>GetFrameHeight()</c>.</summary>
    public float CloseButtonScale { get; init; } = 0.85f;

    /// <summary>Header logo box side, as a multiple of <c>Typography.TitleSize</c>.</summary>
    public float LogoScale { get; init; } = 1.3f;

    /// <summary>Independent copy, so palette edits cannot leak back into a shared instance.</summary>
    public ThemeMetrics Copy() => (ThemeMetrics)MemberwiseClone();
}
