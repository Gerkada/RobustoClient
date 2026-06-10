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

    private int _updateCounter = 0;
    private const int UpdateInterval = 15; // Update not every frame to save FPS

    public override void FrameUpdate(float frameTime)
    {
        _updateCounter++;
        if (_updateCounter < UpdateInterval)
            return;

        _updateCounter = 0;

        // In newer SS14 versions, the player entity is retrieved via LocalSession
        var localEntity = _playerManager.LocalSession?.AttachedEntity;
        if (localEntity == null)
            return;

        if (!_entityManager.TryGetComponent<EyeComponent>(localEntity, out var eyeComponent))
            return;

        ApplyVisualSettings(eyeComponent);
    }

    private void ApplyVisualSettings(EyeComponent eyeComponent)
    {
        if (RobustaConfig.FullbrightEnabled)
        {
            // Disable global lighting and shadows
            _lightManager.Enabled = false;
            _lightManager.DrawLighting = false;
            _lightManager.DrawShadows = false; 
            
            // Disable light calculation for the specific player camera
            if (eyeComponent.Eye != null)
            {
                eyeComponent.Eye.DrawLight = false;
            }
        }
        else
        {
            // Restore vanilla settings
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