using ArabicaCliento.Systems.Abstract;
using Robust.Client.Graphics;
using Robust.Shared.Player;

namespace ArabicaCliento.Systems;

public class ArabicaAutoChemAddOverlaySystem : LocalPlayerSystem
{
    [Dependency] private readonly IOverlayManager _overlayMan = default!;

    private ArabicaAutoChemOverlay? _overlay;

    protected override void OnAttached(LocalPlayerAttachedEvent ev)
    {
        _overlay ??= new ArabicaAutoChemOverlay();
        _overlayMan.AddOverlay(_overlay);
    }

    protected override void OnDetached(LocalPlayerDetachedEvent ev)
    {
        if (_overlay != null) _overlayMan.RemoveOverlay(_overlay);
    }
}