using Content.Shared.Tools.Systems;
using Content.Shared.UserInterface;
using Content.Shared.Wires;
using Content.Shared.Doors.Components;
using Content.Client.Wires.UI;
using Robust.Client.Player;
using Robust.Shared.GameObjects;
using Robust.Shared.IoC;
using RobustoClient.Systems.Abstract;

namespace RobustoClient.Systems.Hacking;

public abstract record WiresIntentStep;

public sealed record EnsurePanelOpenStep : WiresIntentStep;

public sealed record FindWireStep(string WireName, string Binding, bool FindAll = false, int MinimumCount = 1, bool Optional = false) : WiresIntentStep;

public sealed record PerformWireActionStep(string Binding, WiresAction Action, string ToolQuality) : WiresIntentStep;

public sealed record VerifyWireStateStep(string Binding, bool? ExpectedCut = null) : WiresIntentStep;

public sealed record UseWiresIntentStep(string IntentName) : WiresIntentStep;

/// <summary>Runs a prerequisite intent when the target's door bolts are down.</summary>
public sealed record UseIntentIfBoltedStep(string IntentName) : WiresIntentStep;

/// <summary>Uses a tool on the target and waits for the specified upstream door state.</summary>
/// <param name="ToolQuality">The upstream quality used to select a tool.</param>
/// <param name="ExpectedState">The door state required for completion.</param>
public sealed record UseToolOnTargetStep(string ToolQuality, DoorState ExpectedState) : WiresIntentStep;

public sealed record WiresIntentDefinition(string Name, IReadOnlyList<WiresIntentStep> Steps, bool AllowNormallyAvoidedWires = false);

public readonly record struct WiresWireAppearance(WireColor Color, WireLetter Letter);

public sealed record ResolvedWiresWire(int Id, WireColor Color, WireLetter Letter, bool IsCut, string WireName);

public abstract record SolvedWiresStep;

public sealed record SolvedWiresAction(int WireId, string WireName, WiresAction Action, string ToolQuality, bool? ExpectedCut, bool MayShock) : SolvedWiresStep;

public sealed record SolvedTargetToolAction(string ToolQuality, DoorState ExpectedState) : SolvedWiresStep;

public sealed record SolvedWiresPlan(EntityUid Target, string IntentName, IReadOnlyList<SolvedWiresStep> Actions);

/// <summary>Describes why a wire-panel solve could not be started or planned.</summary>
public enum WiresSolveFailureReason
{
    None,
    MissingPanelState,
    UnknownIntent,
    MissingSolution,
    AlreadySolving,
    MissingPanel,
    MissingTool,
    ActionFailed
}

internal enum SolvePhase
{
    OpenPanel,
    WaitPanelOpen,
    OpenWireUi,
    WaitWireUi,
    ExecuteAction,
    WaitForAction,
    WaitForTargetTool
}

/// <summary>
/// Builds and executes client-side plans against the upstream wire-panel UI.
/// </summary>
public sealed partial class RobustaWiresSolver : EntitySystem
{
    public const string PowerWireName = "wire-name-power";
    public const string LogWireName = "wire-name-log";
    public const string AiInteractWireName = "wire-name-ai-act-light";
    public const string AccessWireName = "wire-name-access";
    public const string VendingManagerWireName = "wire-name-vending-contraband";
    public const string BoltWireName = "wire-name-door-bolt";
    public const string SafetyWireName = "wire-name-door-safety";
    public const string BoltLightWireName = "wire-name-bolt-light";
    public const string TimingWireName = "wire-name-door-timer";

    private static readonly HashSet<string> MiscWireNames =
    [
        SafetyWireName,
        BoltLightWireName,
        TimingWireName,
        BoltWireName
    ];

    [Dependency] private readonly ItemUtilitySystem _itemUtility = default!;
    [Dependency] private readonly IPlayerManager _playerManager = default!;
    [Dependency] private readonly SharedToolSystem _tools = default!;
    [Dependency] private readonly SharedUserInterfaceSystem _uiSystem = default!;

