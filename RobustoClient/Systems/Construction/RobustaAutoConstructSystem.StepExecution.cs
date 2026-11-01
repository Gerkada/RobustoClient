using Content.Shared.Construction.Prototypes;
using Content.Shared.Hands;
using Content.Shared.Stacks;
using RobustoClient.Systems.Hacking;

namespace RobustoClient.Systems.Construction;

public sealed partial class RobustaAutoConstructSystem
{
    private void ExecuteCurrentStep(EntityUid player, float frameTime)
    {
        if (_activePlan == null)
            return;

        if (_stepIndex >= _activePlan.Steps.Count)
        {
            CompleteActivePlan();
            return;
        }

        // Track target across entity replacements (e.g. Ghost -> Girder -> WallSolid, Console -> ComputerFrame, or WallSolid -> Girder)
        if (!Exists(_currentTarget) || Deleted(_currentTarget))
        {
            if (TryRelocateTargetEntity(out var replacement))
            {
                _currentTarget = replacement;
                _targetCoordinates = Transform(replacement).Coordinates;
                _targetMapCoords = _transform.GetMapCoordinates(replacement);
                _rescanTimer = 0f;
                _stepRetryCount = 0;
                _equipRetryCount = 0;
                _currentDiagnosticStatus = null;

                if (_phase == StepExecutionPhase.WaitForCompletion)
                {
                    AdvanceStepOnEntityReplacement();
                }

                if (_activePlan == null || _stepIndex >= _activePlan.Steps.Count)
                {
                    _log.Info("AutoConstruct plan completed after entity replacement.");
                    CompleteActivePlan();
                    return;
                }

                _stepTotalElapsed = 0f;
                _currentTaskElapsed = 0f;
                _phase = StepExecutionPhase.PrepareAndEquip;
                _waitTimer = SettleDelay;
                return;
            }
            else
            {
                // If deconstructing to "start" / "destroyed" and the entity was deleted on the final step, we're done!
                if (_activePlan.IsDeconstruct && _stepIndex >= _activePlan.Steps.Count - 1)
                {
                    _log.Info("Target deconstructed and deleted.");
                    CompleteActivePlan();
                    return;
                }

                // If building a ghost step and the ghost was deleted, try re-spawning the ghost so we don't drop the build
                var curMissingStep = _activePlan.Steps[_stepIndex];
                if (curMissingStep.Kind == ConstructionActionKind.StartGhostConstruction &&
                    !string.IsNullOrEmpty(curMissingStep.PrototypeOrQualityOrTag) &&
                    _protoMan.TryIndex<ConstructionPrototype>(curMissingStep.PrototypeOrQualityOrTag, out var respawnCProto) &&
                    _stepTotalElapsed < JobTimeoutSeconds)
                {
                    if (_clientConstruction.TrySpawnGhost(respawnCProto, _targetCoordinates, _targetDirection, out var respawnedGhost) &&
                        respawnedGhost.HasValue && Exists(respawnedGhost.Value))
                    {
                        _currentTarget = respawnedGhost.Value;
                        _phase = StepExecutionPhase.PrepareAndEquip;
                        _waitTimer = 0.35f;
                        return;
                    }
                }

                _rescanTimer += frameTime;
                _currentDiagnosticStatus = $"Waiting for replacement entity to spawn on tile ({_stepTotalElapsed:0.0}s / {JobTimeoutSeconds:0}s)...";
                if (_stepTotalElapsed >= JobTimeoutSeconds || _rescanTimer >= TargetRescanTimeout)
                {
                    FailBlueprintOrJob($"Timed out after {JobTimeoutSeconds:0}s: target entity disappeared and no replacement spawned on tile.");
                }
                return;
            }
        }
        else
        {
            _targetCoordinates = Transform(_currentTarget).Coordinates;
            _targetMapCoords = _transform.GetMapCoordinates(_currentTarget);
        }

        var step = _activePlan.Steps[_stepIndex];

        // Skip redundant door-weld step if door is already welded shut
        if (step.Kind == ConstructionActionKind.UseTool &&
            step.Description.StartsWith("Weld door shut", StringComparison.OrdinalIgnoreCase) &&
            Exists(_currentTarget) &&
            _wiresSolver.IsDoorWelded(_currentTarget))
        {
            AdvanceToNextStep();
            return;
        }

        // Skip redundant wire-panel open step if panel is already open
        if (step.Kind == ConstructionActionKind.UseTool &&
            step.Description.StartsWith("Open wire panel", StringComparison.OrdinalIgnoreCase) &&
            Exists(_currentTarget) &&
            _wiresSolver.IsWirePanelOpen(_currentTarget))
        {
            AdvanceToNextStep();
            return;
        }

        // Skip redundant board-pry step on MachineFrame if no board is inside
        if (step.Kind == ConstructionActionKind.UseTool &&
            step.Description.StartsWith("Pry out board", StringComparison.OrdinalIgnoreCase) &&
            Exists(_currentTarget) &&
            !_solver.HasMachineFrameBoard(_currentTarget))
        {
            AdvanceToNextStep();
            return;
        }

        // Delegate BUI wire-cutting step (or Cutting step when open wire panel still has uncut BUI wires) to RobustaWiresSolver
        if (step.Kind == ConstructionActionKind.CutWires ||
            (step.Kind == ConstructionActionKind.UseTool &&
             string.Equals(step.PrototypeOrQualityOrTag, "Cutting", StringComparison.OrdinalIgnoreCase) &&
             Exists(_currentTarget) &&
             _wiresSolver.HasOpenWiresPanelWithUncutBuiWires(_currentTarget)))
        {
            ExecuteDelegatedCutWiresStep(step);
            return;
        }

        // Skip redundant anchor/unanchor step if entity is already in the desired anchored state
        if (step.Kind == ConstructionActionKind.UseTool &&
            string.Equals(step.PrototypeOrQualityOrTag, "Anchoring", StringComparison.OrdinalIgnoreCase) &&
            Exists(_currentTarget))
        {
            bool isAnchored = Transform(_currentTarget).Anchored;
            if (step.Description.StartsWith("Unanchor", StringComparison.OrdinalIgnoreCase) && !isAnchored)
            {
                AdvanceToNextStep();
                return;
            }
            if (step.Description.StartsWith("Anchor frame", StringComparison.OrdinalIgnoreCase) && isAnchored)
            {
                AdvanceToNextStep();
                return;
            }
        }

        // Skip redundant pipe-layer screwdriver step if entity is already on that pipe layer
        if (TryGetStepTargetPipeLayer(step, out int desiredStepLayer) &&
            Exists(_currentTarget) &&
            _solver.DetectEntityPipeLayer(_currentTarget, out _) == desiredStepLayer)
        {
            AdvanceToNextStep();
            return;
        }

        switch (_phase)
        {
            case StepExecutionPhase.PrepareAndEquip:
                ExecutePrepareAndEquipPhase(player, step);
                break;

            case StepExecutionPhase.EnsureWelderLit:
                ExecuteEnsureWelderLitPhase(player, step);
                break;

            case StepExecutionPhase.PerformInteraction:
                ExecutePerformInteractionPhase(player, step);
                break;

            case StepExecutionPhase.WaitForCompletion:
                ExecuteWaitForCompletionPhase(player, frameTime, step);
                break;
        }
    }

