using System.Numerics;
using Hexa.NET.ImGui;
using static Hexa.NET.ImGui.ImGui;
using RobustoClient.ImGui;

namespace RobustoClient.UI.ImGui;

public sealed partial class CheatMenuImGui
{
    /// <summary>
    /// Vertical stack of widget cards, top to bottom.
    /// </summary>
    /// <remarks>
    /// Each card is submitted with a zero width and <see cref="ImGuiChildFlags.AutoResizeY"/>:
    /// width fills the parent's content region (so the stack follows any window resize) and height
    /// follows the card's own content (so nothing can overflow or grow a scrollbar). No absolute
    /// cursor positioning is used, which keeps this clear of ImGui's
    /// <c>ErrorCheckUsingSetCursorPosToExtendParentBoundaries</c> assertion.
    /// </remarks>
    private void DrawCardStack(IReadOnlyList<WidgetCard> cards)
    {
        var m = RobustaImGuiTheme.Metrics;
        PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(RobustaImGuiScale.CardPaddingX, RobustaImGuiScale.CardPaddingY));

        for (int i = 0; i < cards.Count; i++)
        {
            // Dummy already contributes ItemSpacing after itself, so only top up the remainder.
            if (i > 0)
            {
                float extra = MathF.Max(0f, RobustaImGuiScale.GridGap - m.ItemSpacingY);
                if (extra > 0f)
                    Dummy(new Vector2(0f, extra));
            }

            var card = cards[i];
            bool collapsed = card.Collapsible && IsCardCollapsed(card.Id);

            BeginChild(
                card.Id,
                Vector2.Zero,
                ImGuiChildFlags.AutoResizeY | ImGuiChildFlags.AlwaysUseWindowPadding);

            // The title row is captured before drawing so the collapse control can be placed at its
            // right edge without disturbing the layout flow.
            var titleRowMin = GetCursorScreenPos();
            float titleRowHeight = RobustaImGuiScale.CardTitleRow;

            RobustaImGuiWidgets.CardTitle(card.Title);

            if (card.Collapsible)
                DrawCardCollapseControl(card.Id, collapsed, titleRowMin, titleRowHeight);

            // Skipping the body lets AutoResizeY shrink the child to the title alone, so a collapsed
            // card leaves no dead space behind.
            if (!collapsed)
                card.Body();

            EndChild();
        }

        PopStyleVar();
    }

    /// <summary>
    /// Collapse control for one card: a full-height hit box at the right edge of the title row with
    /// an ASCII marker, because the UI font has no chevron glyph.
    /// </summary>
    private void DrawCardCollapseControl(string id, bool collapsed, Vector2 rowMin, float rowHeight)
    {
        float size = RobustaImGuiScale.CardCollapseButton;
        float x = rowMin.X + GetContentRegionAvail().X - size;
        var min = new Vector2(x, rowMin.Y);
        var max = min + new Vector2(size, rowHeight);

        bool hovered = IsMouseHoveringRect(min, max);
        var drawList = GetWindowDrawList();

        if (hovered)
        {
            drawList.AddRectFilled(
                min,
                max,
                RobustaImGuiTheme.ColU32(RobustaImGuiTheme.Colors.ControlHover),
                RobustaImGuiTheme.Metrics.FrameRounding);
        }

        string marker = collapsed ? "+" : "-";
        var textSize = CalcTextSize(marker);
        drawList.AddText(
            new Vector2(min.X + (size - textSize.X) * 0.5f, min.Y + (rowHeight - textSize.Y) * 0.5f),
            RobustaImGuiTheme.ColU32(hovered ? RobustaImGuiTheme.Colors.TextPrimary : RobustaImGuiTheme.Colors.TextMuted),
            marker);

        if (hovered && IsWindowHovered() && IsMouseClicked(ImGuiMouseButton.Left))
            ToggleCardCollapsed(id);
    }

    /// <summary>Full-width section with no outline, used by tabs whose content is not card-shaped.</summary>
    private static void BeginWideSection(string id)
    {
        PushStyleVar(
            ImGuiStyleVar.WindowPadding,
            new Vector2(RobustaImGuiScale.CardPaddingX, RobustaImGuiScale.CardPaddingY));
        BeginChild(id, Vector2.Zero, ImGuiChildFlags.AlwaysUseWindowPadding);
    }

    private static void EndWideSection()
    {
        EndChild();
        PopStyleVar();
    }
}
