namespace RobustoClient.ImGui.Theme;

/// <summary>
/// Font sizes for the overlay. These are passed to ImGui per <c>PushFont</c> call at draw time,
/// so changing them takes effect the next frame without rebuilding the font atlas.
/// </summary>
public sealed class ThemeTypography
{
    public float RegularSize { get; init; } = 15.5f;
    public float MonoSize { get; init; } = 14.0f;

    /// <summary>
    /// Header title size. Taller than a body line on purpose: the title row is sized from
    /// <c>TitleRowScale</c> to hold it, so the glyphs sit inside the row rather than overflowing it.
    /// </summary>
    public float TitleSize { get; init; } = 27.0f;

    public float CardTitleSize { get; init; } = 14.5f;

    /// <summary>Independent copy, so palette edits cannot leak back into a shared instance.</summary>
    public ThemeTypography Copy() => (ThemeTypography)MemberwiseClone();
}
