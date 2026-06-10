using System.Numerics;
using RobustoClient.Systems.Abstract;
using Content.Client.Commands;
using Robust.Client.GameObjects;
using Robust.Client.Player;
using Robust.Shared.Player;
using Robust.Shared.GameObjects;

namespace RobustoClient.Systems;

public class RobustaAutoZoomSystem : LocalPlayerSystem
{
    [Dependency] private readonly IPlayerManager _player = default!;

    private float _zoom = 1.5f;
    private bool _isEnabled = true; // Zoom enabled by default

    // Toggle method
    public void ToggleZoom()
    {
        _isEnabled = !_isEnabled;
        if (!_isEnabled)
        {
            RestoreZoom(); // Softly restore camera to normal
        }
    }

    public void UpdateZoom(float zoom)
    {
        _zoom = zoom;
        _isEnabled = true; // If value is set manually, enable automatically
    }

    public override void FrameUpdate(float frameTime)
    {
        base.FrameUpdate(frameTime);

        // Do nothing if disabled or no player
        if (!_isEnabled || _player.LocalEntity == null)
            return;

        if (!TryComp<EyeComponent>(_player.LocalEntity.Value, out var eyeComponent))
            return;

        // HARD ZOOM LOCK: check and set our value every frame
        // so FOV changes don't reset it.
        var targetVector = new Vector2(_zoom, _zoom);
        if (eyeComponent.Eye.Zoom != targetVector)
        {
            eyeComponent.Eye.Zoom = targetVector;
            eyeComponent.NetSyncEnabled = false; // Block server synchronization
        }
    }

    protected override void OnAttached(LocalPlayerAttachedEvent ev)
    {
        if (!TryComp<EyeComponent>(ev.Entity, out var eyeComponent))
            return;
            
        eyeComponent.NetSyncEnabled = false;
        // Zoom value will be set in the next Update frame
    }

    protected override void OnDetached(LocalPlayerDetachedEvent ev)
    {
        RestoreZoom(ev.Entity);
    }

    // Helper method to reset zoom to base game settings
    private void RestoreZoom(EntityUid? entity = null)
    {
        var target = entity ?? _player.LocalEntity;
        if (target == null) return;

        if (TryComp<EyeComponent>(target.Value, out var eyeComponent))
        {
            eyeComponent.Eye.Zoom = eyeComponent.Zoom; // Reset to engine default
            eyeComponent.NetSyncEnabled = true; // Return control to server
        }
    }
}