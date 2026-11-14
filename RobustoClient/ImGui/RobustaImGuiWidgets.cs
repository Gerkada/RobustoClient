using System.Globalization;
using System.Numerics;
using Hexa.NET.ImGui;
using static Hexa.NET.ImGui.ImGui;

namespace RobustoClient.ImGui;

public static class RobustaImGuiWidgets
{
    private static readonly Dictionary<string, float> ToggleAnimProgress = new();

    /// <summary>
    /// Toggle row with a colour chip in place of the switch: <c>[chip] Label</c>.
    /// </summary>
    /// <remarks>
    /// The chip shows the effective colour and dims while the row is disabled. The label is both the
    /// enable target and the only state indicator: no toggle circle, so the row reads as one control
    /// rather than a colour field beside a switch.
    /// </remarks>
    /// <param name="fallbackColor">Colour to show while no override is set.</param>
    /// <returns>True when the enabled state or the colour changed.</returns>
    public static bool ToggleColorRow(
        string label,
        ref bool enabled,
        ref string hexColor,
        Vector4 fallbackColor,
        bool disabled = false)
    {
        float rowHeight = RobustaImGuiScale.ToggleRow;
        float lineHeight = GetTextLineHeight();
        float chipSize = lineHeight * 0.9f;
        float padY = MathF.Max(1f, (rowHeight - lineHeight) * 0.5f);
        var drawList = GetWindowDrawList();

        // Keep the true colour intact so editing while dimmed never writes the dimmed shade back.
        var editColor = Theme.ThemeHex.TryParse(hexColor, out var parsed) ? parsed : fallbackColor;

        bool colorChanged = false;
        bool enabledChanged = false;

        PushStyleVar(ImGuiStyleVar.FramePadding, new Vector2(0f, padY));
        SetNextItemWidth(chipSize);
        SetCursorPosX(GetCursorPosX() + 2f);

        if (ColorEdit4(
                $"##espcolor_{label}",
                ref editColor,
                ImGuiColorEditFlags.NoInputs | ImGuiColorEditFlags.NoLabel |
                ImGuiColorEditFlags.NoAlpha | ImGuiColorEditFlags.NoDragDrop))
        {
            if (!disabled)
            {
                hexColor = Theme.ThemeHex.Format(editColor);
                colorChanged = true;
            }
        }

        PopStyleVar();

        // Overlay the dim wash post-render so off/disabled rows still look muted without corrupting editColor.
        if (!enabled || disabled)
        {
            var dimTint = disabled
                ? new Vector4(0f, 0f, 0f, 0.45f)
                : new Vector4(RobustaImGuiTheme.BgControl.X, RobustaImGuiTheme.BgControl.Y, RobustaImGuiTheme.BgControl.Z, 0.65f);
            drawList.AddRectFilled(
                GetItemRectMin(),
                GetItemRectMax(),
                RobustaImGuiTheme.ColU32(dimTint),
                RobustaImGuiTheme.Metrics.FrameRounding);
        }

        SameLine();

        // The label is the enable target and the only indicator: no toggle circle, the text itself
        // is the highlight. A chip plus a switch read as two controls for one setting.
        var labelPos = GetCursorScreenPos();
        float labelWidth = MathF.Max(GetContentRegionAvail().X, lineHeight * 4f);

        InvisibleButton($"##esplabel_{label}", new Vector2(labelWidth, rowHeight));
        bool clicked = !disabled && IsItemClicked();
        bool hovered = IsItemHovered() && !disabled;

        if (clicked)
        {
            enabled = !enabled;
            enabledChanged = true;
        }

        if (hovered)
        {
            drawList.AddRectFilled(
                labelPos,
                labelPos + new Vector2(labelWidth, rowHeight),
                RobustaImGuiTheme.ColU32(new Vector4(1f, 1f, 1f, 0.03f)),
                RobustaImGuiTheme.Metrics.FrameRounding);
        }

        var labelColor = enabled && !disabled
            ? RobustaImGuiTheme.TextPrimary
            : RobustaImGuiTheme.TextMuted;

        if (disabled)
            labelColor = Vector4.Lerp(labelColor, RobustaImGuiTheme.BgControl, 0.5f);

        drawList.AddText(
            new Vector2(labelPos.X, labelPos.Y + (rowHeight - lineHeight) * 0.5f),
            RobustaImGuiTheme.ColU32(labelColor),
            label);

        return colorChanged || enabledChanged;
    }

