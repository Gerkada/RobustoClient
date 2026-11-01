using System.Numerics;
using Robust.Client.Graphics;
using Robust.Client.ResourceManagement;
using Robust.Shared.Enums;

namespace RobustoClient.Systems.Construction;

/// <summary>
/// Renders an AutoChem-style top-center HUD status box and world-space target highlight
/// for AutoBuild, Blueprint Build, AutoDeconstruct, and AutoHack.
/// </summary>
public sealed class RobustaConstructHackHudOverlay : Overlay
{
    private readonly IEntityManager _entMan;
    private readonly Font _font;
    private readonly Font _subFont;

    public override OverlaySpace Space => OverlaySpace.ScreenSpace | OverlaySpace.WorldSpace;

    public RobustaConstructHackHudOverlay()
    {
        _entMan = IoCManager.Resolve<IEntityManager>();
        var cache = IoCManager.Resolve<IResourceCache>();
        var fontRes = cache.GetResource<FontResource>("/Fonts/NotoSans/NotoSans-Regular.ttf");
        _font = new VectorFont(fontRes, 12);
        _subFont = new VectorFont(fontRes, 11);
    }

    protected override void Draw(in OverlayDrawArgs args)
    {
        if (!_entMan.EntitySysManager.TryGetEntitySystem<RobustaAutoConstructSystem>(out var autoConstruct))
            return;

        if (!autoConstruct.TryGetHudDisplay(
                out var header,
                out var detail,
                out var subDetail,
                out float progress,
                out var accentColor,
                out var highlightMapCoords))
        {
            return;
        }

        if ((args.Space & OverlaySpace.WorldSpace) != 0 &&
            highlightMapCoords.HasValue &&
            highlightMapCoords.Value.MapId == args.MapId)
        {
            DrawDistinctActiveTargetMarker(args.WorldHandle, highlightMapCoords.Value.Position);
        }

        if ((args.Space & OverlaySpace.ScreenSpace) != 0)
        {
            DrawScreenHud(args.ScreenHandle, args.Viewport.Size, header, detail, subDetail, progress, accentColor);
        }
    }

    private static void DrawDistinctActiveTargetMarker(DrawingHandleWorld worldHandle, Vector2 center)
    {
        // High-contrast Magenta/HotPink + Gold diamond & target ring so the active build item is
        // immediately distinguishable from the cyan/orange square blueprint tile previews.
        var primaryColor = Color.FromHex("#FF2A85");
        var secondaryColor = Color.Gold;

        // 1. Soft filled circle & outer ring
        worldHandle.DrawCircle(center, 0.38f, primaryColor.WithAlpha(0.18f), filled: true);
        worldHandle.DrawCircle(center, 0.38f, primaryColor.WithAlpha(0.95f), filled: false);
        worldHandle.DrawCircle(center, 0.12f, secondaryColor.WithAlpha(0.9f), filled: false);

        // 2. Rotated outer diamond (rhombus)
        float rOuter = 0.48f;
        var top = center + new Vector2(0f, rOuter);
        var right = center + new Vector2(rOuter, 0f);
        var bottom = center + new Vector2(0f, -rOuter);
        var left = center + new Vector2(-rOuter, 0f);

        worldHandle.DrawLine(top, right, primaryColor);
        worldHandle.DrawLine(right, bottom, primaryColor);
        worldHandle.DrawLine(bottom, left, primaryColor);
        worldHandle.DrawLine(left, top, primaryColor);

        // 3. Inner Gold diamond
        float rInner = 0.28f;
        var iTop = center + new Vector2(0f, rInner);
        var iRight = center + new Vector2(rInner, 0f);
        var iBottom = center + new Vector2(0f, -rInner);
        var iLeft = center + new Vector2(-rInner, 0f);

        worldHandle.DrawLine(iTop, iRight, secondaryColor);
        worldHandle.DrawLine(iRight, iBottom, secondaryColor);
        worldHandle.DrawLine(iBottom, iLeft, secondaryColor);
        worldHandle.DrawLine(iLeft, iTop, secondaryColor);

        // 4. Diagonal crosshair ticks pointing toward center
        float diagStart = 0.42f;
        float diagEnd = 0.20f;
        Vector2[] dirs =
        {
            new(0.7071f, 0.7071f),
            new(0.7071f, -0.7071f),
            new(-0.7071f, 0.7071f),
            new(-0.7071f, -0.7071f)
        };
        foreach (var d in dirs)
        {
            worldHandle.DrawLine(center + d * diagStart, center + d * diagEnd, secondaryColor);
        }
    }

