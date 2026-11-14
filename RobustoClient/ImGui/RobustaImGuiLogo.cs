using System.IO;
using System.Numerics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Hexa.NET.ImGui;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using RobustoClient.UI.ImGui;

namespace RobustoClient.ImGui;

/// <summary>
/// Loads the header logo from an embedded resource into a GL texture on first use, then caches it.
/// Any failure is logged once and disables the logo; the header falls back to text only.
/// The texture lives for the process lifetime: the ImGui context is created once and never recreated.
/// </summary>
public static class RobustaImGuiLogo
{
    /// <summary>
    /// Logical resource name, set in RobustoClient.csproj. The sprite is CC-BY-SA-3.0, taken from
    /// tgstation via Resources/Textures/Clothing/Mask/clown.rsi/icon.png.
    /// </summary>
    private const string ResourceName = "RobustoClient.Assets.ClownLogo.png";

    /// <summary>Alpha above which a pixel counts as visible artwork, out of 255.</summary>
    private const byte AlphaThreshold = 8;

    private static ImTextureRef _texture;
    private static Vector2 _size;
    private static Vector2 _uvMin;
    private static Vector2 _uvMax = Vector2.One;
    private static bool _loaded;
    private static bool _failed;

    /// <summary>
    /// Last sampling mode pushed to GL. Starts false because <see cref="ImGuiGl3Renderer.CreateTexture"/>
    /// uploads with linear filtering, so a nearest request must still be applied on first use.
    /// </summary>
    private static bool _appliedNearest;

    public static bool IsAvailable
    {
        get
        {
            EnsureLoaded();
            return !_failed && _texture.TexID.Handle != 0;
        }
    }

    public static ImTextureRef Texture
    {
        get
        {
            EnsureLoaded();
            return _texture;
        }
    }

    /// <summary>Source dimensions of the decoded image, used to preserve aspect ratio.</summary>
    public static Vector2 SourceSize
    {
        get
        {
            EnsureLoaded();
            return _size;
        }
    }

    /// <summary>
    /// Normalized texture rectangle of the visible artwork, excluding transparent padding.
    /// </summary>
    /// <remarks>
    /// The clown sprite is a 32x32 tile whose actual drawing is only about 17x9 pixels in the middle.
    /// Drawing the whole tile makes the artwork appear far smaller than the space it is given, so the
    /// quad is cropped to the opaque bounds and the art fills its box instead.
    /// </remarks>
    public static (Vector2 Min, Vector2 Max) VisibleUv
    {
        get
        {
            EnsureLoaded();
            return (_uvMin, _uvMax);
        }
    }

    /// <summary>
    /// Size the artwork is drawn at when fitted inside a square box of side <paramref name="box"/>.
    /// Aspect is preserved, so the result is wider than it is tall for this sprite.
    /// </summary>
    public static Vector2 FittedSize(float box)
    {
        var source = SourceSize;
        float aspect = source.Y > 0f ? source.X / source.Y : 1f;

        return aspect >= 1f
            ? new Vector2(box, box / aspect)
            : new Vector2(box * aspect, box);
    }

    /// <summary>
    /// Largest whole-number scale of the source art that fits inside <paramref name="desiredWidth"/>,
    /// never below 1.
    /// </summary>
    public static int PixelPerfectMultiple(float desiredWidth) => SnapMultiple(desiredWidth, SourceSize.X);

    /// <summary>
    /// Whole-number scale for a given source art width. Split out as a pure function so the
    /// arithmetic is testable without a live GL texture.
    /// </summary>
    /// <remarks>
    /// Nearest sampling only looks crisp when every source texel maps to the same number of screen
    /// pixels. At a fractional scale some texels would span 2 pixels and their neighbours 3, which
    /// shimmers as the size changes, so the drawn size is quantised to whole multiples instead.
    /// </remarks>
    public static int SnapMultiple(float desiredWidth, float artWidth)
    {
        if (artWidth <= 0f || !float.IsFinite(desiredWidth))
            return 1;

        return Math.Max(1, (int)MathF.Round(desiredWidth / artWidth));
    }