    /// <summary>
    /// Toggle row laid out as <c>[indicator] Label</c>: a square colour indicator followed by the
    /// name, with the whole row as the hit target. Returns true when the value changed.
    /// </summary>
    /// <remarks>
    /// The indicator fill and border lerp from the theme's control and border tokens to its accent
    /// while the value animates, so it follows whichever theme is active rather than hardcoding
    /// colours. A disabled row greys out and swallows clicks.
    /// </remarks>
    public static bool ToggleSwitch(
        string label,
        ref bool value,
        string? tooltip = null,
        bool disabled = false)
    {
        var drawList = GetWindowDrawList();
        var pos = GetCursorScreenPos();
        float availWidth = GetContentRegionAvail().X;
        float rowHeight = RobustaImGuiScale.ToggleRow;
        float lineHeight = GetTextLineHeight();

        // Square and sized from the line height, so the indicator reads as part of the label line.
        float boxSize = lineHeight * 0.8f;
        var boxMin = new Vector2(pos.X + 2f, pos.Y + (rowHeight - boxSize) * 0.5f);
        var boxMax = boxMin + new Vector2(boxSize, boxSize);
        float labelX = boxMax.X + RobustaImGuiTheme.Metrics.ItemInnerSpacingX;

        InvisibleButton($"##toggle_{label}", new Vector2(MathF.Max(availWidth, boxSize + 40f), rowHeight));
        bool clicked = !disabled && IsItemClicked();
        bool hovered = IsItemHovered() && !disabled;

        if (clicked)
            value = !value;

        if (hovered && !string.IsNullOrEmpty(tooltip))
            SetTooltip(tooltip);

        float target = value ? 1f : 0f;
        if (!ToggleAnimProgress.TryGetValue(label, out float t))
            t = target;

        float dt = GetIO().DeltaTime;
        t += (target - t) * MathF.Min(1f, dt * 16f);
        ToggleAnimProgress[label] = t;

        if (hovered)
        {
            drawList.AddRectFilled(
                pos,
                pos + new Vector2(availWidth, rowHeight),
                RobustaImGuiTheme.ColU32(new Vector4(1f, 1f, 1f, 0.03f)),
                RobustaImGuiTheme.Metrics.FrameRounding);
        }

        var offFill = hovered ? RobustaImGuiTheme.BgControlHover : RobustaImGuiTheme.BgControl;
        var fill = Vector4.Lerp(offFill, RobustaImGuiTheme.AccentViolet, t);
        var border = Vector4.Lerp(RobustaImGuiTheme.BorderSubtle, RobustaImGuiTheme.AccentVioletHover, t);
        float rounding = RobustaImGuiTheme.Metrics.FrameRounding;

        // A disabled row keeps its state visible but reads as inactive.
        if (disabled)
        {
            fill = Vector4.Lerp(fill, RobustaImGuiTheme.BgControl, 0.6f);
            border = Vector4.Lerp(border, RobustaImGuiTheme.BgControl, 0.6f);
        }

        drawList.AddRectFilled(boxMin, boxMax, RobustaImGuiTheme.ColU32(fill), rounding);
        drawList.AddRect(boxMin, boxMax, RobustaImGuiTheme.ColU32(border), rounding);

        float textY = pos.Y + (rowHeight - lineHeight) * 0.5f;
        var labelColor = Vector4.Lerp(RobustaImGuiTheme.TextMuted, RobustaImGuiTheme.TextPrimary, t);

        if (disabled)
            labelColor = Vector4.Lerp(labelColor, RobustaImGuiTheme.BgControl, 0.5f);

        drawList.AddText(new Vector2(labelX, textY), RobustaImGuiTheme.ColU32(labelColor), label);

        return clicked;
    }

    /// <summary>
    /// Card heading in the theme's title colour and size. The single place a card title is drawn, so
    /// a card looks the same whichever container renders it.
    /// </summary>
    public static void CardTitle(string title)
    {
        bool hasBold = !RobustaImGuiTheme.FontBold.IsNull;

        if (hasBold)
            PushFont(RobustaImGuiTheme.FontBold, RobustaImGuiTheme.Typography.CardTitleSize);

        TextColored(RobustaImGuiTheme.Colors.PrimaryText, title);

        if (hasBold)
            PopFont();

        // Inner hairline under the heading. Uses the outline token so it stays distinct from the
        // window and table dividers, which use the heavier border token.
        var drawList = GetWindowDrawList();
        var pos = GetCursorScreenPos();
        drawList.AddLine(
            pos,
            new Vector2(pos.X + GetContentRegionAvail().X, pos.Y),
            RobustaImGuiTheme.ColU32(RobustaImGuiTheme.Colors.Outline));

        Dummy(new Vector2(0f, RobustaImGuiScale.CardTitleGap));
    }