    private readonly Dictionary<string, WiresIntentDefinition> _intents = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<Type, StepExpander> _stepExpanders = new();
    private ActiveSolve? _activeSolve;
    private ISawmill _log = default!;

    public override void Initialize()
    {
        base.Initialize();
        _log = Logger.GetSawmill("robusta.wires");
        RegisterStepExpanders();
        RegisterDefaultIntents();
    }

    public override void FrameUpdate(float frameTime)
    {
        base.FrameUpdate(frameTime);

        if (_activeSolve == null)
            return;

        _activeSolve.Timer -= frameTime;
        _activeSolve.Elapsed += frameTime;
        if (_activeSolve.Elapsed > 10f)
        {
            FailSolve("timed out waiting for the upstream wire-panel state");
            return;
        }

        if (_activeSolve.Timer > 0f)
            return;

        switch (_activeSolve.Phase)
        {
            case SolvePhase.WaitPanelOpen:
                AdvancePanelOpenWait(_activeSolve);
                break;
            case SolvePhase.OpenPanel:
                AdvanceOpenPanel(_activeSolve);
                break;
            case SolvePhase.WaitWireUi:
                AdvanceWireUiWait(_activeSolve);
                break;
            case SolvePhase.OpenWireUi:
                AdvanceOpenWireUi(_activeSolve);
                break;
            case SolvePhase.ExecuteAction:
                AdvanceAction(_activeSolve);
                break;
            case SolvePhase.WaitForAction:
                AdvanceActionVerification(_activeSolve);
                break;
            case SolvePhase.WaitForTargetTool:
                AdvanceTargetToolVerification(_activeSolve);
                break;
        }
    }

    /// <summary>
    /// Registers an intent or replaces an existing intent with the same name.
    /// </summary>
    /// <param name="definition">The intent definition to register.</param>
    public void RegisterIntent(WiresIntentDefinition definition)
    {
        if (string.IsNullOrWhiteSpace(definition.Name))
            throw new ArgumentException("Intent name cannot be empty.", nameof(definition));

        _intents[definition.Name] = definition;
    }

    /// <summary>
    /// Expands an intent into executable upstream wire actions using the latest observed panel state.
    /// </summary>
    /// <param name="target">The entity whose wire panel state should be used.</param>
    /// <param name="intentName">The registered intent to expand.</param>
    /// <param name="plan">The expanded plan when successful.</param>
    /// <returns><see langword="true"/> when every step can be resolved.</returns>
    public bool TryBuildPlan(EntityUid target, string intentName, out SolvedWiresPlan plan)
        => TryBuildPlan(target, intentName, out plan, out _);

    /// <summary>
    /// Expands an intent and reports the reason when the current panel cannot be planned.
    /// </summary>
    /// <param name="target">The entity whose wire-panel state should be used.</param>
    /// <param name="intentName">The registered intent to expand.</param>
    /// <param name="plan">The expanded plan when successful.</param>
    /// <param name="failureReason">The reason expansion failed.</param>
    /// <returns><see langword="true"/> when every step can be resolved.</returns>
    public bool TryBuildPlan(
        EntityUid target,
        string intentName,
        out SolvedWiresPlan plan,
        out WiresSolveFailureReason failureReason)
    {
        plan = default!;
        failureReason = WiresSolveFailureReason.None;
        if (!_states.TryGetValue(target, out var state))
        {
            failureReason = WiresSolveFailureReason.MissingPanelState;
            return false;
        }

        if (!_intents.ContainsKey(intentName))
        {
            failureReason = WiresSolveFailureReason.UnknownIntent;
            return false;
        }

        var context = new IntentExpansionContext(target, state);
        if (!ExpandIntent(intentName, context))
        {
            failureReason = WiresSolveFailureReason.MissingSolution;
            return false;
        }

        plan = new SolvedWiresPlan(target, intentName, context.Actions);
        return true;
    }