    private void ExecutePrepareAndEquipPhase(EntityUid player, ConstructionActionStep step)
    {
        if (_activePlan == null)
            return;

        if (!PrepareAndEquipForStep(player, step))
        {
            if (_equipRetryCount == 0)
                TryOpenWornStorageSlots(player);

            _equipRetryCount++;
            var reqDesc = FormatStepRequirement(step);
            _currentDiagnosticStatus = $"Searching inventory/nearby for {reqDesc} (Attempt {_equipRetryCount}, {_stepTotalElapsed:0.0}s)...";

            if (_stepTotalElapsed < 3.5f)
            {
                _waitTimer = 0.45f;
                return;
            }

            FailBlueprintOrJob($"Missing required {reqDesc} for step {_stepIndex + 1}/{_activePlan.Steps.Count} ('{step.Description}').");
            return;
        }

        _equipRetryCount = 0;

        if (step.Kind is ConstructionActionKind.UseTool or ConstructionActionKind.CutWires)
        {
            _welderIgniteRetries = 0;
            _multiToolCycleRetries = 0;
            _phase = StepExecutionPhase.EnsureWelderLit;
        }
        else
        {
            _phase = StepExecutionPhase.PerformInteraction;
        }
    }

    private void ExecuteEnsureWelderLitPhase(EntityUid player, ConstructionActionStep step)
    {
        var held = _hands.GetActiveItem(player);
        if (held == null || !Exists(held.Value))
        {
            _phase = StepExecutionPhase.PrepareAndEquip;
            return;
        }

        var requiredQuality = step.PrototypeOrQualityOrTag ?? "Cutting";

        // 1. Automatically cycle multi-tools (e.g. Jaws of Life or Power Drill) to the required tool quality
        if (!string.IsNullOrEmpty(requiredQuality) &&
            !IsToolReadyForQuality(held.Value, requiredQuality))
        {
            if (!HasOrCanCycleToolQuality(held.Value, requiredQuality))
            {
                _phase = StepExecutionPhase.PrepareAndEquip;
                return;
            }

            _welderIgniteRetries++;
            _currentDiagnosticStatus = $"Cycling {GetEntityDisplayName(held.Value)} to '{requiredQuality}' mode (Attempt {_welderIgniteRetries})...";
            if (_welderIgniteRetries > 10 && _stepTotalElapsed >= 6f)
            {
                FailBlueprintOrJob($"Could not cycle multi-tool {GetEntityDisplayName(held.Value)} to '{requiredQuality}' mode for '{step.Description}'.");
                return;
            }

            RaisePredictiveEvent(new RequestUseInHandEvent());
            _waitTimer = 0.3f;
            return;
        }

        // 2. Ignite welder if this is a Welding step
        if (string.Equals(requiredQuality, "Welding", StringComparison.OrdinalIgnoreCase) &&
            RequiresWelderIgnition(held.Value))
        {
            if (IsWelderLit(held.Value))
            {
                _litWelderUid = held.Value;
                _welderIgniteRetries = 0;
                _phase = StepExecutionPhase.PerformInteraction;
                return;
            }

            _welderIgniteRetries++;
            _currentDiagnosticStatus = $"Igniting {GetEntityDisplayName(held.Value)} (Attempt {_welderIgniteRetries})...";
            if (_welderIgniteRetries > 10 && _stepTotalElapsed >= 6f)
            {
                FailBlueprintOrJob($"Could not ignite welder {GetEntityDisplayName(held.Value)} for '{step.Description}' (check welding fuel).");
                return;
            }

            RaisePredictiveEvent(new RequestUseInHandEvent());
            _litWelderUid = held.Value;
            _waitTimer = 0.35f;
            return;
        }

        _welderIgniteRetries = 0;
        _phase = StepExecutionPhase.PerformInteraction;
    }

