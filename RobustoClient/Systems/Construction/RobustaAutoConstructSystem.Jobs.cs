using System.Reflection;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;

namespace RobustoClient.Systems.Construction;

public sealed partial class RobustaAutoConstructSystem
{
    public void StartJob(
        EntityUid target,
        SolvedConstructionPlan plan,
        bool resetTaskTimer = true,
        string? desiredEntityProtoId = null,
        Direction? targetDir = null)
    {
        if (!Exists(target) || plan.Steps.Count == 0)
            return;

        _activePlan = plan;
        _stepIndex = 0;
        _phase = StepExecutionPhase.PrepareAndEquip;
        _currentTarget = target;
        _currentTargetLabel = !string.IsNullOrEmpty(desiredEntityProtoId)
            ? desiredEntityProtoId
            : GetEntityDisplayName(target);
        _desiredBlueprintEntityProtoId = desiredEntityProtoId;
        _targetCoordinates = Transform(target).Coordinates;
        _targetMapCoords = _transform.GetMapCoordinates(target);
        _targetDirection = targetDir ?? Transform(target).LocalRotation.GetCardinalDir();
        _preExistingTileEntities.Clear();
        if (_targetMapCoords.MapId != MapId.Nullspace)
        {
            foreach (var uid in _lookup.GetEntitiesInRange(_targetMapCoords, 0.45f, LookupFlags.Uncontained))
            {
                if (uid != target && Exists(uid) && !Deleted(uid) && !HasGhostComponent(uid))
                    _preExistingTileEntities.Add(uid);
            }
        }
        _waitTimer = 0f;
        _rescanTimer = 0f;
        _stepTotalElapsed = 0f;
        if (resetTaskTimer)
            _currentTaskElapsed = 0f;
        _currentDiagnosticStatus = null;
        _hudBannerTimer = 0f;
        _wasInDoAfter = false;
        _stepRetryCount = 0;
        _equipRetryCount = 0;
        _welderIgniteRetries = 0;
        _sawDoAfterForStep = false;
        _doAfterCancelledForStep = false;
        _stepWaitElapsed = 0f;
        _postDoAfterTimer = 0f;
        _delegatedWireSolveStarted = false;

        _log.Info($"Started {(plan.IsDeconstruct ? "Deconstruct" : "Build")} plan on {ToPrettyString(target)} " +
                  $"({plan.GraphId}: {plan.StartNode} -> {plan.TargetNode}, {plan.Steps.Count} steps).");
    }

    public void StopJob(string? reason = null)
    {
        bool hadActiveWork = _activePlan != null || _blueprintQueue.Count > 0;
        _blueprintQueue.Clear();
        _activeBlueprintTask = null;
        _activeBlueprintName = null;
        _totalBlueprintTasksQueued = 0;
        _completedBlueprintTasksCount = 0;
        _currentTaskElapsed = 0f;
        FinishCurrentPlan(cancelled: true, clearGhost: true);
        if (hadActiveWork && !string.IsNullOrEmpty(reason))
            SetHudBanner("[AutoConstruct Stopped]", reason, isError: false, duration: 3f);
    }

    private void FailBlueprintOrJob(string reason)
    {
        bool wasBlueprint = _blueprintQueue.Count > 0 || _activeBlueprintTask != null;
        var modeTitle = wasBlueprint
            ? $"[Blueprint Build Failed] {_activeBlueprintName ?? "Blueprint"}"
            : (_activePlan?.IsDeconstruct == true ? "[AutoDeconstruct Failed]" : $"[AutoBuild Failed] {_currentTargetLabel}");

        _log.Warning($"{modeTitle}: {reason}");
        _blueprintQueue.Clear();
        _activeBlueprintTask = null;
        _activeBlueprintName = null;
        _totalBlueprintTasksQueued = 0;
        _completedBlueprintTasksCount = 0;
        _currentTaskElapsed = 0f;
        FinishCurrentPlan(cancelled: true, clearGhost: true);
        SetHudBanner(modeTitle, reason, isError: true, duration: 6.5f);
    }