    /// <summary>
    /// Starts the generic panel workflow for an intent. The workflow opens the upstream
    /// WiresPanelComponent, opens its normal wire UI, and executes the expanded plan.
    /// </summary>
    /// <param name="target">The entity whose wire panel should be solved.</param>
    /// <param name="intentName">The registered intent to execute.</param>
    /// <returns><see langword="true"/> when the workflow was started.</returns>
    public bool SolveWiresPanelComponent(EntityUid target, string intentName)
        => TryStartSolve(target, intentName, out _);

    /// <summary>Starts a wire-panel solve and reports why it could not start.</summary>
    /// <param name="target">The entity whose wire panel should be solved.</param>
    /// <param name="intentName">The registered intent to execute.</param>
    /// <param name="failureReason">The reason the solve could not start.</param>
    /// <returns><see langword="true"/> when the workflow was started.</returns>
    public bool TryStartSolve(
        EntityUid target,
        string intentName,
        out WiresSolveFailureReason failureReason)
    {
        failureReason = WiresSolveFailureReason.None;
        if (!HasComp<WiresPanelComponent>(target))
        {
            failureReason = WiresSolveFailureReason.MissingPanel;
            return false;
        }

        if (_activeSolve != null)
        {
            failureReason = WiresSolveFailureReason.AlreadySolving;
            return false;
        }

        if (!_intents.ContainsKey(intentName))
        {
            failureReason = WiresSolveFailureReason.UnknownIntent;
            return false;
        }

        _states.Remove(target);
        _activeSolve = new ActiveSolve(target, intentName)
        {
            Phase = SolvePhase.OpenPanel,
            Timer = 0f
        };
        return true;
    }

    /// <summary>Stops the active wire-panel workflow, if any.</summary>
    public void StopSolving()
    {
        _activeSolve = null;
    }

    private bool ExpandIntent(string intentName, IntentExpansionContext context)
    {
        if (!context.IntentStack.Add(intentName) || !_intents.TryGetValue(intentName, out var definition))
            return false;

        var previousDefinition = context.Definition;
        context.Definition = definition;
        foreach (var step in definition.Steps)
        {
            if (!_stepExpanders.TryGetValue(step.GetType(), out var expandStep) || !expandStep(step, context))
            {
                context.Definition = previousDefinition;
                context.IntentStack.Remove(intentName);
                return false;
            }
        }

        context.Definition = previousDefinition;
        context.IntentStack.Remove(intentName);
        return true;
    }

    private void RegisterStepExpanders()
    {
        _stepExpanders[typeof(EnsurePanelOpenStep)] = ExpandEnsurePanelOpen;
        _stepExpanders[typeof(FindWireStep)] = ExpandFindWire;
        _stepExpanders[typeof(PerformWireActionStep)] = ExpandWireAction;
        _stepExpanders[typeof(VerifyWireStateStep)] = ExpandWireVerification;
        _stepExpanders[typeof(UseWiresIntentStep)] = ExpandNestedIntent;
        _stepExpanders[typeof(UseIntentIfBoltedStep)] = ExpandConditionalUnbolt;
        _stepExpanders[typeof(UseToolOnTargetStep)] = ExpandTargetTool;
    }

    private bool ExpandTargetTool(WiresIntentStep step, IntentExpansionContext context)
    {
        if (step is not UseToolOnTargetStep tool || !HasComp<DoorComponent>(context.Target))
            return false;

        context.Actions.Add(new SolvedTargetToolAction(tool.ToolQuality, tool.ExpectedState));
        return true;
    }

    private bool ExpandEnsurePanelOpen(WiresIntentStep step, IntentExpansionContext context) => true;

    private bool ExpandConditionalUnbolt(WiresIntentStep step, IntentExpansionContext context)
    {
        return step is UseIntentIfBoltedStep prerequisite &&
               (!TryComp<DoorBoltComponent>(context.Target, out var bolts) || !bolts.BoltsDown ||
                ExpandIntent(prerequisite.IntentName, context));
    }