    private void ExecutePerformInteractionPhase(EntityUid player, ConstructionActionStep step)
    {
        _sawDoAfterForStep = false;
        _doAfterCancelledForStep = false;
        _stepWaitElapsed = 0f;
        _postDoAfterTimer = 0f;

        if (step.Kind == ConstructionActionKind.StartGhostConstruction)
        {
            if (!Exists(_currentTarget) || !HasGhostComponent(_currentTarget))
            {
                if (!string.IsNullOrEmpty(step.PrototypeOrQualityOrTag) &&
                    _protoMan.TryIndex<ConstructionPrototype>(step.PrototypeOrQualityOrTag, out var ghostCProto) &&
                    _clientConstruction.TrySpawnGhost(ghostCProto, _targetCoordinates, _targetDirection, out var newGhost) &&
                    newGhost.HasValue && Exists(newGhost.Value))
                {
                    _currentTarget = newGhost.Value;
                }
            }

            if (Exists(_currentTarget) && HasGhostComponent(_currentTarget))
            {
                StartGhostConstruction(_currentTarget);
            }
            _waitTimer = SettleDelay;
            _phase = StepExecutionPhase.WaitForCompletion;
            return;
        }

        _stepStartTarget = _currentTarget;
        _stepStartNode = _solver.TryResolveGraphAndNode(_currentTarget, out _, out var sNode, out _) && sNode != null
            ? sNode.Name
            : null;
        var activeHeldAtStart = _hands.GetActiveItem(player);
        _stepStartHeldItem = activeHeldAtStart;
        _stepStartHeldStackCount = activeHeldAtStart.HasValue && TryComp<StackComponent>(activeHeldAtStart.Value, out var startStack)
            ? startStack.Count
            : (activeHeldAtStart.HasValue ? 1 : 0);

        if (step.Kind == ConstructionActionKind.UseTool &&
            step.Description.StartsWith("Weld door shut", StringComparison.OrdinalIgnoreCase) &&
            Exists(_currentTarget) &&
            _wiresSolver.IsDoorOpen(_currentTarget))
        {
            _currentDiagnosticStatus = "Closing open door before welding shut...";
            _itemUtil.EnqueueClickEntity(_currentTarget);
            _waitTimer = 0.6f;
            _phase = StepExecutionPhase.WaitForCompletion;
            return;
        }

        _itemUtil.EnqueueClickEntity(_currentTarget);
        _waitTimer = Math.Max(SettleDelay, step.DoAfterDelay > 0f ? 0.35f : 0.2f);
        _phase = StepExecutionPhase.WaitForCompletion;
    }

