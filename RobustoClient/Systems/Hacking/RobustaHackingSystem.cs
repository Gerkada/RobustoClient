using Content.Shared.Doors.Components;
using Content.Shared.VendingMachines.Components;
using Content.Shared.Wires;
using Robust.Client.Graphics;
using Robust.Client.Input;
using Robust.Client.Player;
using Robust.Shared.GameObjects;
using Robust.Shared.IoC;
using Robust.Shared.Log;
using Robust.Shared.Map;

namespace RobustoClient.Systems.Hacking;

/// <summary>
/// Starts a composable wire-panel hacking intent for the entity under the cursor.
/// </summary>
public sealed class RobustaHackingSystem : EntitySystem
{
    private const float CursorSearchRange = 0.65f;
    private const float MaxInteractRange = 2.5f;

    [Dependency] private readonly IInputManager _input = default!;
    [Dependency] private readonly IPlayerManager _player = default!;
    [Dependency] private readonly IEyeManager _eye = default!;
    [Dependency] private readonly EntityLookupSystem _lookup = default!;
    [Dependency] private readonly SharedTransformSystem _transform = default!;
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
        if (_player.LocalEntity is not { } player || !Exists(player))
            return;

        var cursorMapCoordinates = _eye.PixelToMap(_input.MouseScreenPosition);
        if (cursorMapCoordinates.MapId == MapId.Nullspace ||
            _transform.GetMapCoordinates(player).MapId != cursorMapCoordinates.MapId)
            return;

        var target = FindTargetAtCursor(player, cursorMapCoordinates);
        if (target == null)
        {
            _log.Info("No wire-panel entity found at cursor.");
            return;
        }

        var intent = HasComp<VendingMachineComponent>(target.Value) ? "VendingAccess" : "PryingOpen";
        if (!_wiresSolver.TryStartSolve(target.Value, intent, out var failureReason))
            _log.Warning($"Could not start wire intent {intent} on {ToPrettyString(target.Value)}: {failureReason}.");
    }

    private EntityUid? FindTargetAtCursor(EntityUid player, MapCoordinates cursorMapCoordinates)
    {
        var playerMapCoordinates = _transform.GetMapCoordinates(player);
        EntityUid? bestTarget = null;
        var bestCursorDistance = float.MaxValue;

        foreach (var uid in _lookup.GetEntitiesInRange(cursorMapCoordinates, CursorSearchRange, LookupFlags.Uncontained))
        {
            if (uid == player || !Exists(uid) || !HasComp<WiresPanelComponent>(uid))
                continue;

            var targetMapCoordinates = _transform.GetMapCoordinates(uid);
            if (targetMapCoordinates.MapId != playerMapCoordinates.MapId ||
                (targetMapCoordinates.Position - playerMapCoordinates.Position).Length() > MaxInteractRange)
                continue;

            var cursorDistance = (targetMapCoordinates.Position - cursorMapCoordinates.Position).LengthSquared();
            if (cursorDistance >= bestCursorDistance)
                continue;

            bestCursorDistance = cursorDistance;
            bestTarget = uid;
        }

        return bestTarget;
    }
}