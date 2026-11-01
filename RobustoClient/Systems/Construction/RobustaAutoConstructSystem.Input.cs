using Content.Shared.Interaction;
using Robust.Shared.Map;

namespace RobustoClient.Systems.Construction;

public sealed partial class RobustaAutoConstructSystem
{
    public void HandleConstructKeyTap()
    {
        // Ignore OS key-repeat events while the construct key is held down
        if (_wasConstructKeyDown)
            return;
        _wasConstructKeyDown = true;

        if (_activePlan != null || _blueprintQueue.Count > 0)
        {
            _pendingSingleTap = false;
            _singleTapTimer = 0f;
            _log.Info("Aborting active AutoConstruct job.");
            StopJob();
            return;
        }

        // A held deconstruct combination (whatever AutoDeconstructKey is configured to, e.g. N or Shift+V)
        // immediately triggers Deconstruct instead of starting the build tap window.
        if (RobustaConfig.AutoDeconstructKey.IsDown(_input))
        {
            _pendingSingleTap = false;
            _singleTapTimer = 0f;
            TriggerAtCursor(forceDeconstruct: true);
            return;
        }

        // Second tap within window -> Double-tap Deconstruct!
        if (_pendingSingleTap)
        {
            _pendingSingleTap = false;
            _singleTapTimer = 0f;
            TriggerAtCursor(forceDeconstruct: true);
            return;
        }

        // Start short window to distinguish 1 short tap (Build/Auto) vs double-tap / long-hold (Deconstruct)
        _pendingSingleTap = true;
        _singleTapTimer = DoubleTapOrHoldWindow;
    }

    public void HandleDeconstructKeyTap()
    {
        // Ignore OS key-repeat events while the deconstruct key is held down
        if (_wasDeconstructKeyDown)
            return;
        _wasDeconstructKeyDown = true;

        TriggerAtCursor(forceDeconstruct: true);
    }

    public void TriggerAtCursor(bool? forceDeconstruct = null)
    {
        _pendingSingleTap = false;
        _singleTapTimer = 0f;

        if (_activePlan != null || _blueprintQueue.Count > 0)
        {
            _log.Info("Aborting active AutoConstruct job.");
            StopJob("Aborted by user.");
            return;
        }

        if (_player.LocalEntity is not { } player || !Exists(player))
            return;

        _solver.NoteHeldEntity(_hands.GetActiveItem(player));

        var mouseScreenPos = _input.MouseScreenPosition;
        var cursorMapCoords = _eye.PixelToMap(mouseScreenPos);
        if (cursorMapCoords.MapId == MapId.Nullspace)
            return;

        var playerMapCoords = _transform.GetMapCoordinates(player);
        if (playerMapCoords.MapId != cursorMapCoords.MapId)
            return;

        var target = FindTargetAtCursor(player, cursorMapCoords, out var outOfRangeTarget, out float outOfRangeDist);
        if (target == null || !Exists(target.Value))
        {
            if (outOfRangeTarget.HasValue && Exists(outOfRangeTarget.Value))
            {
                var rangeMsg = $"Target '{GetEntityDisplayName(outOfRangeTarget.Value)}' is out of range ({outOfRangeDist:0.1}m > {SharedInteractionSystem.InteractionRange:0.1}m) — move closer.";
                _log.Info(rangeMsg);
                SetHudBanner("[AutoConstruct]", rangeMsg, isError: true, duration: 4.5f);
            }
            else
            {
                _log.Info("No constructible or deconstructible entity found at cursor.");
                SetHudBanner("[AutoConstruct]", "No constructible or deconstructible entity found at cursor.", isError: true, duration: 3.5f);
            }
            return;
        }

        SolvedConstructionPlan? plan = null;
        if (forceDeconstruct == true)
        {
            plan = _solver.SolveDeconstructPlan(target.Value);
        }
        else if (forceDeconstruct == false)
        {
            plan = _solver.SolveBuildPlan(target.Value) ?? _solver.SolveDeconstructPlan(target.Value);
        }
        else
        {
            if (IsGhostOrUnfinishedFrame(target.Value))
            {
                plan = _solver.SolveBuildPlan(target.Value) ?? _solver.SolveDeconstructPlan(target.Value);
            }
            else
            {
                plan = _solver.SolveDeconstructPlan(target.Value) ?? _solver.SolveBuildPlan(target.Value);
            }
        }

        if (plan == null || plan.Steps.Count == 0)
        {
            var modeLabel = forceDeconstruct == true ? "deconstruction" : "construction";
            var targetLabel = GetEntityDisplayName(target.Value);
            _log.Warning($"Could not solve construction plan for {ToPrettyString(target.Value)}.");
            SetHudBanner("[AutoConstruct Failed]", $"Could not solve {modeLabel} plan for '{targetLabel}'.", isError: true, duration: 5f);
            return;
        }

        StartJob(target.Value, plan);
    }
}
