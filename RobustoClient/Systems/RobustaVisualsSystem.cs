using System.Numerics;
using Robust.Client.GameObjects;
using Robust.Client.Graphics;
using Robust.Client.Player;
using Robust.Shared.GameObjects;
using Robust.Shared.IoC;

namespace RobustoClient.Systems;

public sealed class RobustaVisualsSystem : EntitySystem
{
    [Dependency] private readonly IPlayerManager _playerManager = default!;
    [Dependency] private readonly IEntityManager _entityManager = default!;
    [Dependency] private readonly ILightManager _lightManager = default!;

    private int _updateCounter;
    private const int UpdateInterval = 15; // Update not every frame to save FPS

    public override void FrameUpdate(float frameTime)
    {
        _updateCounter++;
        if (_updateCounter < UpdateInterval)
            return;

        _updateCounter = 0;

        var localEntity = _playerManager.LocalSession?.AttachedEntity;
        if (localEntity == null)
            return;

        if (!_entityManager.TryGetComponent<EyeComponent>(localEntity, out var eyeComponent))
            return;

        ApplyVisualSettings(eyeComponent);
    }

    private void ApplyVisualSettings(EyeComponent eyeComponent)
    {
        var enabled = !RobustaConfig.FullbrightEnabled;

        _lightManager.Enabled = enabled;
        _lightManager.DrawLighting = enabled;
        _lightManager.DrawShadows = enabled;

        if (eyeComponent.Eye != null)
            eyeComponent.Eye.DrawLight = enabled;
    }
}
