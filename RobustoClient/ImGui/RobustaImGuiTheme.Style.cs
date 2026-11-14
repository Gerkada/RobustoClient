using System.Numerics;
using Hexa.NET.ImGui;
using RobustoClient.ImGui.Theme;
using static Hexa.NET.ImGui.ImGui;

namespace RobustoClient.ImGui;

public static partial class RobustaImGuiTheme
{
    /// <summary>Applies the active palette's colours and metrics to the global ImGui style.</summary>
    public static void ApplyStyle()
    {
        var m = Current.Metrics;
        var c = Current.Colors;
        var style = GetStyle();

        style.WindowPadding = new Vector2(RobustaImGuiScale.WindowPadding, RobustaImGuiScale.WindowPadding);
        style.FramePadding = new Vector2(m.FramePaddingX, m.FramePaddingY);
        style.CellPadding = new Vector2(m.CellPaddingX, m.CellPaddingY);
        style.ItemSpacing = new Vector2(m.ItemSpacingX, m.ItemSpacingY);
        style.ItemInnerSpacing = new Vector2(m.ItemInnerSpacingX, m.ItemInnerSpacingY);
        style.IndentSpacing = m.IndentSpacing;
        style.ScrollbarSize = m.ScrollbarSize;
        style.ButtonTextAlign = new Vector2(0.5f, 0.5f);

        style.WindowBorderSize = m.WindowBorderSize;
        style.ChildBorderSize = m.ChildBorderSize;
        style.FrameBorderSize = m.FrameBorderSize;
        style.PopupBorderSize = m.PopupBorderSize;

        style.WindowRounding = m.WindowRounding;
        style.ChildRounding = m.ChildRounding;
        style.FrameRounding = m.FrameRounding;
        style.PopupRounding = m.PopupRounding;
        style.ScrollbarRounding = m.ScrollbarRounding;
        style.GrabRounding = m.GrabRounding;

        var colors = style.Colors;

        // Every token-to-slot assignment lives in ThemeStyleMap so it can be audited; the only
        // literals below are values that have no theme token (a shadow, two row backgrounds, the
        // modal dim).
        foreach (var (token, slots) in ThemeStyleMap.StyleSlots)
        {
            if (!ThemeColorTokens.TryGetProperty(token, out var prop))
                continue;

            var value = (Vector4)prop.GetValue(c)!;
            foreach (var slot in slots)
                colors[(int)slot] = value;
        }

        colors[(int)ImGuiCol.BorderShadow] = Vector4.Zero;
        colors[(int)ImGuiCol.TableRowBg] = Vector4.Zero;
        colors[(int)ImGuiCol.TableRowBgAlt] = new Vector4(1f, 1f, 1f, 0.02f);
        colors[(int)ImGuiCol.ModalWindowDimBg] = new Vector4(0f, 0f, 0f, 0.45f);
    }
}
