using System.Numerics;
using Hexa.NET.ImGui;

namespace RobustoClient.ImGui;

public static class RobustaImGuiWidgets
{
    private static readonly Dictionary<string, float> SwitchAnimProgress = new();

    public static bool ToggleSwitch(string label, ref bool value, string? tooltip = null)
    {
        var drawList = Hexa.NET.ImGui.ImGui.GetWindowDrawList();
        var pos = Hexa.NET.ImGui.ImGui.GetCursorScreenPos();
        float availWidth = Hexa.NET.ImGui.ImGui.GetContentRegionAvail().X;
        float rowHeight = 26f;
        float switchWidth = 38f;
        float switchHeight = 20f;

        Hexa.NET.ImGui.ImGui.InvisibleButton($"##toggle_{label}", new Vector2(MathF.Max(availWidth, switchWidth + 40f), rowHeight));
        bool clicked = Hexa.NET.ImGui.ImGui.IsItemClicked();
        bool hovered = Hexa.NET.ImGui.ImGui.IsItemHovered();

        if (clicked)
            value = !value;

        if (hovered && !string.IsNullOrEmpty(tooltip))
            Hexa.NET.ImGui.ImGui.SetTooltip(tooltip);

        float target = value ? 1f : 0f;
        if (!SwitchAnimProgress.TryGetValue(label, out float t))
            t = target;

        float dt = Hexa.NET.ImGui.ImGui.GetIO().DeltaTime;
        t += (target - t) * MathF.Min(1f, dt * 16f);
        SwitchAnimProgress[label] = t;

        if (hovered)
        {
            drawList.AddRectFilled(
                pos,
                pos + new Vector2(availWidth, rowHeight),
                RobustaImGuiTheme.ColU32(new Vector4(1f, 1f, 1f, 0.03f)),
                5f);
        }

        float textY = pos.Y + (rowHeight - Hexa.NET.ImGui.ImGui.GetTextLineHeight()) * 0.5f;
        drawList.AddText(
            new Vector2(pos.X + 4f, textY),
            RobustaImGuiTheme.ColU32(value ? RobustaImGuiTheme.TextPrimary : RobustaImGuiTheme.TextMuted),
            label);

        var trackMin = new Vector2(pos.X + availWidth - switchWidth - 4f, pos.Y + (rowHeight - switchHeight) * 0.5f);
        var trackMax = trackMin + new Vector2(switchWidth, switchHeight);

        var offColor = hovered ? RobustaImGuiTheme.BgControlHover : RobustaImGuiTheme.BgControl;
        var trackColor = Vector4.Lerp(offColor, RobustaImGuiTheme.AccentViolet, t);
        drawList.AddRectFilled(trackMin, trackMax, RobustaImGuiTheme.ColU32(trackColor), switchHeight * 0.5f);
        drawList.AddRect(trackMin, trackMax, RobustaImGuiTheme.ColU32(RobustaImGuiTheme.BorderSubtle), switchHeight * 0.5f);

        float radius = (switchHeight * 0.5f) - 3f;
        float knobX = trackMin.X + 3f + radius + t * (switchWidth - 2f * (radius + 3f));
        float knobY = trackMin.Y + switchHeight * 0.5f;
        var knobColor = Vector4.Lerp(RobustaImGuiTheme.TextMuted, Vector4.One, t);
        drawList.AddCircleFilled(new Vector2(knobX, knobY), radius, RobustaImGuiTheme.ColU32(knobColor), 20);

        return clicked;
    }

    public static void BeginSectionCard(string id, string title, float height = 0f)
    {
        Hexa.NET.ImGui.ImGui.PushStyleColor(ImGuiCol.ChildBg, RobustaImGuiTheme.BgCard);
        Hexa.NET.ImGui.ImGui.PushStyleVar(ImGuiStyleVar.ChildRounding, 8f);
        Hexa.NET.ImGui.ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(12f, 10f));

