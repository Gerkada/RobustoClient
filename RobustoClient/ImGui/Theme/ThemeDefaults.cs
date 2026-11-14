using System.Numerics;
using RobustoClient.ImGui;

namespace RobustoClient.ImGui.Theme;

/// <summary>
/// Compiled-in palettes used when the embedded JSON is missing, unreadable or incomplete.
/// A malformed theme file must never produce an unusable UI, so every token has a safe value here.
/// Keep in sync with <c>Builtin/Dark.json</c>; the round-trip test
/// asserts the two agree.
/// </summary>
public static class ThemeDefaults
{
    public const string DefaultThemeName = "Dark";

    public static RobustaThemePalette Dark() => new()
    {
        Name = "Dark",
        Colors = new ThemeColors
        {
            Window = Hex("#1A1A1A"),
            Header = Hex("#1A1A1A"),
            TabStrip = Hex("#161616"),
            Card = Hex("#161616"),
            Popup = Hex("#161616"),
            Control = Hex("#17171A"),
            ControlHover = Hex("#2A2A2B"),
            ControlActive = Hex("#353535"),
            TextPrimary = Hex("#D3D3D3"),
            TextMuted = Hex("#A3A3A3"),
            TextDisabled = Hex("#5C5C5C"),
            Border = Hex("#2A2A2A"),
            Outline = Hex("#212121"),
            Primary = Hex("#8E8D91"),
            PrimaryHover = Hex("#AAAAAA"),
            PrimaryText = Hex("#C6C6C6"),
            Secondary = Hex("#F05252"),
            SecondaryHover = Hex("#F36565"),
            OnPrimary = Hex("#0B0D14"),
            OnSecondary = Hex("#0B0D14"),
            SelectedBg = Hex("#444345"),
            SelectedAccent = Hex("#747474"),
            TabHover = Hex("#3E3E3C"),
            TabSelectedBg = Hex("#080303"),
            TabText = Hex("#9BA1B0"),
            TabTextSelected = Hex("#FFFFFF"),
            Success = Hex("#34D399"),
            Warning = Hex("#FBBF24"),
            Danger = Hex("#F87171"),
            Info = Hex("#38BDF8"),
            RadarBgOuter = Hex("#0F1420F2"),
            RadarBgInner = Hex("#080D14FA"),
            RadarRing = Hex("#4D4D4DA6"),
            RadarGrid = Hex("#49494973"),
            SeveritySyndicate = Hex("#FF6B6B"),
            SeverityGrandTheft = Hex("#FFA500"),
            SeverityMajor = Hex("#FFD700"),
            SeverityRestricted = Hex("#F7F7F7"),
            SeverityMinor = Hex("#B0B0B0"),
            SeverityUnknown = Hex("#E090FF"),
            ScrollbarBg = Hex("#24232399"),
            ScrollbarGrab = Hex("#272626"),
            ScrollbarGrabHovered = Hex("#2C2C2C")
        },
        Metrics = new ThemeMetrics
        {
            WindowRounding = 0f,
            ChildRounding = 8f,
            FrameRounding = 6f,
            PopupRounding = 8f,
            ScrollbarRounding = 6f,
            GrabRounding = 6f,
            FramePaddingX = 8f,
            FramePaddingY = 5f,
            CellPaddingX = 8f,
            CellPaddingY = 5f,
            ItemSpacingX = 8f,
            ItemSpacingY = 7f,
            ItemInnerSpacingX = 6f,
            ItemInnerSpacingY = 4f,
            WindowBorderSize = 1f,
            ChildBorderSize = 1f,
            FrameBorderSize = 1f,
            PopupBorderSize = 1f,
            ScrollbarSize = 10f,
            IndentSpacing = 16f,
            TogglePadScale = 0.11f,
            ButtonRowScale = 1f,
            SliderPadScale = 0.11f,
            CardTitleRowScale = 1f,
            CardButtonScale = 1.15f,
            CardTitleGapScale = 0.125f,
            CardCollapseButtonScale = 1f,
            TitleRowScale = 2f,
            TabRowScale = 1.5f,
            GridGapScale = 0.1f,
            CardPaddingScaleX = 0.5f,
            CardPaddingScaleY = 0.1f,
            WindowPaddingScale = 0.75f,
            CloseButtonScale = 0.85f,
            LogoScale = 2.3f
        },
        Typography = new ThemeTypography
        {
            RegularSize = 14f,
            MonoSize = 14f,
            TitleSize = 15f,
            CardTitleSize = 18f
        },
        DefaultWindowSize = new Vector2(380f, 500f),
        MinWindowSize = new Vector2(380f, 420f),
        TitleFont = RobustaFontCatalog.DefaultTitleFont,
        TextFont = RobustaFontCatalog.DefaultTextFont
    };

    private static Vector4 Hex(string hex) =>
        ThemeHex.TryParse(hex, out var color) ? color : Vector4.One;
}
