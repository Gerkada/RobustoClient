using Hexa.NET.ImGui;

namespace RobustoClient.ImGui.Theme;

/// <summary>
/// Declares where every colour token is consumed. <see cref="StyleSlots"/> is applied to the global
/// ImGui style; <see cref="DrawOnly"/> tokens are read straight from <c>Colors</c> by drawing code
/// and therefore update immediately.
/// </summary>
/// <remarks>
/// This exists as data rather than as inline assignments so a test can assert that every token is
/// reachable. A token with no consumer is a picker that does nothing, which is invisible until a user
/// reports it.
/// </remarks>
public static class ThemeStyleMap
{
    /// <summary>Token to the ImGui style slots it feeds. A token may drive several slots.</summary>
    public static readonly IReadOnlyList<(string Token, ImGuiCol[] Slots)> StyleSlots =
    [
        (ThemeColorTokens.TextPrimary, [ImGuiCol.Text]),
        (ThemeColorTokens.TextDisabled, [ImGuiCol.TextDisabled]),
        (ThemeColorTokens.Window, [ImGuiCol.WindowBg]),
        (ThemeColorTokens.Card, [ImGuiCol.ChildBg]),
        (ThemeColorTokens.Popup, [ImGuiCol.PopupBg]),
        (ThemeColorTokens.Border,
        [
            ImGuiCol.Border, ImGuiCol.Separator, ImGuiCol.ResizeGrip,
            ImGuiCol.TableBorderStrong, ImGuiCol.TableBorderLight
        ]),
        (ThemeColorTokens.Control, [ImGuiCol.FrameBg, ImGuiCol.Button]),
        (ThemeColorTokens.ControlHover,
            [ImGuiCol.FrameBgHovered, ImGuiCol.ButtonHovered, ImGuiCol.HeaderHovered]),
        (ThemeColorTokens.ControlActive, [ImGuiCol.FrameBgActive]),
        (ThemeColorTokens.Header,
            [ImGuiCol.TitleBg, ImGuiCol.TitleBgActive, ImGuiCol.TitleBgCollapsed, ImGuiCol.TableHeaderBg]),
        (ThemeColorTokens.Primary,
            [ImGuiCol.ScrollbarGrabActive, ImGuiCol.SliderGrab, ImGuiCol.ButtonActive, ImGuiCol.HeaderActive,
             ImGuiCol.NavCursor]),
        (ThemeColorTokens.PrimaryHover,
            [ImGuiCol.SliderGrabActive, ImGuiCol.SeparatorHovered, ImGuiCol.ResizeGripHovered]),
        (ThemeColorTokens.PrimaryText,
            [ImGuiCol.CheckMark, ImGuiCol.SeparatorActive, ImGuiCol.ResizeGripActive, ImGuiCol.DragDropTarget]),
        (ThemeColorTokens.SelectedBg, [ImGuiCol.Header, ImGuiCol.TextSelectedBg]),
        (ThemeColorTokens.ScrollbarBg, [ImGuiCol.ScrollbarBg]),
        (ThemeColorTokens.ScrollbarGrab, [ImGuiCol.ScrollbarGrab]),
        (ThemeColorTokens.ScrollbarGrabHovered, [ImGuiCol.ScrollbarGrabHovered])
    ];

    /// <summary>
    /// Tokens consumed directly by drawing code. These need no style slot because they are read at
    /// draw time, which is why they already update the instant a picker moves.
    /// </summary>
    public static readonly string[] DrawOnly =
    [
        ThemeColorTokens.TextMuted,
        ThemeColorTokens.TabStrip,
        ThemeColorTokens.TabHover,
        ThemeColorTokens.TabSelectedBg,
        ThemeColorTokens.TabText,
        ThemeColorTokens.TabTextSelected,
        ThemeColorTokens.Outline,
        ThemeColorTokens.Secondary,
        ThemeColorTokens.SecondaryHover,
        ThemeColorTokens.OnPrimary,
        ThemeColorTokens.OnSecondary,
        ThemeColorTokens.SelectedAccent,
        ThemeColorTokens.Success,
        ThemeColorTokens.Warning,
        ThemeColorTokens.Danger,
        ThemeColorTokens.Info,
        ThemeColorTokens.RadarBgOuter,
        ThemeColorTokens.RadarBgInner,
        ThemeColorTokens.RadarRing,
        ThemeColorTokens.RadarGrid,
        ThemeColorTokens.SeveritySyndicate,
        ThemeColorTokens.SeverityGrandTheft,
        ThemeColorTokens.SeverityMajor,
        ThemeColorTokens.SeverityRestricted,
        ThemeColorTokens.SeverityMinor,
        ThemeColorTokens.SeverityUnknown
    ];
}
