using System.Numerics;
using Hexa.NET.ImGui;
using static Hexa.NET.ImGui.ImGui;
using RobustoClient.ImGui;

namespace RobustoClient.UI.ImGui;

public sealed partial class CheatMenuImGui
{
    // Full labels: at the 380px minimum width each tab still has room for these. Internal so the
    // fit test reads the same source the renderer does.
    internal static readonly (string Label, MenuTab Tab)[] Tabs =
    [
        ("COMBAT", MenuTab.Combat),
        ("VISUALS", MenuTab.Visuals),
        ("UTILITY", MenuTab.Utility),
        ("BINDS", MenuTab.Binds),
        ("THEMES", MenuTab.Themes)
    ];

    /// <summary>
    /// Floor for the per-tab text scale. Below this the label is unreadable, so the window minimum
    /// width is what should give way, not legibility.
    /// </summary>
    internal const float MinTabTextScale = 0.75f;

    /// <summary>
    /// Tab row integrated into the header band. Buttons split the row width equally, so no dead
    /// space appears at the right and every tab scales with the window. Brackets frame the group.
    /// </summary>
    private void DrawTabStrip(Vector2 rowMin, float width, float rowHeight)
    {
        var m = RobustaImGuiTheme.Metrics;
        var c = RobustaImGuiTheme.Colors;
        var drawList = GetWindowDrawList();

        drawList.AddRectFilled(
            rowMin,
            rowMin + new Vector2(width, rowHeight),
            RobustaImGuiTheme.ColU32(c.TabStrip));

        float margin = m.ItemSpacingX;
        float bracketWidth = CalcTextSize("[").X;
        float usable = MathF.Max(0f, width - (margin + bracketWidth) * 2f);
        float tabWidth = usable / Tabs.Length;

        float bracketY = rowMin.Y + (rowHeight - CalcTextSize("[").Y) * 0.5f;
        uint bracketColor = RobustaImGuiTheme.ColU32(c.TextMuted);

        drawList.AddText(new Vector2(rowMin.X + margin, bracketY), bracketColor, "[");
        drawList.AddText(new Vector2(rowMin.X + width - margin - bracketWidth, bracketY), bracketColor, "]");

        // Captured before any PushFont so the per-tab scale multiplies the real current size.
        float baseFontSize = GetFontSize();
        float textRoom = MathF.Max(0f, tabWidth - m.ItemInnerSpacingX * 2f);

        float x = rowMin.X + margin + bracketWidth;

        foreach (var (label, tab) in Tabs)
        {
            var tabMin = new Vector2(x, rowMin.Y);
            var tabMax = new Vector2(x + tabWidth, rowMin.Y + rowHeight);

            // While the header is collapsed nothing is selected: the content area is not on screen,
            // so a highlighted tab would claim a state the window is not in.
            bool selected = !_headerCollapsed && _activeTab == tab;
            bool hovered = IsWindowHovered() && IsMouseHoveringRect(tabMin, tabMax);

            if (selected)
            {
                drawList.AddRectFilled(tabMin, tabMax, RobustaImGuiTheme.ColU32(c.TabSelectedBg));
                drawList.AddRectFilled(
                    new Vector2(tabMin.X, tabMax.Y - MathF.Max(2f, RobustaImGuiScale.LineHeight * 0.12f)),
                    tabMax,
                    RobustaImGuiTheme.ColU32(c.SelectedAccent));
            }
            else if (hovered)
            {
                drawList.AddRectFilled(tabMin, tabMax, RobustaImGuiTheme.ColU32(c.TabHover));
            }

            // Shrink the label toward the button width rather than clipping or truncating it.
            float naturalWidth = CalcTextSize(label).X;
            float scale = naturalWidth > textRoom && naturalWidth > 0f
                ? MathF.Max(MinTabTextScale, textRoom / naturalWidth)
                : 1f;

            bool scaled = scale < 0.999f && !RobustaImGuiTheme.FontRegular.IsNull;
            if (scaled)
                PushFont(RobustaImGuiTheme.FontRegular, baseFontSize * scale);

            var textSize = CalcTextSize(label);
            var textColor = selected ? c.TabTextSelected : hovered ? c.TextPrimary : c.TabText;
            var textPos = new Vector2(
                x + (tabWidth - textSize.X) * 0.5f,
                rowMin.Y + (rowHeight - textSize.Y) * 0.5f);

            drawList.AddText(textPos, RobustaImGuiTheme.ColU32(textColor), label);

            if (scaled)
                PopFont();

            if (hovered && IsMouseClicked(ImGuiMouseButton.Left))
                SelectTab(tab);

            x += tabWidth;
        }
    }
}