    private bool ExpandNestedIntent(WiresIntentStep step, IntentExpansionContext context)
    {
        return step is UseWiresIntentStep nested && ExpandIntent(nested.IntentName, context);
    }

    private bool ExpandFindWire(WiresIntentStep step, IntentExpansionContext context)
    {
        if (step is not FindWireStep find)
            return false;

        if (find.Optional && !HasWireFeedback(context.State, find.WireName))
        {
            context.Bindings[find.Binding] = [];
            return true;
        }

        if (!TryResolveWires(context.Target, context.State, find, out var wires))
            return false;

        context.Bindings[find.Binding] = wires;
        return true;
    }

    private bool ExpandWireAction(WiresIntentStep step, IntentExpansionContext context)
    {
        if (step is not PerformWireActionStep action ||
            !context.Bindings.TryGetValue(action.Binding, out var wires) ||
            context.Definition == null)
            return false;

        foreach (var wire in wires)
        {
            var isCut = context.PlannedCuts[wire.Id];
            if (action.Action == WiresAction.Cut && isCut ||
                action.Action == WiresAction.Mend && !isCut)
                continue;

            if (action.Action == WiresAction.Pulse && isCut)
                return false;

            if (action.Action == WiresAction.Cut &&
                MiscWireNames.Contains(wire.WireName) &&
                !context.Definition.AllowNormallyAvoidedWires)
                return false;

            context.Actions.Add(new SolvedWiresAction(
                wire.Id,
                wire.WireName,
                action.Action,
                action.ToolQuality,
                action.Action switch
                {
                    WiresAction.Cut => true,
                    WiresAction.Mend => false,
                    _ => null
                },
                wire.WireName == PowerWireName));
            if (action.Action != WiresAction.Pulse)
                context.PlannedCuts[wire.Id] = action.Action == WiresAction.Cut;
        }

        return true;
    }

    private bool ExpandWireVerification(WiresIntentStep step, IntentExpansionContext context)
    {
        if (step is not VerifyWireStateStep verify || !context.Bindings.TryGetValue(verify.Binding, out var wires))
            return false;

        return verify.ExpectedCut == null || wires.All(wire => context.PlannedCuts[wire.Id] == verify.ExpectedCut.Value);
    }

    private bool TryResolveWires(EntityUid target, WiresBoundUserInterfaceState state, FindWireStep find, out List<ResolvedWiresWire> result)
    {
        result = new();
        var solution = GetSolution(target, state);
        foreach (var wire in state.WiresList)
        {
            if (!solution.TryGetValue(new WiresWireAppearance(wire.Color, wire.Letter), out var name) ||
                !string.Equals(name, find.WireName, StringComparison.OrdinalIgnoreCase))
                continue;

            result.Add(new ResolvedWiresWire(wire.Id, wire.Color, wire.Letter, wire.IsCut, name));
            if (!find.FindAll)
                break;
        }

        if (find.FindAll && find.WireName == PowerWireName && !HasConfirmedPowerSolution(target, state))
            return false;

        return result.Count >= find.MinimumCount;
    }

    private void AdvanceOpenPanel(ActiveSolve solve)
    {
        if (!TryComp<WiresPanelComponent>(solve.Target, out var panel))
        {
            FailSolve("target no longer has a WiresPanelComponent");
            return;
        }

        if (panel.Open)
        {
            solve.Phase = SolvePhase.OpenWireUi;
            solve.Timer = 0f;
            solve.Elapsed = 0f;
            return;
        }

        if (!solve.ToolRequested)
        {
            solve.ToolRequested = _itemUtility.TryEquipByPredicate(
                uid => _tools.HasQuality(uid, panel.OpeningTool),
                $"Panel:{panel.OpeningTool}");
            if (!solve.ToolRequested)
            {
                FailSolve("could not equip a screwdriver");
                return;
            }
            solve.Timer = 0.25f;
            return;
        }

        if (_itemUtility.IsBusy)
        {
            solve.Timer = 0f;
            return;
        }

        ClickTarget(solve.Target);
        solve.Phase = SolvePhase.WaitPanelOpen;
        solve.Timer = 0.25f;
        solve.Elapsed = 0f;
    }

