using System.Collections.Generic;
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

    /// <summary>Reused query buffer, this check runs on the frame path.</summary>
    private readonly HashSet<EntityUid> _nearby = new();

    /// <summary>Frames between scans. A slip trigger does not appear and vanish between frames, and
    /// the broadphase query is the only real cost here.</summary>
    private const int ScanInterval = 3;

    private int _scanCounter = ScanInterval;
    private bool _lastOnSoap;

    public void SetEnabled(bool enabled)
    {
        if (RobustaConfig.AntiSlipEnabled && !enabled && _forcePressWalk)
        {
            PressWalk(BoundKeyState.Up);
            _forcePressWalk = false;
            _changed = false;
        }
        RobustaConfig.AntiSlipEnabled = enabled;
    }

    public override void FrameUpdate(float frameTime)
    {
        base.FrameUpdate(frameTime);
        var player = _playerManager.LocalEntity;

        if (!player.HasValue)
        {
            _forcePressWalk = false;
            _changed = false;
            return;
        }

        if (!RobustaConfig.AntiSlipEnabled)
        {
            if (_forcePressWalk)
            {
                _forcePressWalk = false;
                _changed = false;
                PressWalk(BoundKeyState.Up);
            }
            return;
        }
        var onSoap = IsPlayerOnSoap(player.Value);
        _changed = onSoap != _forcePressWalk;
        _forcePressWalk = onSoap;

        if (_changed)
            PressWalk(_forcePressWalk ? BoundKeyState.Down : BoundKeyState.Up);
    }

    private void PressWalk(BoundKeyState state)
    {
        if (!_playerManager.LocalEntity.HasValue)
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
        if (_scanCounter++ < ScanInterval)
            return _lastOnSoap;

        _scanCounter = 0;
        return _lastOnSoap = ScanForSoap(player);
    }

    private bool ScanForSoap(EntityUid player)
    {
        _nearby.Clear();
        _lookup.GetEntitiesInRange(player, 1f, _nearby, LookupFlags.Uncontained);

        foreach (var entity in _nearby)
        {
            if (!HasComp<SlipperyComponent>(entity)) continue;
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