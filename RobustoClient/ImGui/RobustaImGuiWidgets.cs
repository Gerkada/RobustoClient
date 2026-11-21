using System.Globalization;
using System.Numerics;
using Hexa.NET.ImGui;
using static Hexa.NET.ImGui.ImGui;

namespace RobustoClient.ImGui;

public static class RobustaImGuiWidgets
{
    private static readonly Dictionary<string, float> ToggleAnimProgress = new();

    /// <summary>The part of a label ImGui would draw: everything before the <c>##</c> id suffix.</summary>
    private static string VisibleLabel(string label)
    {
        int hash = label.IndexOf("##", StringComparison.Ordinal);
        return hash >= 0 ? label[..hash] : label;
    }

    /// <summary>Toggle row with a color chip in place of the switch: <c>[chip] Label</c>.</summary>
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

        // Keep the true color intact so editing while dimmed never writes the dimmed shade back.
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

        // Dim the chip without corrupting editColor.
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

        // The label is the enable target and the only indicator.
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
            VisibleLabel(label));

        return colorChanged || enabledChanged;
    }

    /// <summary>Color row with no on/off state: a chip and a label, with the chip as the only control.</summary>
    /// <param name="id">Unique widget id, so two rows can share a label.</param>
    /// <returns>True when the color changed.</returns>
    public static bool ColorRow(string label, string id, ref string hexColor, Vector4 fallbackColor)
    {
        float rowHeight = RobustaImGuiScale.ToggleRow;
        float lineHeight = GetTextLineHeight();
        float chipSize = lineHeight * 0.9f;
        float padY = MathF.Max(1f, (rowHeight - lineHeight) * 0.5f);

        // Keep the true color intact so an unparseable override falls back without being written back.
        var editColor = Theme.ThemeHex.TryParse(hexColor, out var parsed) ? parsed : fallbackColor;
        bool changed = false;

        PushStyleVar(ImGuiStyleVar.FramePadding, new Vector2(0f, padY));
        SetNextItemWidth(chipSize);
        SetCursorPosX(GetCursorPosX() + 2f);

        if (ColorEdit4(
                $"##colorrow_{id}",
                ref editColor,
                ImGuiColorEditFlags.NoInputs | ImGuiColorEditFlags.NoLabel |
                ImGuiColorEditFlags.NoAlpha | ImGuiColorEditFlags.NoDragDrop))
        {
            hexColor = Theme.ThemeHex.Format(editColor);
            changed = true;
        }

        PopStyleVar();

        SameLine();

        // Same geometry as ToggleColorRow, the invisible item ends the row.
        var labelPos = GetCursorScreenPos();
        float labelWidth = MathF.Max(GetContentRegionAvail().X, lineHeight * 4f);
        InvisibleButton($"##colorrowlabel_{id}", new Vector2(labelWidth, rowHeight));

        GetWindowDrawList().AddText(
            new Vector2(labelPos.X, labelPos.Y + (rowHeight - lineHeight) * 0.5f),
            RobustaImGuiTheme.ColU32(RobustaImGuiTheme.TextPrimary),
            VisibleLabel(label));

        return changed;
    }

    /// <summary>Toggle row laid out as <c>[indicator] Label</c>, returns true when the value changed.</summary>
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

    public static void CardTitle(string title)
    {
        bool hasBold = !RobustaImGuiTheme.FontBold.IsNull;

        if (hasBold)
            PushFont(RobustaImGuiTheme.FontBold, RobustaImGuiTheme.Typography.CardTitleSize);

        TextColored(RobustaImGuiTheme.Colors.PrimaryText, title);

        if (hasBold)
            PopFont();

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
            ImGuiChildFlags.AlwaysUseWindowPadding | (height == 0f ? ImGuiChildFlags.AutoResizeY : ImGuiChildFlags.None),
            height == 0f ? ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse : ImGuiWindowFlags.None);

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

    /// <summary>Wrapped muted secondary label, so long hints stay inside narrow cards.</summary>
    public static void MutedTextWrapped(string text)
    {
        PushStyleColor(ImGuiCol.Text, RobustaImGuiTheme.TextMuted);
        TextWrapped(text);
        PopStyleColor();
    }

    /// <summary>Locates the float specifier in an ImGui-style format string such as <c>"%.1f m"</c>.</summary>
    private static bool TryParseFloatSpec(string format, out int specStart, out int specEnd, out int decimals)
    {
        specStart = specEnd = -1;
        decimals = 0;

        int percent = format.IndexOf("%.", StringComparison.Ordinal);
        if (percent < 0)
            return false;

        int cursor = percent + 2;
        int digitsStart = cursor;
        while (cursor < format.Length && char.IsAsciiDigit(format[cursor]))
            cursor++;

        if (cursor == digitsStart || cursor >= format.Length ||
            (format[cursor] != 'f' && format[cursor] != 'F'))
        {
            return false;
        }

        decimals = int.Parse(
            format.AsSpan(digitsStart, cursor - digitsStart),
            CultureInfo.InvariantCulture);
        specStart = percent;
        specEnd = cursor + 1;
        return true;
    }

    /// <summary>Formats a slider reading from an ImGui-style format string such as <c>"%.0f px"</c>.</summary>
    internal static string FormatReading(string format, float value)
    {
        if (!TryParseFloatSpec(format, out int specStart, out int specEnd, out int decimals))
            return value.ToString(CultureInfo.InvariantCulture);

        string number = value.ToString("F" + decimals, CultureInfo.InvariantCulture);
        return format[..specStart] + number + format[specEnd..];
    }

    /// <summary>
    /// Snaps a slider value to its grid: <paramref name="step"/> when given, otherwise the format's precision.
    /// </summary>
    internal static float SnapToFormat(float value, string format, float min, float max, float step = 0f)
    {
        if (!TryParseFloatSpec(format, out _, out _, out int decimals))
            return value;

        if (step > 0f)
            value = min + MathF.Round((value - min) / step) * step;

        // Rounding the step product again clears the float noise it leaves behind.
        return Math.Clamp(MathF.Round(value, Math.Min(decimals, 6)), min, max);
    }

    /// <summary>
    /// Single-line labelled slider, returns true when the value changed.
    /// </summary>
    public static bool SliderRow(
        string label,
        string id,
        ref float value,
        float min,
        float max,
        string format,
        float step = 0f)
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
        float before = value;
        bool changed = SliderFloat(id, ref value, min, max, "");

        PopStyleVar();

        // A snap that moves the value counts as a change, so an off-grid config value is persisted.
        value = SnapToFormat(value, format, min, max, step);
        changed |= value != before;

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

        // The label color follows the fill. The accent fill is light, so it needs the paired dark
        // foreground, the neutral control fill is dark and needs the body text color. Using
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
            var textSize = CalcTextSize(VisibleLabel(label));
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
