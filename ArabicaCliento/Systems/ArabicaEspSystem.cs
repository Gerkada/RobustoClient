using Robust.Client.Graphics;
using Robust.Shared.GameObjects;
using Robust.Shared.IoC;

namespace ArabicaCliento.Systems;

public sealed class ArabicaEspSystem : EntitySystem
{
    [Dependency] private readonly IOverlayManager _overlayManager = default!;

    public override void Initialize()
    {
        base.Initialize();
        
        // Добавляем наш ESP в менеджер отрисовки игры
        if (!_overlayManager.HasOverlay<ArabicaEspOverlay>())
        {
            _overlayManager.AddOverlay(new ArabicaEspOverlay());
        }

        // ДОБАВЛЯЕМ ПОИСКОВИК ПРЕДМЕТОВ
        if (!_overlayManager.HasOverlay<ArabicaItemSearchOverlay>())
            _overlayManager.AddOverlay(new ArabicaItemSearchOverlay());
    }
}