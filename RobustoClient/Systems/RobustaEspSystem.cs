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

        if (!_overlayManager.HasOverlay<RobustaEspOverlay>())
            _overlayManager.AddOverlay(new RobustaEspOverlay());
    }
}