    private void AdvancePanelOpenWait(ActiveSolve solve)
    {
        if (!TryComp<WiresPanelComponent>(solve.Target, out var panel) || !panel.Open)
        {
            solve.Timer = 0f;
            return;
        }

        solve.Phase = SolvePhase.OpenWireUi;
        solve.Timer = 0f;
        solve.ToolRequested = false;
        solve.Elapsed = 0f;
    }

    private void AdvanceOpenWireUi(ActiveSolve solve)
    {
        if (solve.RemovingSecurity && _construction.IsBusy)
        {
            solve.Elapsed = 0f;
            return;
        }

        if (TryComp<WiresPanelSecurityComponent>(solve.Target, out var security) && !security.WiresAccessible)
        {
            AdvancePanelSecurity(solve, security);
            return;
        }

        if (!_states.ContainsKey(solve.Target))
        {
            if (!solve.ToolRequested)
            {
                solve.ToolRequested = _itemUtility.TryEquipByPredicate(
                    uid => _tools.HasQuality(uid, SharedToolSystem.CutQuality), "Wirecutters") ||
                    _itemUtility.TryEquipByPredicate(
                        uid => _tools.HasQuality(uid, SharedToolSystem.PulseQuality), "Multitool");
                if (!solve.ToolRequested)
                {
                    FailSolve("could not equip wirecutters or a multitool");
                    return;
                }
            }

            if (_itemUtility.IsBusy)
            {
                solve.Timer = 0f;
                return;
            }

            ClickTarget(solve.Target);
            solve.Phase = SolvePhase.WaitWireUi;
            solve.Timer = 0.25f;
            solve.Elapsed = 0f;
            return;
        }

        StartPlan(solve);
    }

    private void AdvanceWireUiWait(ActiveSolve solve)
    {
        if (!_states.ContainsKey(solve.Target))
        {
            solve.Timer = 0f;
            return;
        }

        StartPlan(solve);
    }

    private void StartPlan(ActiveSolve solve)
    {
        if (_intents.TryGetValue(solve.IntentName, out var definition) &&
            definition.Steps.OfType<UseIntentIfBoltedStep>().FirstOrDefault() is { } prerequisite &&
            TryComp<DoorBoltComponent>(solve.Target, out var bolts) && bolts.BoltsDown)
        {
            if (TryBuildPlan(solve.Target, prerequisite.IntentName, out var unboltPlan))
            {
                solve.Plan = unboltPlan;
                solve.ActionIndex = 0;
                solve.ToolRequested = false;
                solve.Phase = SolvePhase.ExecuteAction;
                solve.Elapsed = 0f;
                solve.Unbolting = true;
                return;
            }

            if (TryStartDiscovery(solve))
                return;

            FailSolve("could not discover the bolt wire required to raise the door bolts");
            return;
        }

        if (_states.TryGetValue(solve.Target, out var state) && TryFinishUnpoweredIntent(solve, state))
            return;

        if (!TryBuildPlan(solve.Target, solve.IntentName, out var plan, out var failureReason))
        {
            if (failureReason == WiresSolveFailureReason.MissingSolution && TryStartDiscovery(solve))
                return;

            FailSolve(failureReason == WiresSolveFailureReason.MissingSolution
                ? "discovery could not resolve the intent; fully insulated gloves and observable wire feedback are required"
                : $"could not build the wire plan ({failureReason})");
            return;
        }

        solve.Plan = plan;
        solve.Phase = SolvePhase.ExecuteAction;
        solve.ActionIndex = 0;
        solve.ToolRequested = false;
        solve.Timer = 0f;
        solve.Elapsed = 0f;
    }