        Hexa.NET.ImGui.ImGui.BeginChild(
            id,
            new Vector2(0f, height),
            ImGuiChildFlags.Borders | ImGuiChildFlags.AlwaysUseWindowPadding | (height == 0f ? ImGuiChildFlags.AutoResizeY : ImGuiChildFlags.None));

        if (!RobustaImGuiTheme.FontBold.IsNull)
            Hexa.NET.ImGui.ImGui.PushFont(RobustaImGuiTheme.FontBold, 14.5f);

        Hexa.NET.ImGui.ImGui.TextColored(RobustaImGuiTheme.AccentCyan, title);

        if (!RobustaImGuiTheme.FontBold.IsNull)
            Hexa.NET.ImGui.ImGui.PopFont();

        Hexa.NET.ImGui.ImGui.Separator();
        Hexa.NET.ImGui.ImGui.Spacing();
    }

    public static void EndSectionCard()
    {
        Hexa.NET.ImGui.ImGui.EndChild();
        Hexa.NET.ImGui.ImGui.PopStyleVar(2);
        Hexa.NET.ImGui.ImGui.PopStyleColor();
    }

    public static bool AccentButton(string label, Vector2 size = default, bool active = false)
    {
        var baseCol = active ? RobustaImGuiTheme.AccentViolet : RobustaImGuiTheme.BgControl;
        var hoverCol = active ? RobustaImGuiTheme.AccentVioletHover : RobustaImGuiTheme.BgControlHover;

        bool customPad = PushButtonPaddingIfNeeded(label, size);
        Hexa.NET.ImGui.ImGui.PushStyleColor(ImGuiCol.Button, baseCol);
        Hexa.NET.ImGui.ImGui.PushStyleColor(ImGuiCol.ButtonHovered, hoverCol);
        Hexa.NET.ImGui.ImGui.PushStyleColor(ImGuiCol.ButtonActive, RobustaImGuiTheme.AccentCyan);
        bool pressed = Hexa.NET.ImGui.ImGui.Button(label, size);
        Hexa.NET.ImGui.ImGui.PopStyleColor(3);
        if (customPad)
            Hexa.NET.ImGui.ImGui.PopStyleVar();
        return pressed;
    }

    public static bool DangerButton(string label, Vector2 size = default)
    {
        bool customPad = PushButtonPaddingIfNeeded(label, size);
        Hexa.NET.ImGui.ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.65f, 0.14f, 0.18f, 0.90f));
        Hexa.NET.ImGui.ImGui.PushStyleColor(ImGuiCol.ButtonHovered, RobustaImGuiTheme.AccentRed);
        Hexa.NET.ImGui.ImGui.PushStyleColor(ImGuiCol.ButtonActive, new Vector4(0.95f, 0.30f, 0.30f, 1.0f));
        bool pressed = Hexa.NET.ImGui.ImGui.Button(label, size);
        Hexa.NET.ImGui.ImGui.PopStyleColor(3);
        if (customPad)
            Hexa.NET.ImGui.ImGui.PopStyleVar();
        return pressed;
    }

    private static bool PushButtonPaddingIfNeeded(string label, Vector2 size)
    {
        var style = Hexa.NET.ImGui.ImGui.GetStyle();
        var pad = style.FramePadding;
        bool changed = false;

        if (size.Y > 0f)
        {
            float maxPadY = MathF.Max(1f, (size.Y - Hexa.NET.ImGui.ImGui.GetTextLineHeight()) * 0.5f);
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
            var textSize = Hexa.NET.ImGui.ImGui.CalcTextSize(visibleLabel);
            float maxPadX = MathF.Max(2f, (size.X - textSize.X) * 0.5f);
            if (pad.X > maxPadX)
            {
                pad.X = maxPadX;
                changed = true;
            }
        }

        if (changed)
            Hexa.NET.ImGui.ImGui.PushStyleVar(ImGuiStyleVar.FramePadding, pad);

        return changed;
    }
}
