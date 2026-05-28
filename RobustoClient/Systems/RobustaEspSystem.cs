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
        
        // Добавляем наш ESP в менеджер отрисовки игры
        if (!_overlayManager.HasOverlay<RobustaEspOverlay>())
        {
            _overlayManager.AddOverlay(new RobustaEspOverlay());
        }

        // ДОБАВЛЯЕМ ПОИСКОВИК ПРЕДМЕТОВ
        if (!_overlayManager.HasOverlay<RobustaItemSearchOverlay>())
            _overlayManager.AddOverlay(new RobustaItemSearchOverlay());
    }
}