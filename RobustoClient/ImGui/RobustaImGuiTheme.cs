using System.IO;
using System.Numerics;
using System.Runtime.InteropServices;
using Hexa.NET.ImGui;
using Robust.Shared.ContentPack;
using Robust.Shared.IoC;
using Robust.Shared.Utility;

namespace RobustoClient.ImGui;

public static unsafe class RobustaImGuiTheme
{
    public static ImFontPtr FontRegular { get; private set; }
    public static ImFontPtr FontBold { get; private set; }
    public static ImFontPtr FontMono { get; private set; }

    // Palette constants (RGBA Vector4)
    public static readonly Vector4 BgWindow = Hex("#0B0D14", 0.97f);
    public static readonly Vector4 BgSidebar = Hex("#0E111B", 1.00f);
    public static readonly Vector4 BgCard = Hex("#121521", 0.96f);
    public static readonly Vector4 BgControl = Hex("#1A1E2E", 1.00f);
    public static readonly Vector4 BgControlHover = Hex("#242A40", 1.00f);
    public static readonly Vector4 BgControlActive = Hex("#2E3652", 1.00f);
    public static readonly Vector4 BorderSubtle = Hex("#252B40", 0.90f);
    public static readonly Vector4 TextPrimary = Hex("#CDD6F4", 1.00f);
    public static readonly Vector4 TextMuted = Hex("#7F849C", 1.00f);
    public static readonly Vector4 AccentViolet = Hex("#7C3AED", 1.00f);
    public static readonly Vector4 AccentVioletHover = Hex("#8B5CF6", 1.00f);
    public static readonly Vector4 AccentCyan = Hex("#38BDF8", 1.00f);
    public static readonly Vector4 AccentGreen = Hex("#10B981", 1.00f);
    public static readonly Vector4 AccentRed = Hex("#EF4444", 1.00f);
    public static readonly Vector4 AccentGold = Hex("#F59E0B", 1.00f);

    public static void Apply()
    {
        LoadFonts();

        var style = Hexa.NET.ImGui.ImGui.GetStyle();
        style.WindowPadding = new Vector2(12f, 12f);
        style.FramePadding = new Vector2(8f, 5f);
        style.CellPadding = new Vector2(8f, 5f);
        style.ItemSpacing = new Vector2(8f, 7f);
        style.ItemInnerSpacing = new Vector2(6f, 4f);
        style.ButtonTextAlign = new Vector2(0.5f, 0.5f);
        style.IndentSpacing = 16f;
        style.ScrollbarSize = 10f;
        style.GrabMinSize = 10f;

        style.WindowBorderSize = 1f;
        style.ChildBorderSize = 1f;
        style.PopupBorderSize = 1f;
        style.FrameBorderSize = 1f;

        style.WindowRounding = 10f;
        style.ChildRounding = 8f;
        style.FrameRounding = 6f;
        style.PopupRounding = 8f;
        style.ScrollbarRounding = 6f;
        style.GrabRounding = 6f;
        style.TabRounding = 6f;

        var colors = style.Colors;
        colors[(int)ImGuiCol.Text] = TextPrimary;
        colors[(int)ImGuiCol.TextDisabled] = TextMuted;
        colors[(int)ImGuiCol.WindowBg] = BgWindow;
        colors[(int)ImGuiCol.ChildBg] = BgCard;
        colors[(int)ImGuiCol.PopupBg] = Hex("#10131F", 0.98f);
        colors[(int)ImGuiCol.Border] = BorderSubtle;
        colors[(int)ImGuiCol.BorderShadow] = Vector4.Zero;

        colors[(int)ImGuiCol.FrameBg] = BgControl;
        colors[(int)ImGuiCol.FrameBgHovered] = BgControlHover;
        colors[(int)ImGuiCol.FrameBgActive] = BgControlActive;

        colors[(int)ImGuiCol.TitleBg] = Hex("#090B11", 1.00f);
        colors[(int)ImGuiCol.TitleBgActive] = Hex("#0E111B", 1.00f);
        colors[(int)ImGuiCol.TitleBgCollapsed] = Hex("#090B11", 0.80f);

        colors[(int)ImGuiCol.ScrollbarBg] = Hex("#0B0D14", 0.60f);
        colors[(int)ImGuiCol.ScrollbarGrab] = Hex("#2A314A", 1.00f);
        colors[(int)ImGuiCol.ScrollbarGrabHovered] = Hex("#3B4466", 1.00f);
        colors[(int)ImGuiCol.ScrollbarGrabActive] = AccentViolet;

        colors[(int)ImGuiCol.CheckMark] = AccentCyan;
        colors[(int)ImGuiCol.SliderGrab] = AccentViolet;
        colors[(int)ImGuiCol.SliderGrabActive] = AccentCyan;

        colors[(int)ImGuiCol.Button] = BgControl;
        colors[(int)ImGuiCol.ButtonHovered] = BgControlHover;
        colors[(int)ImGuiCol.ButtonActive] = AccentViolet;

        colors[(int)ImGuiCol.Header] = Hex("#1E2438", 1.00f);
        colors[(int)ImGuiCol.HeaderHovered] = Hex("#28304A", 1.00f);
        colors[(int)ImGuiCol.HeaderActive] = AccentViolet;

        colors[(int)ImGuiCol.Separator] = BorderSubtle;
        colors[(int)ImGuiCol.SeparatorHovered] = AccentVioletHover;
        colors[(int)ImGuiCol.SeparatorActive] = AccentCyan;

        colors[(int)ImGuiCol.ResizeGrip] = Hex("#252B40", 0.40f);
        colors[(int)ImGuiCol.ResizeGripHovered] = AccentVioletHover;
        colors[(int)ImGuiCol.ResizeGripActive] = AccentCyan;

        colors[(int)ImGuiCol.TableHeaderBg] = Hex("#161A29", 1.00f);
        colors[(int)ImGuiCol.TableBorderStrong] = BorderSubtle;
        colors[(int)ImGuiCol.TableBorderLight] = Hex("#1C2133", 0.80f);
        colors[(int)ImGuiCol.TableRowBg] = Vector4.Zero;
        colors[(int)ImGuiCol.TableRowBgAlt] = new Vector4(1f, 1f, 1f, 0.02f);
    }