    public static void BeginSectionCard(string id, string title, float height = 0f)
    {
        PushStyleColor(ImGuiCol.ChildBg, RobustaImGuiTheme.BgCard);
        PushStyleVar(ImGuiStyleVar.ChildRounding, RobustaImGuiTheme.Metrics.ChildRounding);
        PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(RobustaImGuiScale.CardPaddingX, RobustaImGuiScale.CardPaddingY));

        BeginChild(
            id,
            new Vector2(0f, height),
            ImGuiChildFlags.AlwaysUseWindowPadding | (height == 0f ? ImGuiChildFlags.AutoResizeY : ImGuiChildFlags.None));

        CardTitle(title);

        Spacing();
    }

    public static void EndSectionCard()
    {
        EndChild();
        PopStyleVar(2);
        PopStyleColor();
    }

    /// <summary>Muted secondary label, used for field captions and hints.</summary>
    public static void MutedText(string text) =>
        TextColored(RobustaImGuiTheme.TextMuted, text);

    /// <summary>
    /// Formats a slider reading from an ImGui-style format string such as <c>"%.0f px"</c>.
    /// </summary>
    /// <remarks>
    /// The call sites use ImGui's <c>printf</c>-style specifiers because that is what
    /// <c>SliderFloat</c> takes. <see cref="string.Format"/> is .NET formatting and only substitutes
    /// <c>{0}</c> placeholders, so passing these strings to it returns the pattern literally and the
    /// number never appears. This translates the one specifier form the sliders use and passes the
    /// surrounding text through unchanged.
    /// </remarks>
    internal static string FormatReading(string format, float value)
    {
        int percent = format.IndexOf("%.", StringComparison.Ordinal);
        if (percent < 0)
            return value.ToString(CultureInfo.InvariantCulture);

        int cursor = percent + 2;
        int digitsStart = cursor;
        while (cursor < format.Length && char.IsAsciiDigit(format[cursor]))
            cursor++;

        // Require at least one precision digit and the trailing float specifier.
        if (cursor == digitsStart || cursor >= format.Length ||
            (format[cursor] != 'f' && format[cursor] != 'F'))
        {
            return value.ToString(CultureInfo.InvariantCulture);
        }

        int decimals = int.Parse(
            format.AsSpan(digitsStart, cursor - digitsStart),
            CultureInfo.InvariantCulture);

        string number = value.ToString("F" + decimals, CultureInfo.InvariantCulture);
        return format[..percent] + number + format[(cursor + 1)..];
    }

    /// <summary>
    /// Single-line labelled slider: label on the left, value in a fixed column, bar filling the rest.
    /// Returns true when the value changed. The caller decides how to persist.
    /// </summary>
    /// <remarks>
    /// The reading is drawn after <c>SliderFloat</c> has run so it reflects the value from this frame
    /// rather than the previous one, and the value column is reserved to a fixed width so the bar
    /// does not shift as the number's width changes.
    /// </remarks>
    public static bool SliderRow(
        string label,
        string id,
        ref float value,
        float min,
        float max,
        string format)
    {
        float rowHeight = RobustaImGuiScale.SliderRow;
        float lineHeight = GetTextLineHeight();
        float padY = MathF.Max(1f, (rowHeight - lineHeight) * 0.5f);

        // Widest reading the column must hold, so the bar starts at a stable x.
        float valueWidth = MathF.Max(
            CalcTextSize(FormatReading(format, min)).X,
            CalcTextSize(FormatReading(format, max)).X);

        MutedText(label);
        SameLine();

        // Remember where the value column starts, then reserve it so the slider lays out after it.
        var valuePos = GetCursorScreenPos();
        Dummy(new Vector2(valueWidth, lineHeight));
        SameLine();

        PushStyleVar(
            ImGuiStyleVar.FramePadding,
            new Vector2(RobustaImGuiTheme.Metrics.FramePaddingX, padY));

        // Bar takes whatever width is left. Empty format: the reading is drawn in its own column.
        SetNextItemWidth(-1f);
        bool changed = SliderFloat(id, ref value, min, max, "");

        PopStyleVar();

        // Drawn after the slider so a drag shows the new value in the same frame.
        string reading = FormatReading(format, value);
        GetWindowDrawList().AddText(
            new Vector2(valuePos.X + valueWidth - CalcTextSize(reading).X, valuePos.Y),
            RobustaImGuiTheme.ColU32(RobustaImGuiTheme.TextPrimary),
            reading);

        return changed;
    }

    public static bool AccentButton(string label, Vector2 size = default, bool active = false)
    {
        var baseCol = active ? RobustaImGuiTheme.AccentViolet : RobustaImGuiTheme.BgControl;
        var hoverCol = active ? RobustaImGuiTheme.AccentVioletHover : RobustaImGuiTheme.BgControlHover;

        bool customPad = PushButtonPaddingIfNeeded(label, size);
        PushStyleColor(ImGuiCol.Button, baseCol);
        PushStyleColor(ImGuiCol.ButtonHovered, hoverCol);
        PushStyleColor(ImGuiCol.ButtonActive, RobustaImGuiTheme.AccentCyan);

        // The label colour follows the fill. The accent fill is light, so it needs the paired dark
        // foreground; the neutral control fill is dark and needs the body text colour. Using
        // OnPrimary for both made inactive buttons dark-on-dark.
        PushStyleColor(
            ImGuiCol.Text,
            active ? RobustaImGuiTheme.Colors.OnPrimary : RobustaImGuiTheme.Colors.TextPrimary);

        bool pressed = Button(label, size);
        PopStyleColor(4);
        if (customPad)
            PopStyleVar();
        return pressed;
    }

    public static bool DangerButton(string label, Vector2 size = default)
    {
        var c = RobustaImGuiTheme.Colors;
        bool customPad = PushButtonPaddingIfNeeded(label, size);
        PushStyleColor(ImGuiCol.Button, c.Secondary);
        PushStyleColor(ImGuiCol.ButtonHovered, c.SecondaryHover);
        PushStyleColor(ImGuiCol.ButtonActive, c.Secondary);
        bool pressed = Button(label, size);
        PopStyleColor(3);
        if (customPad)
            PopStyleVar();
        return pressed;
    }

    /// <summary>
    /// Square glyph button drawn at an absolute screen position, for window chrome where the normal
    /// layout flow is not available. Returns true on click.
    /// </summary>
    /// <remarks>Hit testing is a plain rectangle test, so the button needs no ImGui id.</remarks>
    public static bool IconButton(
        Vector2 screenPos,
        float size,
        string glyph,
        Vector4 idleText,
        Vector4 hoverFill,
        Vector4 hoverText) =>
        IconButton(screenPos, new Vector2(size, size), glyph, idleText, hoverFill, hoverText);

    /// <summary>
    /// Icon button with independent width and height. Multi-character glyphs such as <c>[-]</c> need a
    /// wider box than the square chrome buttons.
    /// </summary>
    public static bool IconButton(
        Vector2 screenPos,
        Vector2 size,
        string glyph,
        Vector4 idleText,
        Vector4 hoverFill,
        Vector4 hoverText)
    {
        bool hovered = IsWindowHovered() && IsMouseHoveringRect(screenPos, screenPos + size);
        bool pressed = hovered && IsMouseClicked(ImGuiMouseButton.Left);

        var drawList = GetWindowDrawList();
        if (hovered)
        {
            drawList.AddRectFilled(
                screenPos,
                screenPos + size,
                RobustaImGuiTheme.ColU32(hoverFill),
                RobustaImGuiTheme.Metrics.FrameRounding);
        }

        var textSize = CalcTextSize(glyph);
        var textPos = screenPos + new Vector2((size.X - textSize.X) * 0.5f, (size.Y - textSize.Y) * 0.5f);
        drawList.AddText(textPos, RobustaImGuiTheme.ColU32(hovered ? hoverText : idleText), glyph);

        return pressed;
    }

    private static bool PushButtonPaddingIfNeeded(string label, Vector2 size)
    {
        var style = GetStyle();
        var pad = style.FramePadding;
        bool changed = false;

        if (size.Y > 0f)
        {
            float maxPadY = MathF.Max(1f, (size.Y - GetTextLineHeight()) * 0.5f);
            if (pad.Y > maxPadY)
            {
                pad.Y = maxPadY;
                changed = true;
            }
        }

        if (size.X > 0f)
        {
            int hashIdx = label.IndexOf("##", StringComparison.Ordinal);
            var visibleLabel = hashIdx >= 0 ? label[..hashIdx] : label;
            var textSize = CalcTextSize(visibleLabel);
            float maxPadX = MathF.Max(2f, (size.X - textSize.X) * 0.5f);
            if (pad.X > maxPadX)
            {
                pad.X = maxPadX;
                changed = true;
            }
        }

        if (changed)
            PushStyleVar(ImGuiStyleVar.FramePadding, pad);

        return changed;
    }
}
