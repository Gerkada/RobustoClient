using RobustoClient.Systems.Abstract;
using Robust.Client.Graphics;
using Robust.Shared.Player;

namespace RobustoClient.Systems;

public class RobustaAutoChemAddOverlaySystem : LocalPlayerSystem
{
    [Dependency] private readonly IOverlayManager _overlayMan = default!;

    private RobustaAutoChemOverlay? _overlay;

    protected override void OnAttached(LocalPlayerAttachedEvent ev)
    {
        _overlay ??= new RobustaAutoChemOverlay();
        _overlayMan.AddOverlay(_overlay);
    }

    protected override void OnDetached(LocalPlayerDetachedEvent ev)
    {
        if (_overlay != null) _overlayMan.RemoveOverlay(_overlay);
    }
}