    private void FinishCurrentPlan(bool cancelled = false, bool clearGhost = false)
    {
        _itemUtil.CancelAll();
        ExtinguishActiveWelderIfNeeded();
        if (!cancelled && _blueprintQueue.Count == 0 && !_wiresSolver.IsSolving)
        {
            _itemUtil.StowActiveTrackedItem();
        }

        if ((cancelled || clearGhost || _blueprintQueue.Count == 0) &&
            Exists(_currentTarget) && HasGhostComponent(_currentTarget))
        {
            ClearGhostEntity(_currentTarget);
        }

        _preExistingTileEntities.Clear();
        _activePlan = null;
        _stepIndex = 0;
        _phase = StepExecutionPhase.PrepareAndEquip;
        _currentTarget = EntityUid.Invalid;
        _currentDiagnosticStatus = null;
        _waitTimer = 0f;
        _rescanTimer = 0f;
        _stepTotalElapsed = 0f;
        _wasInDoAfter = false;
        _stepStartTarget = EntityUid.Invalid;
        _stepStartNode = null;
        _stepStartHeldItem = null;
        _stepStartHeldStackCount = 0;
        _stepRetryCount = 0;
        _equipRetryCount = 0;
        _welderIgniteRetries = 0;
        _sawDoAfterForStep = false;
        _doAfterCancelledForStep = false;
        _stepWaitElapsed = 0f;
        _postDoAfterTimer = 0f;
        if (_delegatedWireSolveStarted)
        {
            _wiresSolver.StopSolving();
            _delegatedWireSolveStarted = false;
        }
    }

    private void CompleteActivePlan()
    {
        if (_activePlan == null)
            return;

        bool wasDeconstruct = _activePlan.IsDeconstruct;
        var graphId = _activePlan.GraphId;
        var startNode = _activePlan.StartNode;
        var targetNode = _activePlan.TargetNode;
        var label = _currentTargetLabel;

        if (_blueprintQueue.Count > 0 && _activeBlueprintTask is { } bpTask &&
            Exists(bpTask.GridUid) && TryComp<MapGridComponent>(bpTask.GridUid, out var bpGrid))
        {
            var bpMap = _transform.ToMapCoordinates(_mapSystem.GridTileToLocal(bpTask.GridUid, bpGrid, bpTask.Tile));
            if (IsBlueprintItemAlreadySatisfied(bpMap, bpTask.Item, bpTask.Dir, bpTask.GridUid, bpGrid, bpTask.Tile))
            {
                for (int i = _blueprintQueue.Count - 1; i >= 0; i--)
                {
                    var q = _blueprintQueue[i];
                    if (q.GridUid == bpTask.GridUid &&
                        q.Tile == bpTask.Tile &&
                        string.Equals(q.Item.EntityPrototypeId, bpTask.Item.EntityPrototypeId, StringComparison.OrdinalIgnoreCase))
                    {
                        _blueprintQueue.RemoveAt(i);
                        _completedBlueprintTasksCount++;
                        break;
                    }
                }
                _activeBlueprintTask = null;
                _currentTaskElapsed = 0f;
            }
        }

        _log.Info("AutoConstruct plan completed.");
        FinishCurrentPlan(cancelled: false, clearGhost: false);

        if (_blueprintQueue.Count == 0 && !_wiresSolver.IsSolving)
        {
            SetHudBanner(
                wasDeconstruct ? "[AutoDeconstruct Complete]" : "[AutoBuild Complete]",
                $"Finished {(wasDeconstruct ? "deconstructing" : "building")} {label} ({graphId}: {startNode} -> {targetNode}).",
                isError: false,
                duration: 2.5f);
        }
    }

    private void AdvanceToNextStep()
    {
        _stepIndex++;
        _rescanTimer = 0f;
        _stepTotalElapsed = 0f;
        _stepRetryCount = 0;
        _equipRetryCount = 0;
        _currentDiagnosticStatus = null;
        _phase = StepExecutionPhase.PrepareAndEquip;
    }

    private void StartGhostConstruction(EntityUid ghostUid)
    {
        try
        {
            var method = _clientConstruction.GetType().GetMethod(
                "TryStartConstruction",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (method != null)
            {
                var parameters = method.GetParameters();
                var args = new object?[parameters.Length];
                if (parameters.Length > 0)
                    args[0] = ghostUid;
                method.Invoke(_clientConstruction, args);
                return;
            }
        }
        catch (Exception ex)
        {
            _log.Warning($"Reflection invocation for TryStartConstruction failed: {ex.Message}");
        }
    }
}