    private void AdvancePastGhostStartSteps()
    {
        if (_activePlan == null || _stepIndex >= _activePlan.Steps.Count)
            return;

        var ghostStep = _activePlan.Steps[_stepIndex];
        var firstNode = ghostStep.ExpectedNextNode;
        _stepIndex++;

        if (string.IsNullOrEmpty(firstNode))
            return;

        while (_stepIndex < _activePlan.Steps.Count &&
               string.Equals(_activePlan.Steps[_stepIndex].ExpectedNextNode, firstNode, StringComparison.OrdinalIgnoreCase))
        {
            _stepIndex++;
        }
    }

    private void AdvanceStepOnEntityReplacement()
    {
        if (_activePlan == null || _stepIndex >= _activePlan.Steps.Count)
            return;

        var curStep = _activePlan.Steps[_stepIndex];
        if (curStep.Kind == ConstructionActionKind.StartGhostConstruction)
        {
            AdvancePastGhostStartSteps();
            return;
        }

        // Advance past the current step that triggered the entity replacement.
        _stepIndex++;
    }

    private void ExecuteDelegatedCutWiresStep(ConstructionActionStep step)
    {
        if (!Exists(_currentTarget))
            return;

        if (!_solver.HasWiresUserInterface(_currentTarget))
        {
            if (step.Kind == ConstructionActionKind.CutWires)
                _stepIndex++;
            _phase = StepExecutionPhase.PrepareAndEquip;
            return;
        }

        if (!_delegatedWireSolveStarted)
        {
            if (_wiresSolver.AreAllWiresCut(_currentTarget) && !_wiresSolver.IsDoorBolted(_currentTarget))
            {
                if (step.Kind == ConstructionActionKind.CutWires)
                    _stepIndex++;
                _phase = StepExecutionPhase.PrepareAndEquip;
                _waitTimer = SettleDelay;
                return;
            }

            if (!_wiresSolver.TryStartSolve(
                    _currentTarget,
                    RobustaWiresSolver.CutAllIntent,
                    out var failureReason,
                    allowNestedFromConstruction: true))
            {
                _log.Warning($"Could not start wire-cut solve on {ToPrettyString(_currentTarget)}: {failureReason}.");
                FinishCurrentPlan();
                return;
            }

            _delegatedWireSolveStarted = true;
            return;
        }

        if (_wiresSolver.IsSolving)
            return;

        _delegatedWireSolveStarted = false;
        if (_wiresSolver.AreAllWiresCut(_currentTarget) && !_wiresSolver.IsDoorBolted(_currentTarget))
        {
            if (step.Kind == ConstructionActionKind.CutWires)
                _stepIndex++;
            _rescanTimer = 0f;
            _stepRetryCount = 0;
        _equipRetryCount = 0;
            _phase = StepExecutionPhase.PrepareAndEquip;
            _waitTimer = SettleDelay;
            return;
        }

        _stepRetryCount++;
        if (_stepTotalElapsed < JobTimeoutSeconds && _stepRetryCount <= 12)
        {
            _currentDiagnosticStatus = $"Retrying internal wire cutting (Attempt {_stepRetryCount + 1}, {_stepTotalElapsed:0.0}s / {JobTimeoutSeconds:0}s)...";
            _waitTimer = 0.5f;
            return;
        }

        FailBlueprintOrJob($"Could not cut all internal wires on {GetEntityDisplayName(_currentTarget)} (check Wirecutters/Multitool or insulated gloves).");
    }
}
