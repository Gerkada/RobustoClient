using System.Numerics;
using Hexa.NET.ImGui;
using static Hexa.NET.ImGui.ImGui;
using RobustoClient.ImGui;

namespace RobustoClient.UI.ImGui;

public sealed partial class CheatMenuImGui
{
    /// <summary>
    /// Height of the whole header band, plus the title row height for callers that place controls
    /// inside it. Includes any growth needed to fit the logo.
    /// </summary>
    private static float HeaderBandHeight(out float titleRowHeight)
    {
        var m = RobustaImGuiTheme.Metrics;
        titleRowHeight = RobustaImGuiScale.TitleRowHeight;

        // Grow the row to fit the logo rather than clamping the logo to the row: otherwise raising
        // LogoScale past the point where the art fills the row would silently stop having any effect.
        if (RobustaImGuiLogo.IsAvailable)
        {
            float logoHeight = RobustaImGuiLogo.FittedSize(RobustaImGuiScale.LogoBox).Y;

            // Pixel-perfect drawing uses whole multiples, which can be taller than the fitted size.
            var source = RobustaImGuiLogo.SourceSize;
            int multiple = RobustaImGuiLogo.PixelPerfectMultiple(RobustaImGuiScale.LogoBox);
            logoHeight = source.Y * multiple;

            titleRowHeight = MathF.Max(titleRowHeight, logoHeight + m.ItemInnerSpacingY * 2f);
        }

        return titleRowHeight + RobustaImGuiScale.TabRowHeight;
    }

    /// <summary>
    /// Two-row header: product mark with logo on the title row, tab strip integrated beneath it.
    /// Replaces the native title bar, so the title row is also the window's drag handle.
    /// </summary>
    private void DrawHeader()
    {
        var m = RobustaImGuiTheme.Metrics;
        var c = RobustaImGuiTheme.Colors;

        float height = HeaderBandHeight(out float titleRowHeight);
        float tabRowHeight = height - titleRowHeight;

        var origin = GetCursorScreenPos();
        var width = GetContentRegionAvail().X;

        // Reserve the whole band through layout. Dummy() advances the cursor and grows the parent's
        // content bounds; moving the cursor with SetCursorScreenPos to claim space is an assert in
        // ImGui (ErrorCheckUsingSetCursorPosToExtendParentBoundaries), which aborts the process.
        Dummy(new Vector2(width, height - m.ItemSpacingY));

        var drawList = GetWindowDrawList();
        drawList.AddRectFilled(origin, origin + new Vector2(width, height), RobustaImGuiTheme.ColU32(c.Header));
        drawList.AddLine(
            origin + new Vector2(0f, height),
            origin + new Vector2(width, height),
            RobustaImGuiTheme.ColU32(c.Border));

        var titleRowMin = origin;
        var titleRowMax = origin + new Vector2(width, titleRowHeight);

        float cursorX = DrawHeaderLogo(titleRowMin, titleRowHeight);
        DrawHeaderTitle(titleRowMin, titleRowHeight, cursorX);

        // The drag region stops short of the header controls, otherwise a click on close or the
        // theme toggle would also nudge the window. The threshold ignores click jitter.
        float controlsLeft = DrawHeaderRightControls(origin, width, titleRowHeight);
        var dragMin = titleRowMin;
        var dragMax = new Vector2(MathF.Max(dragMin.X, controlsLeft), titleRowMax.Y);

        if (IsMouseHoveringRect(dragMin, dragMax) && IsMouseDragging(ImGuiMouseButton.Left, 3f))
        {
            SetWindowPos(GetWindowPos() + GetIO().MouseDelta);
            SetMouseCursor(ImGuiMouseCursor.Hand);
        }

        // Tab row sits inside the same band, directly beneath the title row.
        DrawTabStrip(new Vector2(origin.X, origin.Y + titleRowHeight), width, tabRowHeight);
    }

