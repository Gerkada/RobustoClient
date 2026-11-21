using Content.Shared.Interaction;
using Content.Shared.Wires;
using Robust.Client.Player;
using Robust.Shared.GameObjects;
using Robust.Shared.IoC;
using Robust.Shared.Log;
using Robust.Shared.Map;
using RobustoClient.Systems.Abstract;

namespace RobustoClient.Systems.Hacking;

/// <summary>
/// Starts a composable wire-panel hacking intent for the entity under the cursor.
/// </summary>
public sealed class RobustaHackingSystem : EntitySystem
{
    private const float CursorSearchRange = 0.65f;

    [Dependency] private readonly IPlayerManager _player = default!;
    [Dependency] private readonly SharedTransformSystem _transform = default!;
    [Dependency] private readonly MouseUtilitySystem _mouse = default!;
    [Dependency] private readonly RobustaWiresSolver _wiresSolver = default!;

    private ISawmill _log = default!;

    public override void Initialize()
    {
        base.Initialize();
        _log = Logger.GetSawmill("robusta.hacking");
    }

    /// <summary>
    /// Finds the wire-panel entity under the cursor and starts its appropriate default intent.
    /// </summary>
    public void TriggerAtCursor()
    {
        if (_wiresSolver.IsSolving)
        {
            _log.Info("Aborting active wire-hacking solve.");
            _wiresSolver.StopSolving("Aborted by user.");
            return;
        }

        if (_player.LocalEntity is not { } player || !Exists(player))
            return;

        if (!_mouse.TryGetMouseMapCoordinates(out var cursorMapCoordinates) ||
            _transform.GetMapCoordinates(player).MapId != cursorMapCoordinates.MapId)
            return;

        var target = FindTargetAtCursor(player, cursorMapCoordinates, out var outOfRangeTarget, out float outOfRangeDist);
        if (target == null)
        {
            if (outOfRangeTarget.HasValue && Exists(outOfRangeTarget.Value))
            {
                var msg = $"Target '{Name(outOfRangeTarget.Value)}' is out of range ({outOfRangeDist:0.1}m > {SharedInteractionSystem.InteractionRange:0.1}m) — move closer.";
                _log.Info(msg);
                _wiresSolver.ReportStatusBanner("[AutoHack]", msg, isError: true, duration: 4.5f);
            }
            else
            {
                _log.Info("No wire-panel entity found at cursor.");
                _wiresSolver.ReportStatusBanner("[AutoHack]", "No wire-panel entity found at cursor.", isError: true, duration: 3.5f);
            }
            return;
        }

        var intent = _wiresSolver.IsVendingMachine(target.Value) ? "VendingAccess" : "PryingOpen";
        if (!_wiresSolver.TryStartSolve(target.Value, intent, out var failureReason))
        {
            _log.Warning($"Could not start wire intent {intent} on {ToPrettyString(target.Value)}: {failureReason}.");
            _wiresSolver.ReportStatusBanner(
                "[AutoHack Failed]",
                $"Could not start '{intent}' on {Name(target.Value)}: {failureReason}",
                isError: true,
                duration: 5f);
        }
    }

    private EntityUid? FindTargetAtCursor(
        EntityUid player,
        MapCoordinates cursorMapCoordinates,
        out EntityUid? outOfRangeTarget,
        out float outOfRangeDist)
    {
        outOfRangeTarget = null;
        outOfRangeDist = 0f;

        var playerMapCoordinates = _transform.GetMapCoordinates(player);
        EntityUid? bestTarget = null;
        var bestCursorDistance = float.MaxValue;
        var bestOutOfRangeCursorDist = float.MaxValue;

        foreach (var uid in _mouse.GetEntitiesNearCursor(
            cursorMapCoordinates, 
            CursorSearchRange, 
            whitelist: [typeof(WiresPanelComponent)]))
        {
            if (uid == player || !Exists(uid))
                continue;

            var targetMapCoordinates = _transform.GetMapCoordinates(uid);
            if (targetMapCoordinates.MapId != playerMapCoordinates.MapId)
                continue;

            var playerDist = (targetMapCoordinates.Position - playerMapCoordinates.Position).Length();
            var cursorDistance = (targetMapCoordinates.Position - cursorMapCoordinates.Position).LengthSquared();

            if (playerDist > SharedInteractionSystem.InteractionRange)
            {
                if (cursorDistance < bestOutOfRangeCursorDist)
                {
                    bestOutOfRangeCursorDist = cursorDistance;
                    outOfRangeTarget = uid;
                    outOfRangeDist = playerDist;
                }
                continue;
            }

            if (cursorDistance >= bestCursorDistance)
                continue;

            bestCursorDistance = cursorDistance;
            bestTarget = uid;
        }

        return bestTarget;
    }
}