    private static void LoadFonts()
    {
        var io = Hexa.NET.ImGui.ImGui.GetIO();
        FontRegular = TryAddVfsFont(io, ["/Fonts/NotoSans/NotoSans-Regular.ttf", "/EngineFonts/NotoSans/NotoSans-Regular.ttf"], 15.5f);
        FontBold = TryAddVfsFont(io, ["/Fonts/NotoSans/NotoSans-Bold.ttf", "/Fonts/NotoSansDisplay/NotoSansDisplay-Bold.ttf"], 16.5f);
        FontMono = TryAddVfsFont(io, ["/EngineFonts/NotoSans/NotoSansMono-Regular.ttf", "/Fonts/RobotoMono/RobotoMono-Regular.ttf"], 14.0f);

        if (FontRegular.IsNull)
            FontRegular = io.Fonts.AddFontDefault();
        if (FontBold.IsNull)
            FontBold = FontRegular;
        if (FontMono.IsNull)
            FontMono = FontRegular;
    }

    private static ImFontPtr TryAddVfsFont(ImGuiIOPtr io, string[] candidatePaths, float sizePixels)
    {
        if (!RobustoClient.UI.ImGui.RobustaImGuiManager.TryResolve<IResourceManager>(out var resMan))
            return ImFontPtr.Null;

        foreach (var path in candidatePaths)
        {
            try
            {
                if (!resMan.TryContentFileRead(new ResPath(path), out var stream))
                    continue;

                using (stream)
                {
                    using var ms = new MemoryStream();
                    stream.CopyTo(ms);
                    var bytes = ms.ToArray();
                    if (bytes.Length == 0)
                        continue;

                    void* nativeMem = Hexa.NET.ImGui.ImGui.MemAlloc((nuint)bytes.Length);
                    Marshal.Copy(bytes, 0, (nint)nativeMem, bytes.Length);
                    return io.Fonts.AddFontFromMemoryTTF(nativeMem, bytes.Length, sizePixels);
                }
            }
            catch
            {
                // Try next fallback path.
            }
        }

        return ImFontPtr.Null;
    }

    public static Vector4 Hex(string hex, float alpha = 1.0f)
    {
        var clean = hex.AsSpan().TrimStart('#');
        if (clean.Length < 6)
            return new Vector4(1f, 1f, 1f, alpha);

        byte r = Convert.ToByte(clean[..2].ToString(), 16);
        byte g = Convert.ToByte(clean.Slice(2, 2).ToString(), 16);
        byte b = Convert.ToByte(clean.Slice(4, 2).ToString(), 16);
        return new Vector4(r / 255f, g / 255f, b / 255f, alpha);
    }

    public static uint ColU32(Vector4 color) => Hexa.NET.ImGui.ImGui.ColorConvertFloat4ToU32(color);
}
