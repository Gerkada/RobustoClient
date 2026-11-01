using Content.Shared.Stacks;

namespace RobustoClient.Systems.Construction;

public sealed partial class RobustaAutoConstructSystem
{
    private void ExecuteWaitForCompletionPhase(EntityUid player, float frameTime, ConstructionActionStep step)
    {
        if (_activePlan == null)
            return;

        // 1. If we started a ghost construction, keep rechecking and retrying until the ghost is replaced by the real entity (up to 60s)
        if (step.Kind == ConstructionActionKind.StartGhostConstruction)
        {
            if (TryRelocateNonGhostTarget(out var spawnedStructure))
            {
                _currentTarget = spawnedStructure;
                _targetCoordinates = Transform(spawnedStructure).Coordinates;
                _targetMapCoords = _transform.GetMapCoordinates(spawnedStructure);
                _rescanTimer = 0f;
                _stepTotalElapsed = 0f;
                _currentTaskElapsed = 0f;
                _stepRetryCount = 0;
                _equipRetryCount = 0;
                _currentDiagnosticStatus = null;
                AdvancePastGhostStartSteps();
                _phase = StepExecutionPhase.PrepareAndEquip;
                return;
            }

            _rescanTimer += frameTime;
            if (_stepTotalElapsed >= JobTimeoutSeconds)
            {
                FailBlueprintOrJob(
                    $"Timed out after {JobTimeoutSeconds:0}s waiting for construction ghost ({step.PrototypeOrQualityOrTag}) to spawn structure (tile obstructed, out of range, or missing material).");
                return;
            }

            if (!_sawDoAfterForStep && _rescanTimer >= StepRetryInterval)
            {
                _rescanTimer = 0f;
                _stepRetryCount++;
                _currentDiagnosticStatus =
                    $"Waiting for ghost ({step.PrototypeOrQualityOrTag}) to spawn structure (Attempt {_stepRetryCount + 1}, {_stepTotalElapsed:0.0}s / {JobTimeoutSeconds:0}s)...";
                _phase = StepExecutionPhase.PrepareAndEquip;
                return;
            }

            if (_sawDoAfterForStep && _rescanTimer >= StepRetryInterval * 1.5f)
            {
                _rescanTimer = 0f;
                _stepRetryCount++;
                _currentDiagnosticStatus =
                    $"Re-checking ghost structure spawn ({step.PrototypeOrQualityOrTag}) (Attempt {_stepRetryCount + 1}, {_stepTotalElapsed:0.0}s / {JobTimeoutSeconds:0}s)...";
                _phase = StepExecutionPhase.PrepareAndEquip;
            }
            return;
        }

        // 2. For steps with a DoAfter delay (e.g. Welding, Wrenching, Prying, Screwing), ensure the DoAfter
        // actually started and finished on the server before swapping tools or advancing to the next step.
        if (step.DoAfterDelay > 0f)
        {
            _stepWaitElapsed += frameTime;

            if (_doAfterCancelledForStep)
            {
                if (_stepTotalElapsed >= JobTimeoutSeconds)
                {
                    FailBlueprintOrJob($"Timed out after {JobTimeoutSeconds:0}s because DoAfter for '{step.Description}' kept getting interrupted.");
                    return;
                }

                _doAfterCancelledForStep = false;
                _sawDoAfterForStep = false;
                _rescanTimer = 0f;
                _stepRetryCount++;
                _currentDiagnosticStatus =
                    $"DoAfter interrupted on '{step.Description}' — retrying (Attempt {_stepRetryCount + 1}, {_stepTotalElapsed:0.0}s / {JobTimeoutSeconds:0}s)...";
                _waitTimer = 0.45f;
                _phase = StepExecutionPhase.PrepareAndEquip;
                return;
            }

            if (!_sawDoAfterForStep)
            {
                bool stepAlreadyProgressed = !Exists(_currentTarget) ||
                                             Deleted(_currentTarget) ||
                                             _currentTarget != _stepStartTarget;

                if (!stepAlreadyProgressed &&
                    step.Kind == ConstructionActionKind.UseTool &&
                    string.Equals(step.PrototypeOrQualityOrTag, "Anchoring", StringComparison.OrdinalIgnoreCase) &&
                    Exists(_currentTarget))
                {
                    bool isAnchored = Transform(_currentTarget).Anchored;
                    bool wantsUnanchor = step.Description.StartsWith("Unanchor", StringComparison.OrdinalIgnoreCase);
                    bool wantsAnchor = step.Description.StartsWith("Anchor frame", StringComparison.OrdinalIgnoreCase);
                    if ((wantsUnanchor && !isAnchored) || (wantsAnchor && isAnchored))
                        stepAlreadyProgressed = true;
                }

                if (!stepAlreadyProgressed &&
                    step.Description.StartsWith("Open wire panel", StringComparison.OrdinalIgnoreCase) &&
                    Exists(_currentTarget) &&
                    _wiresSolver.IsWirePanelOpen(_currentTarget))
                {
                    stepAlreadyProgressed = true;
                }

                if (!stepAlreadyProgressed &&
                    step.Description.StartsWith("Weld door shut", StringComparison.OrdinalIgnoreCase) &&
                    Exists(_currentTarget) &&
                    _wiresSolver.IsDoorWelded(_currentTarget))
                {
                    stepAlreadyProgressed = true;
                }

                if (!stepAlreadyProgressed &&
                    TryGetStepTargetPipeLayer(step, out int checkPipeLayer))
                {
                    if (Exists(_currentTarget) &&
                        _solver.DetectEntityPipeLayer(_currentTarget, out _) == checkPipeLayer)
                    {
                        stepAlreadyProgressed = true;
                    }
                    else if (TryRelocateReplacementEntity(_stepStartTarget, out var repPipeEnt) &&
                             _solver.DetectEntityPipeLayer(repPipeEnt, out _) == checkPipeLayer)
                    {
                        _currentTarget = repPipeEnt;
                        _targetCoordinates = Transform(repPipeEnt).Coordinates;
                        _targetMapCoords = _transform.GetMapCoordinates(repPipeEnt);
                        stepAlreadyProgressed = true;
                    }
                }

                if (!stepAlreadyProgressed &&
                    Exists(_currentTarget) &&
                    _solver.TryResolveGraphAndNode(_currentTarget, out _, out var checkNode, out _) &&
                    checkNode != null &&
                    !string.IsNullOrEmpty(step.ExpectedNextNode) &&
                    !string.Equals(step.ExpectedNextNode, _stepStartNode, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(checkNode.Name, step.ExpectedNextNode, StringComparison.OrdinalIgnoreCase))
                {
                    stepAlreadyProgressed = true;
                }

                if (!stepAlreadyProgressed && _stepWaitElapsed < Math.Min(0.95f, step.DoAfterDelay + 0.45f))
                {
                    return;
                }

                if (!stepAlreadyProgressed && step.DoAfterDelay >= 0.45f)
                {
                    if (_stepTotalElapsed >= JobTimeoutSeconds)
                    {
                        FailBlueprintOrJob($"Timed out after {JobTimeoutSeconds:0}s: step '{step.Description}' never started its DoAfter.");
                        return;
                    }

                    _stepRetryCount++;
                    _rescanTimer = 0f;
                    _currentDiagnosticStatus =
                        $"Retrying '{step.Description}' — waiting for DoAfter to start (Attempt {_stepRetryCount + 1}, {_stepTotalElapsed:0.0}s / {JobTimeoutSeconds:0}s)...";
                    _phase = StepExecutionPhase.PrepareAndEquip;
                    return;
                }
            }
            else
            {
                // Give server-side DoAfter completion and node/entity transition a brief settle buffer
                // after the client DoAfter finishes so tool swapping never cancels the server's final tick.
                _postDoAfterTimer += frameTime;
                if (_postDoAfterTimer < 0.25f)
                    return;
            }
        }

        // Verify DoorWelded transitions actually took effect before advancing
        if (step.Kind == ConstructionActionKind.UseTool &&
            step.Description.StartsWith("Weld door shut", StringComparison.OrdinalIgnoreCase) &&
            Exists(_currentTarget) &&
            !_wiresSolver.IsDoorWelded(_currentTarget))
        {
            _rescanTimer += frameTime;
            if (_rescanTimer >= StepRetryInterval)
            {
                if (_stepTotalElapsed >= JobTimeoutSeconds)
                {
                    FailBlueprintOrJob($"Timed out after {JobTimeoutSeconds:0}s: '{step.Description}' did not weld the door shut.");
                    return;
                }

                _rescanTimer = 0f;
                _stepRetryCount++;
                _currentDiagnosticStatus =
                    $"Retrying '{step.Description}' (Attempt {_stepRetryCount + 1}, {_stepTotalElapsed:0.0}s / {JobTimeoutSeconds:0}s)...";
                _phase = StepExecutionPhase.PrepareAndEquip;
            }
            return;
        }

        // Verify WirePanel open transitions actually took effect before advancing
        if (step.Kind == ConstructionActionKind.UseTool &&
            step.Description.StartsWith("Open wire panel", StringComparison.OrdinalIgnoreCase) &&
            Exists(_currentTarget) &&
            !_wiresSolver.IsWirePanelOpen(_currentTarget))
        {
            _rescanTimer += frameTime;
            if (_rescanTimer >= StepRetryInterval)
            {
                if (_stepTotalElapsed >= JobTimeoutSeconds)
                {
                    FailBlueprintOrJob($"Timed out after {JobTimeoutSeconds:0}s: '{step.Description}' did not open the wire panel.");
                    return;
                }

                _rescanTimer = 0f;
                _stepRetryCount++;
                _currentDiagnosticStatus =
                    $"Retrying '{step.Description}' (Attempt {_stepRetryCount + 1}, {_stepTotalElapsed:0.0}s / {JobTimeoutSeconds:0}s)...";
                _phase = StepExecutionPhase.PrepareAndEquip;
            }
            return;
        }

        // Verify MachineFrame board/parts pry-out actually took effect before cutting wires
        if (step.Kind == ConstructionActionKind.UseTool &&
            step.Description.StartsWith("Pry out board", StringComparison.OrdinalIgnoreCase) &&
            Exists(_currentTarget) &&
            _solver.HasMachineFrameBoard(_currentTarget))
        {
            _rescanTimer += frameTime;
            if (_rescanTimer >= StepRetryInterval)
            {
                if (_stepTotalElapsed >= JobTimeoutSeconds)
                {
                    FailBlueprintOrJob($"Timed out after {JobTimeoutSeconds:0}s: '{step.Description}' did not eject board from machine frame.");
                    return;
                }

                _rescanTimer = 0f;
                _stepRetryCount++;
                _currentDiagnosticStatus =
                    $"Retrying '{step.Description}' (Attempt {_stepRetryCount + 1}, {_stepTotalElapsed:0.0}s / {JobTimeoutSeconds:0}s)...";
                _phase = StepExecutionPhase.PrepareAndEquip;
            }
            return;
        }

        // Verify item/material insertions actually consumed or inserted the held item before stowing/advancing
        if (step.Kind is ConstructionActionKind.InsertPrototype
                      or ConstructionActionKind.InsertComponent
                      or ConstructionActionKind.InsertTag
                      or ConstructionActionKind.AddMaterial &&
            _stepStartHeldItem.HasValue &&
            Exists(_stepStartHeldItem.Value) &&
            _hands.GetActiveItem(player) == _stepStartHeldItem)
        {
            int curCount = TryComp<StackComponent>(_stepStartHeldItem.Value, out var curSt) ? curSt.Count : 1;
            bool consumed = curCount < _stepStartHeldStackCount;
            if (!consumed &&
                (string.Equals(step.PrototypeOrQualityOrTag, "MachineBoard", StringComparison.OrdinalIgnoreCase) ||
                 _solver.IsMachineBoardEntity(_stepStartHeldItem.Value)) &&
                Exists(_currentTarget) &&
                _solver.HasMachineFrameBoard(_currentTarget))
            {
                consumed = true;
            }

            if (!consumed &&
                Exists(_currentTarget) &&
                !string.IsNullOrEmpty(step.ExpectedNextNode) &&
                !string.Equals(step.ExpectedNextNode, _stepStartNode, StringComparison.OrdinalIgnoreCase) &&
                _solver.TryResolveGraphAndNode(_currentTarget, out _, out var postInsertNode, out _) &&
                postInsertNode != null &&
                string.Equals(postInsertNode.Name, step.ExpectedNextNode, StringComparison.OrdinalIgnoreCase))
            {
                consumed = true;
            }

            if (!consumed)
            {
                _rescanTimer += frameTime;
                if (_rescanTimer >= 0.85f)
                {
                    _rescanTimer = 0f;
                    _stepRetryCount++;

                    // If inserting a part/material into a MachineFrame that already has its board,
                    // that specific requirement slot may already be satisfied after 2 attempts; advance to the next step.
                    if (_stepRetryCount > 2 &&
                        string.Equals(_activePlan.GraphId, "Machine", StringComparison.OrdinalIgnoreCase) &&
                        Exists(_currentTarget) &&
                        _solver.HasMachineFrameBoard(_currentTarget) &&
                        !_solver.IsMachineBoardEntity(_stepStartHeldItem.Value))
                    {
                        AdvanceToNextStep();
                        return;
                    }

                    if (_stepTotalElapsed >= JobTimeoutSeconds)
                    {
                        FailBlueprintOrJob($"Timed out after {JobTimeoutSeconds:0}s: step '{step.Description}' did not consume or insert the held item.");
                        return;
                    }

                    _currentDiagnosticStatus =
                        $"Retrying '{step.Description}' — item not yet inserted (Attempt {_stepRetryCount + 1}, {_stepTotalElapsed:0.0}s / {JobTimeoutSeconds:0}s)...";
                    _phase = StepExecutionPhase.PrepareAndEquip;
                }
                return;
            }
        }

        // Verify Unanchor / Anchor transitions actually took effect before advancing
        if (step.Kind == ConstructionActionKind.UseTool &&
            string.Equals(step.PrototypeOrQualityOrTag, "Anchoring", StringComparison.OrdinalIgnoreCase) &&
            Exists(_currentTarget))
        {
            bool isAnchored = Transform(_currentTarget).Anchored;
            bool wantsUnanchor = step.Description.StartsWith("Unanchor", StringComparison.OrdinalIgnoreCase);
            bool wantsAnchor = step.Description.StartsWith("Anchor frame", StringComparison.OrdinalIgnoreCase);

            if ((wantsUnanchor && isAnchored) || (wantsAnchor && !isAnchored))
            {
                _rescanTimer += frameTime;
                if (_rescanTimer >= StepRetryInterval)
                {
                    // If anchoring on Primary failed and the next step(s) adjust pipe layer via Screwdriver,
                    // swap the pipe-layer Screwdriver step(s) before Anchor frame so it switches layer while unanchored first.
                    if (wantsAnchor && !isAnchored &&
                        _stepIndex + 1 < _activePlan.Steps.Count &&
                        TryGetStepTargetPipeLayer(_activePlan.Steps[_stepIndex + 1], out _))
                    {
                        var anchorStep = _activePlan.Steps[_stepIndex];
                        int insertIdx = _stepIndex;
                        while (insertIdx + 1 < _activePlan.Steps.Count &&
                               TryGetStepTargetPipeLayer(_activePlan.Steps[insertIdx + 1], out _))
                        {
                            _activePlan.Steps[insertIdx] = _activePlan.Steps[insertIdx + 1];
                            insertIdx++;
                        }
                        _activePlan.Steps[insertIdx] = anchorStep;
                        _rescanTimer = 0f;
                        _stepRetryCount = 0;
                        _equipRetryCount = 0;
                        _currentDiagnosticStatus = "Switching pipe layer via Screwdriver before anchoring...";
                        _phase = StepExecutionPhase.PrepareAndEquip;
                        return;
                    }

                    if (_stepTotalElapsed >= JobTimeoutSeconds)
                    {
                        FailBlueprintOrJob(
                            $"Timed out after {JobTimeoutSeconds:0}s: '{step.Description}' could not {(wantsAnchor ? "anchor" : "unanchor")} {_currentTargetLabel} (tile layer occupied or obstructed).");
                        return;
                    }

                    _rescanTimer = 0f;
                    _stepRetryCount++;
                    _currentDiagnosticStatus =
                        $"Retrying '{step.Description}' (Attempt {_stepRetryCount + 1}, {_stepTotalElapsed:0.0}s / {JobTimeoutSeconds:0}s)...";
                    _phase = StepExecutionPhase.PrepareAndEquip;
                }
                return;
            }
        }

        // Verify pipe-layer Screwdriver transitions (both in-place and entity-replacement)
        if (TryGetStepTargetPipeLayer(step, out int verifyPipeLayer))
        {
            if ((!Exists(_currentTarget) || Deleted(_currentTarget)) &&
                TryRelocateTargetEntity(out var relocatedPipeEnt))
            {
                _currentTarget = relocatedPipeEnt;
                _targetCoordinates = Transform(relocatedPipeEnt).Coordinates;
                _targetMapCoords = _transform.GetMapCoordinates(relocatedPipeEnt);
            }
            else if (Exists(_currentTarget) &&
                     _currentTarget == _stepStartTarget &&
                     _solver.DetectEntityPipeLayer(_currentTarget, out _) != verifyPipeLayer &&
                     TryRelocateReplacementEntity(_stepStartTarget, out var replacedPipeEnt))
            {
                _currentTarget = replacedPipeEnt;
                _targetCoordinates = Transform(replacedPipeEnt).Coordinates;
                _targetMapCoords = _transform.GetMapCoordinates(replacedPipeEnt);
            }

            if (Exists(_currentTarget) &&
                _solver.DetectEntityPipeLayer(_currentTarget, out _) != verifyPipeLayer)
            {
                _rescanTimer += frameTime;
                if (_rescanTimer >= StepRetryInterval)
                {
                    if (_stepTotalElapsed >= JobTimeoutSeconds)
                    {
                        FailBlueprintOrJob($"Timed out after {JobTimeoutSeconds:0}s: '{step.Description}' did not change pipe layer on {_currentTargetLabel}.");
                        return;
                    }

                    _rescanTimer = 0f;
                    _stepRetryCount++;
                    _currentDiagnosticStatus =
                        $"Retrying '{step.Description}' (Attempt {_stepRetryCount + 1}, {_stepTotalElapsed:0.0}s / {JobTimeoutSeconds:0}s)...";
                    _phase = StepExecutionPhase.PrepareAndEquip;
                }
                return;
            }
        }

        // If this step is the final step of an edge that replaces/deletes the entity
        // (e.g. Console -> ComputerFrame, Machine -> MachineFrame, Wall -> Girder, or -> start),
        // wait for the entity replacement before advancing.
        bool isFinalStepOfEdge =
            !string.IsNullOrEmpty(step.ExpectedNextNode) &&
            (_stepIndex + 1 >= _activePlan.Steps.Count ||
             !string.Equals(_activePlan.Steps[_stepIndex + 1].ExpectedNextNode, step.ExpectedNextNode, StringComparison.OrdinalIgnoreCase));

        bool expectsEntityReplacement =
            isFinalStepOfEdge &&
            Exists(_currentTarget) &&
            _solver.DoesEdgeReplaceOrDeleteEntity(_currentTarget, _activePlan.GraphId, step.ExpectedNextNode);

        if (expectsEntityReplacement && Exists(_currentTarget) && _currentTarget == _stepStartTarget)
        {
            if (TryRelocateReplacementEntity(_stepStartTarget, out var spawnedReplacement))
            {
                _currentTarget = spawnedReplacement;
                _targetCoordinates = Transform(spawnedReplacement).Coordinates;
                _targetMapCoords = _transform.GetMapCoordinates(spawnedReplacement);
                AdvanceToNextStep();
                return;
            }

            _rescanTimer += frameTime;
            if (_rescanTimer >= StepRetryInterval)
            {
                if (_stepTotalElapsed >= JobTimeoutSeconds)
                {
                    FailBlueprintOrJob($"Timed out after {JobTimeoutSeconds:0}s: step '{step.Description}' did not transition to node '{step.ExpectedNextNode}'.");
                    return;
                }

                _rescanTimer = 0f;
                _stepRetryCount++;
                _currentDiagnosticStatus =
                    $"Waiting for '{step.Description}' -> '{step.ExpectedNextNode}' (Attempt {_stepRetryCount + 1}, {_stepTotalElapsed:0.0}s / {JobTimeoutSeconds:0}s)...";
                _phase = StepExecutionPhase.PrepareAndEquip;
            }
            return;
        }

        // If this step transitions an in-place frame node (e.g. MachineFrame missingWires <-> machineFrame,
        // ComputerFrame stages 0..4, or Airlock panel security nodes) without replacing the entity, verify the node changed before advancing.
        if (isFinalStepOfEdge &&
            !expectsEntityReplacement &&
            !string.IsNullOrEmpty(step.ExpectedNextNode) &&
            !string.Equals(step.ExpectedNextNode, _stepStartNode, StringComparison.OrdinalIgnoreCase) &&
            Exists(_currentTarget) &&
            _solver.CanVerifyInPlaceNodeTransition(_currentTarget, _activePlan.GraphId) &&
            _solver.TryResolveGraphAndNode(_currentTarget, out _, out var currentFrameNode, out _) &&
            currentFrameNode != null &&
            !string.Equals(currentFrameNode.Name, step.ExpectedNextNode, StringComparison.OrdinalIgnoreCase))
        {
            _rescanTimer += frameTime;
            if (_rescanTimer >= StepRetryInterval)
            {
                if (_stepTotalElapsed >= JobTimeoutSeconds)
                {
                    FailBlueprintOrJob($"Timed out after {JobTimeoutSeconds:0}s: step '{step.Description}' did not reach node '{step.ExpectedNextNode}'.");
                    return;
                }

                _rescanTimer = 0f;
                _stepRetryCount++;
                _currentDiagnosticStatus =
                    $"Waiting for node '{step.ExpectedNextNode}' on '{step.Description}' (Attempt {_stepRetryCount + 1}, {_stepTotalElapsed:0.0}s / {JobTimeoutSeconds:0}s)...";
                _phase = StepExecutionPhase.PrepareAndEquip;
            }
            return;
        }

        AdvanceToNextStep();
    }
}
