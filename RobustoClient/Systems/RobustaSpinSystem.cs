using System;
using Content.Shared.MouseRotator;
using Robust.Client.Graphics;
using Robust.Client.Input;
using Robust.Client.Player;
using Robust.Shared.Map;
using Robust.Shared.Timing;
using Robust.Shared.Maths;

namespace RobustoClient.Systems;

public class RobustaSpinSystem : EntitySystem
{
    [Dependency] private readonly IInputManager _input = default!;
    [Dependency] private readonly IPlayerManager _player = default!;
    [Dependency] private readonly IGameTiming _timing = default!;
    [Dependency] private readonly IEyeManager _eye = default!;
    [Dependency] private readonly SharedTransformSystem _transform = default!;
    private float _lastDegrees;

    public override void Update(float frameTime)
    {
        if (!_timing.IsFirstTimePredicted)
            return;

        if (!RobustaConfig.SpinBotEnabled)
            if (!_input.MouseScreenPosition.IsValid)
                return;

        // Get local player
        var player = _player.LocalSession?.AttachedEntity;

        if (player == null || !TryComp<MouseRotatorComponent>(player.Value, out var rotator))
            return;

        Angle angle;
        Angle curRot;

        if (RobustaConfig.SpinBotEnabled)
        {
            _lastDegrees += RobustaConfig.SpinBotDegreesPerSecond * frameTime;
            angle = Angle.FromDegrees(_lastDegrees);
            curRot = _transform.GetWorldRotation(Transform(player.Value));
            if (_lastDegrees > 360f)
                _lastDegrees = 0;
        }
        else
        {
            var xform = Transform(player.Value);
            var coords = _input.MouseScreenPosition;

            // Convert mouse coordinates
            var mapPos = _eye.ScreenToMap(coords);

            if (mapPos.MapId == MapId.Nullspace)
                return;

            angle = (mapPos.Position - _transform.GetMapCoordinates(xform).Position).ToWorldAngle();
            curRot = _transform.GetWorldRotation(xform);
        }

        if (rotator.Simple4DirMode)
        {
            var angleDir = angle.GetCardinalDir();
            if (angleDir == curRot.GetCardinalDir())
                return;

            RaisePredictiveEvent(new RequestMouseRotatorRotationEvent()
            {
                Rotation = angle,
            });

            return;
        }

        var diff = Angle.ShortestDistance(angle, curRot);
        if (Math.Abs(diff.Theta) < rotator.AngleTolerance.Theta)
            return;

        // Send packet to server
        RaisePredictiveEvent(new RequestMouseRotatorRotationEvent
        {
            Rotation = angle
        });
    }
}