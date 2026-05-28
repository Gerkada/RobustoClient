using System;
using System.Numerics;
using Robust.Client.Graphics;
using Robust.Client.ResourceManagement;
using Robust.Shared.Enums;
using Robust.Shared.IoC;
using Robust.Shared.Maths;
using RobustoClient.Systems.AutoChem;

namespace RobustoClient.Systems;

public sealed class RobustaAutoChemOverlay : Overlay
{
    private readonly IEntityManager _entMan;
    private readonly Font _font;
    private RobustaAutoChemSystem? _autoChem;

    public override OverlaySpace Space => OverlaySpace.ScreenSpace;

    public RobustaAutoChemOverlay()
    {
        _entMan = IoCManager.Resolve<IEntityManager>();
        var cache = IoCManager.Resolve<IResourceCache>();
        _font = new VectorFont(cache.GetResource<FontResource>("/Fonts/NotoSans/NotoSans-Regular.ttf"), 12);
    }

    protected override void Draw(in OverlayDrawArgs args)
    {
        _autoChem ??= _entMan.System<RobustaAutoChemSystem>();
        
        if (_autoChem.CurrentState == AutoChemState.Idle) return;

        var screenHandle = args.ScreenHandle;
        var viewportSize = args.Viewport.Size;
        
        // --- DATA ---
        string statusText = $"[AutoChem] {_autoChem.CurrentState}";
        string infoText = _autoChem.GetStatusInfo();
        float progress = _autoChem.GetProgress();

        // --- MEASUREMENTS (Dynamic layout) ---
        var statusDim = screenHandle.GetDimensions(_font, statusText, 1f);
        var infoDim = screenHandle.GetDimensions(_font, infoText, 1f);

        float padding = 15f;
        float verticalGap = 12f; // Increased gap for readability
        float barHeight = 4f;

        float boxWidth = Math.Max(statusDim.X, infoDim.X) + padding * 3f;
        boxWidth = Math.Clamp(boxWidth, 320f, 800f);

        // Summing heights: Top padding + Text1 + Gap + Text2 + Gap + Bar + Bottom padding
        float boxHeight = padding + statusDim.Y + verticalGap + infoDim.Y + verticalGap + barHeight + padding;

        var basePos = new Vector2(viewportSize.X / 2f - boxWidth / 2f, 40f);
        var boxSize = new Vector2(boxWidth, boxHeight);

        // --- BACKGROUND RENDERING ---
        screenHandle.DrawRect(UIBox2.FromDimensions(basePos, boxSize), Color.Black.WithAlpha(0.85f));
        screenHandle.DrawRect(UIBox2.FromDimensions(basePos, boxSize), Color.Cyan.WithAlpha(0.4f), filled: false);

        // --- TEXT RENDERING (Top to bottom) ---
        float currentY = basePos.Y + padding;
        
        // 1. Status
        screenHandle.DrawString(_font, new Vector2(basePos.X + padding, currentY), statusText, Color.Cyan);
        currentY += statusDim.Y + verticalGap;

        // 2. Description
        screenHandle.DrawString(_font, new Vector2(basePos.X + padding, currentY), infoText, Color.White);
        currentY += infoDim.Y + verticalGap;

        // --- PROGRESS BAR ---
        var barPos = new Vector2(basePos.X + padding, currentY);
        var barFullWidth = boxWidth - (padding * 2);
        
        screenHandle.DrawRect(UIBox2.FromDimensions(barPos, new Vector2(barFullWidth, barHeight)), Color.DarkSlateGray);
        screenHandle.DrawRect(UIBox2.FromDimensions(barPos, new Vector2(barFullWidth * progress, barHeight)), 
            progress < 1f ? Color.LimeGreen : Color.Gold);
    }
}