    /// <summary>Draws the logo and returns the x coordinate the title should start at.</summary>
    private static float DrawHeaderLogo(Vector2 rowMin, float rowHeight)
    {
        float pad = RobustaImGuiTheme.Metrics.ItemSpacingX * 2f;
        float x = rowMin.X + pad;

        // Pixel-perfect is unconditional: nearest sampling keeps a low-resolution sprite's pixel grid
        // intact instead of blurring it. Cheap no-op once applied, since the filter is texture state.
        RobustaImGuiLogo.ApplyFilter(nearest: true);

        if (!RobustaImGuiLogo.IsAvailable)
            return x;

        // Whole multiples only, so every source texel covers the same number of screen pixels.
        var fitted = RobustaImGuiLogo.FittedSize(RobustaImGuiScale.LogoBox);
        var source = RobustaImGuiLogo.SourceSize;
        int multiple = RobustaImGuiLogo.PixelPerfectMultiple(fitted.X);
        var size = new Vector2(source.X * multiple, source.Y * multiple);

        // Nearest sampling at a fractional origin picks the wrong texel, which reintroduces the
        // uneven blocks the integer scale was meant to avoid.
        var min = new Vector2(
            MathF.Floor(x),
            MathF.Floor(rowMin.Y + (rowHeight - size.Y) * 0.5f));

        var (uvMin, uvMax) = RobustaImGuiLogo.VisibleUv;

        GetWindowDrawList().AddImage(RobustaImGuiLogo.Texture, min, min + size, uvMin, uvMax);

        return min.X + size.X + RobustaImGuiTheme.Metrics.ItemInnerSpacingX;
    }

    private static void DrawHeaderTitle(Vector2 rowMin, float rowHeight, float startX)
    {
        var c = RobustaImGuiTheme.Colors;
        bool hasTitle = !RobustaImGuiTheme.FontTitle.IsNull;

        if (hasTitle)
            PushFont(RobustaImGuiTheme.FontTitle, RobustaImGuiTheme.Typography.TitleSize);

        float textHeight = CalcTextSize("ROBUSTO").Y;

        // ImGui centres the line box; the capitals only fill part of it, so each face carries its own
        // correction (see RobustaFontFace.TitleOffsetEm). Without it an all-caps display face sits
        // noticeably high in the row.
        float baseline = rowMin.Y + (rowHeight - textHeight) * 0.5f
                         + RobustaFontCatalog.TitleOffsetOf(
                             RobustaImGuiTheme.Current.TitleFont,
                             RobustaFontCatalog.DefaultTitleFont) * RobustaImGuiTheme.Typography.TitleSize;

        GetWindowDrawList().AddText(
            new Vector2(startX, baseline),
            RobustaImGuiTheme.ColU32(c.TextPrimary),
            "ROBUSTO");

        if (hasTitle)
            PopFont();
    }

    /// <summary>
    /// Draws the collapse and close controls, returning the x where the controls begin so the drag
    /// region can stop short of them.
    /// </summary>
    private float DrawHeaderRightControls(Vector2 origin, float width, float rowHeight)
    {
        var m = RobustaImGuiTheme.Metrics;
        var c = RobustaImGuiTheme.Colors;
        float size = RobustaImGuiScale.CloseButtonSize;
        float pad = m.ItemSpacingX;
        float y = origin.Y + (rowHeight - size) * 0.5f;

        // Right-aligned: close furthest right, then collapse.
        float closeX = origin.X + width - size - pad;

        // "[-]" is three characters, so it needs a wider box than the square buttons.
        string collapseGlyph = _headerCollapsed ? "[+]" : "[-]";
        float collapseWidth = MathF.Max(size, CalcTextSize(collapseGlyph).X + m.ItemInnerSpacingX * 2f);
        float collapseX = closeX - collapseWidth - m.ItemInnerSpacingX;

        if (RobustaImGuiWidgets.IconButton(new Vector2(closeX, y), size, "X", c.TextMuted, c.Secondary, c.OnSecondary))
            IsOpen = false;

        if (RobustaImGuiWidgets.IconButton(
                new Vector2(collapseX, y),
                new Vector2(collapseWidth, size),
                collapseGlyph,
                c.TextMuted,
                c.ControlHover,
                c.TextPrimary))
        {
            if (_headerCollapsed)
                ExpandHeader();
            else
                CollapseHeader();
        }

        return collapseX;
    }
}