    private void AdvanceAction(ActiveSolve solve)
    {
        if (solve.Plan == null || solve.ActionIndex >= solve.Plan.Actions.Count)
        {
            if (solve.Unbolting)
            {
                if (TryComp<DoorBoltComponent>(solve.Target, out var bolts) && bolts.BoltsDown)
                    return;

                solve.Unbolting = false;
                StartPlan(solve);
                return;
            }

            if (solve.Discovery != null)
            {
                AdvanceDiscovery(solve);
                return;
            }

            _log.Debug($"Solved wire intent {solve.IntentName} on {ToPrettyString(solve.Target)}.");
            _activeSolve = null;
            return;
        }

        if (solve.Plan.Actions[solve.ActionIndex] is SolvedTargetToolAction toolAction)
        {
            AdvanceTargetTool(solve, toolAction);
            return;
        }

        var action = (SolvedWiresAction) solve.Plan.Actions[solve.ActionIndex];
        if (solve.Discovery is { Restoring: false } && !HasInsulatedGloves())
        {
            FailSolve("fully insulated gloves were removed before discovery");
            return;
        }
        if (!solve.ToolRequested)
        {
            solve.ToolRequested = _itemUtility.TryEquipByPredicate(
                uid => _tools.HasQuality(uid, action.ToolQuality),
                action.ToolQuality);
            if (!solve.ToolRequested)
            {
                FailSolve($"could not equip {action.ToolQuality}");
                return;
            }
        }

        if (_itemUtility.IsBusy)
        {
            solve.Timer = 0f;
            return;
        }

        if (!TryGetWireUi(solve.Target, out var ui))
        {
            FailSolve("wire UI is no longer open");
            return;
        }

        ui.PerformAction(action.WireId, action.Action);
        solve.SentWireId = action.WireId;
        solve.SentAction = action.Action;
        solve.SentStateRevision = _stateRevisions.GetValueOrDefault(solve.Target);
        solve.Phase = SolvePhase.WaitForAction;
        solve.Timer = 0f;
        solve.Elapsed = 0f;
    }

    private void AdvanceActionVerification(ActiveSolve solve)
    {
        if (solve.Plan == null || solve.SentWireId == null || solve.SentAction == null)
        {
            FailSolve("missing action state");
            return;
        }

        if (!_states.TryGetValue(solve.Target, out var state))
        {
            solve.Timer = 0f;
            return;
        }

        if (_stateRevisions.GetValueOrDefault(solve.Target) <= solve.SentStateRevision)
        {
            solve.Timer = 0f;
            return;
        }

        var wire = state.WiresList.FirstOrDefault(w => w.Id == solve.SentWireId.Value);
        var expected = ((SolvedWiresAction) solve.Plan.Actions[solve.ActionIndex]).ExpectedCut;
        if (wire == null || expected != null && wire.IsCut != expected.Value)
        {
            solve.Timer = 0f;
            return;
        }

        solve.ActionIndex++;
        solve.ToolRequested = false;
        solve.SentWireId = null;
        solve.SentAction = null;
        solve.Phase = SolvePhase.ExecuteAction;
        solve.Timer = 0f;
        solve.Elapsed = 0f;
    }

    private bool TryGetWireUi(EntityUid target, out WiresBoundUserInterface ui)
    {
        ui = default!;
        if (!TryComp<UserInterfaceComponent>(target, out var userInterface))
            return false;

        return _uiSystem.TryGetOpenUi((target, userInterface), WiresUiKey.Key, out ui);
    }

    private void ClickTarget(EntityUid target)
    {
        if (_playerManager.LocalSession?.AttachedEntity is { } player)
            _itemUtility.ClickEntity(player, target);
    }

    private void FailSolve(string reason)
    {
        if (_activeSolve != null)
            _log.Warning($"Wire intent {_activeSolve.IntentName} failed: {reason}.");
        _activeSolve = null;
    }

}