    private void DrawScreenHud(
        DrawingHandleScreen screenHandle,
        Vector2i viewportSize,
        string header,
        string detail,
        string? subDetail,
        float progress,
        Color accentColor)
    {
        var headerDim = screenHandle.GetDimensions(_font, header, 1f);
        var detailDim = screenHandle.GetDimensions(_font, detail, 1f);
        var subDim = !string.IsNullOrEmpty(subDetail)
            ? screenHandle.GetDimensions(_subFont, subDetail, 1f)
            : Vector2.Zero;

        float padding = 14f;
        float verticalGap = 8f;
        float barHeight = 4f;

        float maxTextWidth = Math.Max(headerDim.X, Math.Max(detailDim.X, subDim.X));
        float boxWidth = Math.Clamp(maxTextWidth + padding * 2.2f, 300f, 640f);

        float boxHeight = padding + headerDim.Y + verticalGap + detailDim.Y + verticalGap + barHeight + padding;
        if (!string.IsNullOrEmpty(subDetail))
            boxHeight += subDim.Y + verticalGap;

        float topY = 76f;
        if (_entMan.EntitySysManager.TryGetEntitySystem<RobustoClient.Systems.AutoChem.RobustaAutoChemSystem>(out var autoChem) &&
            autoChem.CurrentState is not RobustoClient.Systems.AutoChem.IdleState)
        {
            topY = 150f;
        }

        var basePos = new Vector2(viewportSize.X / 2f - boxWidth / 2f, topY);
        var boxSize = new Vector2(boxWidth, boxHeight);

        // Background & border (matching RobustaAutoChemOverlay aesthetic)
        screenHandle.DrawRect(UIBox2.FromDimensions(basePos, boxSize), Color.Black.WithAlpha(0.85f));
        screenHandle.DrawRect(UIBox2.FromDimensions(basePos, boxSize), accentColor.WithAlpha(0.45f), filled: false);

        float currentY = basePos.Y + padding;

        // 1. Header line
        screenHandle.DrawString(_font, new Vector2(basePos.X + padding, currentY), header, accentColor);
        currentY += headerDim.Y + verticalGap;

        // 2. Step / primary detail line
        screenHandle.DrawString(_font, new Vector2(basePos.X + padding, currentY), detail, Color.White);
        currentY += detailDim.Y + verticalGap;

        // 3. Optional diagnostic / retry / queue sub-detail line
        if (!string.IsNullOrEmpty(subDetail))
        {
            screenHandle.DrawString(_subFont, new Vector2(basePos.X + padding, currentY), subDetail, Color.LightGray);
            currentY += subDim.Y + verticalGap;
        }

        // 4. Progress bar
        var barPos = new Vector2(basePos.X + padding, currentY);
        var barFullWidth = boxWidth - (padding * 2f);
        var clampedProgress = Math.Clamp(progress, 0f, 1f);

        Color barColor = accentColor == Color.OrangeRed
            ? Color.Crimson
            : (clampedProgress < 1f ? Color.LimeGreen : Color.Gold);

        screenHandle.DrawRect(UIBox2.FromDimensions(barPos, new Vector2(barFullWidth, barHeight)), Color.DarkSlateGray);
        screenHandle.DrawRect(UIBox2.FromDimensions(barPos, new Vector2(barFullWidth * clampedProgress, barHeight)), barColor);
    }
}
