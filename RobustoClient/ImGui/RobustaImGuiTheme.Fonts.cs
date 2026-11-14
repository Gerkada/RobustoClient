using System.IO;
using System.Numerics;
using System.Runtime.InteropServices;
using Hexa.NET.ImGui;
using Robust.Shared.ContentPack;
using Robust.Shared.Utility;
using static Hexa.NET.ImGui.ImGui;

namespace RobustoClient.ImGui;

public static unsafe partial class RobustaImGuiTheme
{
    public static ImFontPtr FontRegular { get; private set; }
    public static ImFontPtr FontBold { get; private set; }

    /// <summary>Face the window title draws with, chosen by the theme's <c>TitleFont</c>.</summary>
    public static ImFontPtr FontTitle { get; private set; }

    /// <summary>Every baked catalogue face, keyed by <see cref="RobustaFontFace.Key"/>.</summary>
    private static readonly Dictionary<string, ImFontPtr> BakedFonts = new();

    /// <summary>Upper bound of the theme editor's title-size slider, used as the bake size.</summary>
    private const float MaxBakeSize = 40f;

    /// <summary>
    /// Builds the font atlas. Sizes are only defaults here — the draw path passes
    /// <see cref="ThemeTypography"/> sizes to every <c>PushFont</c>, so changing a size does not
    /// require rebuilding this atlas.
    /// </summary>
    /// <remarks>
    /// Every catalogue face is baked once, at the largest size any theme can ask for, so switching
    /// themes only repoints the active pointers. Rebuilding the atlas on a theme switch would mean
    /// re-uploading textures mid-session, which is what the 1.92 dynamic-texture path exists to avoid.
    /// </remarks>
    private static void LoadFonts()
    {
        var io = GetIO();

        // PushFont can request any size, but anything larger than the baked size is upscaled and
        // blurs.
        float bakeSize = MathF.Max(
            MathF.Max(Typography.CardTitleSize, Typography.TitleSize),
            MathF.Max(Typography.RegularSize, MaxBakeSize));

        BakedFonts.Clear();
        foreach (var face in RobustaFontCatalog.Faces)
        {
            var font = TryAddVfsFont(io, face.Path, bakeSize);
            if (!font.IsNull)
                BakedFonts[face.Key] = font;
        }

        ResolveFonts();

        if (FontRegular.IsNull)
            FontRegular = io.Fonts.AddFontDefault();
        if (FontBold.IsNull)
            FontBold = FontRegular;
        if (FontTitle.IsNull)
            FontTitle = FontBold;
    }

    /// <summary>Points the public font fields at the faces the active theme selected.</summary>
    /// <remarks>
    /// Safe to call before the atlas exists: it returns early while nothing is baked, and
    /// <see cref="LoadFonts"/> resolves again once it has. Each role falls back to RobotoMono and then
    /// to whatever is already set, so an unknown key in a theme file degrades instead of blanking.
    /// </remarks>
    public static void ResolveFonts()
    {
        if (BakedFonts.Count == 0)
            return;

        var palette = Current;

        var text = PickFont(palette.TextFont, RobustaFontCatalog.DefaultTextFont);
        if (!text.IsNull)
        {
            FontRegular = text;

            // Card titles and other bold runs stay in the same family as the body text.
            var bold = RobustaFontCatalog.TryGet(palette.TextFont, out var textFace)
                ? PickFont(textFace.BoldKey, RobustaFontCatalog.DefaultTextFont)
                : text;
            FontBold = bold.IsNull ? text : bold;
        }

        var title = PickFont(palette.TitleFont, RobustaFontCatalog.DefaultTitleFont);
        if (!title.IsNull)
            FontTitle = title;
    }

    /// <summary>Baked face for <paramref name="key"/>, falling back to <paramref name="fallbackKey"/>.</summary>
    private static ImFontPtr PickFont(string? key, string fallbackKey)
    {
        if (RobustaFontCatalog.TryGet(key, out var face) && BakedFonts.TryGetValue(face.Key, out var font))
            return font;

        if (RobustaFontCatalog.TryGet(fallbackKey, out var fallback) &&
            BakedFonts.TryGetValue(fallback.Key, out var fallbackFont))
        {
            return fallbackFont;
        }

        MarseyLogger.Warn($"[Fonts] No baked face for '{key}' or '{fallbackKey}'.");
        return ImFontPtr.Null;
    }

    /// <summary>Adds a face from the game's resources. Null when the read or parse fails.</summary>
    private static ImFontPtr TryAddVfsFont(ImGuiIOPtr io, string path, float sizePixels)
    {
        if (!RobustoClient.UI.ImGui.RobustaImGuiManager.TryResolve<IResourceManager>(out var resMan))
            return ImFontPtr.Null;

        try
        {
            if (!resMan.TryContentFileRead(new ResPath(path), out var stream))
            {
                MarseyLogger.Warn($"[Fonts] '{path}' is not in the resource manager.");
                return ImFontPtr.Null;
            }

            using (stream)
            {
                using var ms = new MemoryStream();
                stream.CopyTo(ms);

                var bytes = ms.ToArray();
                if (bytes.Length == 0)
                    return ImFontPtr.Null;

                // The atlas takes ownership of this buffer and frees it on destruction, so it must
                // not be freed or reused here. No glyph ranges are passed: under 1.92 glyphs
                // rasterize on demand, and a range would silently cap coverage.
                void* nativeMem = MemAlloc((nuint)bytes.Length);
                Marshal.Copy(bytes, 0, (nint)nativeMem, bytes.Length);
                return io.Fonts.AddFontFromMemoryTTF(nativeMem, bytes.Length, sizePixels);
            }
        }
        catch (Exception ex)
        {
            MarseyLogger.Warn($"[Fonts] Failed to load '{path}': {ex.Message}");
            return ImFontPtr.Null;
        }
    }

    /// <summary>Parses a hex colour into an ImGui colour vector.</summary>
    public static uint ColU32(Vector4 color) => ColorConvertFloat4ToU32(color);

    // ---------------------------------------------------------------------------------------
    // Forwarding properties retained so existing windows compile unchanged. New code should read
    // from RobustaImGuiTheme.Colors / .Metrics / .Typography directly.
    // ---------------------------------------------------------------------------------------

    public static Vector4 TextPrimary => Colors.TextPrimary;
    public static Vector4 TextMuted => Colors.TextMuted;
    public static Vector4 BgCard => Colors.Card;
    public static Vector4 BgControl => Colors.Control;
    public static Vector4 BgControlHover => Colors.ControlHover;
    public static Vector4 BorderSubtle => Colors.Border;
    public static Vector4 AccentCyan => Colors.Info;
    public static Vector4 AccentGreen => Colors.Success;
    public static Vector4 AccentRed => Colors.Danger;
    public static Vector4 AccentGold => Colors.Warning;
    public static Vector4 AccentViolet => Colors.Primary;
    public static Vector4 AccentVioletHover => Colors.PrimaryHover;
}