    /// <summary>
    /// Applies nearest sampling, doing nothing when it has already been applied.
    /// </summary>
    public static void ApplyFilter(bool nearest = true)
    {
        EnsureLoaded();

        if (_failed || _texture.TexID.Handle == 0 || _appliedNearest == nearest)
            return;

        RobustaImGuiManager.Renderer?.SetTextureFilter(_texture.TexID, nearest);
        _appliedNearest = nearest;
    }

    private static unsafe void EnsureLoaded()
    {
        if (_loaded)
            return;

        _loaded = true;

        var renderer = RobustaImGuiManager.Renderer;
        if (renderer == null || !renderer.IsInitialized)
        {
            // Not a failure: the backend simply is not up yet. Retry on the next call.
            _loaded = false;
            return;
        }

        try
        {
            using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(ResourceName);
            if (stream == null)
            {
                MarseyLogger.Warn($"[Logo] Embedded resource '{ResourceName}' is missing.");
                _failed = true;
                return;
            }

            if (!TryDecodeLogoPng(stream, out var rgba, out int width, out int height))
            {
                MarseyLogger.Warn("[Logo] Embedded image decoded to zero size.");
                _failed = true;
                return;
            }

            var handle = GCHandle.Alloc(rgba, GCHandleType.Pinned);
            try
            {
                var texId = renderer.CreateTexture((byte*)handle.AddrOfPinnedObject(), width, height);
                if (texId.Handle == 0)
                {
                    MarseyLogger.Warn("[Logo] GL texture creation returned no handle.");
                    _failed = true;
                    return;
                }

                _texture = new ImTextureRef(null, texId);
                MarseyLogger.Debug(
                    $"[Logo] Loaded embedded logo ({width}x{height}), visible art {_size.X:0}x{_size.Y:0}.");
            }
            finally
            {
                handle.Free();
            }
        }
        catch (Exception ex)
        {
            MarseyLogger.Warn($"[Logo] Failed to load embedded logo: {ex.GetType().Name}: {ex.Message}");
            _failed = true;
        }
    }

    // Isolated behind NoInlining so EnsureLoaded's try/catch runs even if SixLabors.ImageSharp fails to resolve at JIT time.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static bool TryDecodeLogoPng(Stream stream, out byte[] rgba, out int width, out int height)
    {
        using var image = Image.Load<Rgba32>(stream);
        int w = image.Width;
        int h = image.Height;
        if (w <= 0 || h <= 0)
        {
            width = w;
            height = h;
            rgba = [];
            return false;
        }

        width = w;
        height = h;
        rgba = new byte[w * h * 4];
        int minX = w, minY = h, maxX = -1, maxY = -1;
        var pixels = rgba;

        image.ProcessPixelRows(accessor =>
        {
            for (int y = 0; y < accessor.Height; y++)
            {
                var row = accessor.GetRowSpan(y);
                for (int x = 0; x < row.Length; x++)
                {
                    var p = row[x];
                    int o = (y * w + x) * 4;
                    pixels[o] = p.R;
                    pixels[o + 1] = p.G;
                    pixels[o + 2] = p.B;
                    pixels[o + 3] = p.A;

                    if (p.A <= AlphaThreshold)
                        continue;

                    if (x < minX) minX = x;
                    if (x > maxX) maxX = x;
                    if (y < minY) minY = y;
                    if (y > maxY) maxY = y;
                }
            }
        });

        if (maxX >= minX && maxY >= minY)
        {
            _uvMin = new Vector2(minX / (float)w, minY / (float)h);
            _uvMax = new Vector2((maxX + 1) / (float)w, (maxY + 1) / (float)h);
            _size = new Vector2(maxX - minX + 1, maxY - minY + 1);
        }
        else
        {
            _uvMin = Vector2.Zero;
            _uvMax = Vector2.One;
            _size = new Vector2(w, h);
        }

        return true;
    }
}
