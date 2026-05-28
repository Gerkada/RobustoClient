using Robust.Client.Graphics;
using Robust.Shared.GameObjects;
using Robust.Shared.IoC;

namespace RobustoClient.Systems;

public sealed class RobustaEspSystem : EntitySystem
{
    [Dependency] private readonly IOverlayManager _overlayManager = default!;

    public override void Initialize()
    {
        base.Initialize();
        
        // Add our ESP to the game's overlay manager
        if (!_overlayManager.HasOverlay<RobustaEspOverlay>())
        {
            _overlayManager.AddOverlay(new RobustaEspOverlay());
        }

        // Add item searcher
        if (!_overlayManager.HasOverlay<RobustaItemSearchOverlay>())
            _overlayManager.AddOverlay(new RobustaItemSearchOverlay());
    }
}