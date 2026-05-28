using System.Numerics;
using Robust.Client.GameObjects;
using Robust.Client.Graphics;
using Robust.Client.Player;
using Robust.Shared.GameObjects;
using Robust.Shared.IoC;

namespace ArabicaCliento.Systems;

public sealed class ArabicaVisualsSystem : EntitySystem
{
    [Dependency] private readonly IPlayerManager _playerManager = default!;
    [Dependency] private readonly IEntityManager _entityManager = default!;
    [Dependency] private readonly ILightManager _lightManager = default!;

    private int _updateCounter = 0;
    private const int UpdateInterval = 15; // Обновляем не каждый кадр, чтобы не жрать FPS

    public override void Update(float frameTime)
    {
        _updateCounter++;
        if (_updateCounter < UpdateInterval)
            return;

        _updateCounter = 0;

        // В новых версиях SS14 сущность игрока берется через LocalSession
        var localEntity = _playerManager.LocalSession?.AttachedEntity;
        if (localEntity == null)
            return;

        if (!_entityManager.TryGetComponent<EyeComponent>(localEntity, out var eyeComponent))
            return;

        ApplyVisualSettings(eyeComponent);
    }

    private void ApplyVisualSettings(EyeComponent eyeComponent)
    {
        if (ArabicaConfig.FullbrightEnabled)
        {
            // Отключаем глобальное освещение и тени
            _lightManager.Enabled = false;
            _lightManager.DrawLighting = false;
            _lightManager.DrawShadows = false; 
            
            // Отключаем расчет света для камеры конкретного игрока
            if (eyeComponent.Eye != null)
            {
                eyeComponent.Eye.DrawLight = false;
            }
        }
        else
        {
            // Возвращаем ванильные настройки
            _lightManager.Enabled = true;
            _lightManager.DrawLighting = true;
            _lightManager.DrawShadows = true;
            
            if (eyeComponent.Eye != null)
            {
                eyeComponent.Eye.DrawLight = true;
            }
        }
    }
}