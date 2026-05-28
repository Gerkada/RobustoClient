using Content.Shared.Movement.Components;
using Content.Shared.Slippery;
using Content.Shared.StepTrigger.Components;
using Robust.Client.GameObjects;
using Robust.Client.Graphics;
using Robust.Client.Input;
using Robust.Client.Player;
using Robust.Shared.Input;
using Robust.Shared.Timing;

namespace RobustoClient.Systems;

public sealed class RobustaAntiSlipSystem : EntitySystem
{
    [Dependency] private readonly InputSystem _inputSystem = default!;
    [Dependency] private readonly SharedTransformSystem _transform = default!;
    [Dependency] private readonly EntityLookupSystem _lookup = default!;
    [Dependency] private readonly IInputManager _inputManager = default!;
    [Dependency] private readonly IGameTiming _gameTiming = default!;
    [Dependency] private readonly IPlayerManager _playerManager = default!;
    [Dependency] private readonly IEyeManager _eyeManager = default!;
    
    private bool _changed;

    private bool _forcePressWalk;

    public void SetEnabled(bool enabled)
    {
        if (RobustaConfig.AntiSlipEnabled && !enabled && _forcePressWalk) 
            PressWalk(BoundKeyState.Down);
        RobustaConfig.AntiSlipEnabled = enabled;
    }

    public override void Update(float frameTime)
    {
        var player = _playerManager.LocalEntity;

        if (!player.HasValue || !RobustaConfig.AntiSlipEnabled)
            return;
        var onSoap = IsPlayerOnSoap(player.Value);
        _changed = onSoap != _forcePressWalk;
        _forcePressWalk = onSoap;
    }

    public override void FrameUpdate(float frameTime)
    {
        if (_changed)
            PressWalk(_forcePressWalk ? BoundKeyState.Down : BoundKeyState.Up);
    }

    private void PressWalk(BoundKeyState state)
    {
        if (!_playerManager.LocalEntity.HasValue || !RobustaConfig.AntiSlipEnabled)
            return;

        var player = _playerManager.LocalEntity.Value;
        var playerCord = _transform.GetMoverCoordinates(player);
        var screenCord = _eyeManager.CoordinatesToScreen(_transform.GetMoverCoordinates(player));
        var keyFunctionId = _inputManager.NetworkBindMap.KeyFunctionID(EngineKeyFunctions.Walk);


        var message = new ClientFullInputCmdMessage(_gameTiming.CurTick, _gameTiming.TickFraction, keyFunctionId)
        {
            State = state,
            Coordinates = playerCord,
            ScreenCoordinates = screenCord,
            Uid = EntityUid.Invalid,
        };

        _inputSystem.HandleInputCommand(_playerManager.LocalSession, EngineKeyFunctions.Walk, message);
    }

    private bool IsPlayerOnSoap(EntityUid player)
    {
        foreach (var entity in _lookup.GetEntitiesInRange(player, 1f, LookupFlags.Uncontained).ToList()
                     .Where(HasComp<SlipperyComponent>))
        {
            if (!TryComp<StepTriggerComponent>(entity, out var triggerComponent)) continue;
            if (!triggerComponent.Active)
                continue;
            var (walking, sprint) = GetPlayerSpeed(player);
            if (sprint <= triggerComponent.RequiredTriggeredSpeed) continue;
            if (walking >= triggerComponent.RequiredTriggeredSpeed) continue; // Ignore if we can't resist it
            return true;
        }

        return false;
    }

    private (float, float) GetPlayerSpeed(EntityUid player)
    {
        TryComp(player, out MovementSpeedModifierComponent? comp);
        return (comp?.CurrentWalkSpeed ?? MovementSpeedModifierComponent.DefaultBaseWalkSpeed,
            comp?.CurrentSprintSpeed ?? MovementSpeedModifierComponent.DefaultBaseSprintSpeed);
    }
}