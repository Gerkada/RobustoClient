using Content.Shared.Doors.Components;
using Content.Shared.Hands.EntitySystems;
using Content.Shared.Prying.Components;

namespace RobustoClient.Systems.Hacking;

public sealed partial class RobustaWiresSolver
{
    [Dependency] private readonly SharedHandsSystem _hands = default!;

    private void AdvanceTargetTool(ActiveSolve solve, SolvedTargetToolAction action)
    {
        if (!TryComp<DoorComponent>(solve.Target, out var door))
        {
            FailSolve("tool target no longer has a door component");
            return;
        }

        // Prying toggles doors; never interact with a door that is already open.
        if (door.State == action.ExpectedState)
        {
            CompleteTargetTool(solve);
            return;
        }

        if (door.State != DoorState.Closed)
            return;

        if (!solve.ToolRequested)
        {
            solve.ToolRequested = _itemUtility.TryEquipByPredicate(
                uid => _tools.HasQuality(uid, action.ToolQuality) &&
                       TryComp<PryingComponent>(uid, out var prying) && prying.Enabled,
                $"Tool:{action.ToolQuality}");
            if (!solve.ToolRequested)
                FailSolve($"could not equip an enabled {action.ToolQuality} tool");
            return;
        }

        if (_itemUtility.IsBusy)
            return;

        if (_playerManager.LocalEntity is not { } player ||
            _hands.GetActiveItem(player) is not { } held || !_tools.HasQuality(held, action.ToolQuality))
        {
            FailSolve("required tool was not equipped");
            return;
        }

        ClickTarget(solve.Target);
        solve.Phase = SolvePhase.WaitForTargetTool;
        solve.Elapsed = 0f;
    }

    private void AdvanceTargetToolVerification(ActiveSolve solve)
    {
        if (solve.Plan?.Actions[solve.ActionIndex] is not SolvedTargetToolAction action ||
            !TryComp<DoorComponent>(solve.Target, out var door))
        {
            FailSolve("lost target tool action or door");
            return;
        }

        // The upstream pry DoAfter and opening animation must finish before reporting success.
        if (door.State == action.ExpectedState)
            CompleteTargetTool(solve);
    }

    private static void CompleteTargetTool(ActiveSolve solve)
    {
        solve.ActionIndex++;
        solve.ToolRequested = false;
        solve.Phase = SolvePhase.ExecuteAction;
        solve.Elapsed = 0f;
    }
}