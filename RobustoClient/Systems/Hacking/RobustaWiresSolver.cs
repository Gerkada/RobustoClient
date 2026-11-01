using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Content.Client.Wires.UI;
using Content.Shared.DoAfter;
using Content.Shared.Doors;
using Content.Shared.Doors.Components;
using Content.Shared.Tools.Systems;
using Content.Shared.Wires;
using Robust.Client.Player;
using Robust.Shared.GameObjects;
using Robust.Shared.IoC;
using Robust.Shared.Log;
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
public sealed record UseToolOnTargetStep(string ToolQuality, DoorState ExpectedState) : WiresIntentStep;

/// <summary>Cuts all remaining uncut wires in the panel (cutting Power wires first and Bolt wire last).</summary>
public sealed record CutAllWiresStep : WiresIntentStep;

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
/// Shared by both standalone hacking (RobustaHackingSystem) and auto-deconstruction (RobustaAutoConstructSystem).
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

    private const int CheckFrameInterval = 5;
    private const float SolveTimeoutSeconds = 60f;
    private const float PhaseRetryIntervalSeconds = 1.25f;

    private readonly Dictionary<string, WiresIntentDefinition> _intents = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<Type, StepExpander> _stepExpanders = new();
    private ActiveSolve? _activeSolve;
    private ISawmill _log = default!;

    private int _frameCheckCounter;
    private float _frameDeltaAccumulator;
    private float _phaseRetryTimer;

    private string? _hudBannerHeader;
    private string? _hudBannerDetail;
    private bool _hudBannerIsError;
    private float _hudBannerTimer;

    public bool IsSolving => _activeSolve != null;

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

        if (_hudBannerTimer > 0f)
            _hudBannerTimer = Math.Max(0f, _hudBannerTimer - frameTime);

        if (_activeSolve == null)
        {
            _frameCheckCounter = 0;
            _frameDeltaAccumulator = 0f;
            _phaseRetryTimer = 0f;
            return;
        }

        if (!Exists(_activeSolve.Target) || Deleted(_activeSolve.Target))
        {
            FailSolve("target entity no longer exists");
            return;
        }

        if (_playerManager.LocalEntity is { } player)
        {
            if (IsPlayerInActiveDoAfter(player) || _construction.IsPlayerMoving(player))
                return;
        }

        _frameDeltaAccumulator += frameTime;
        _frameCheckCounter++;
        if (_frameCheckCounter < CheckFrameInterval)
            return;

        float stepDt = _frameDeltaAccumulator;
        _frameDeltaAccumulator = 0f;
        _frameCheckCounter = 0;

        _activeSolve.Timer -= stepDt;
        _activeSolve.Elapsed += stepDt;
        if (_itemUtility.IsBusy)
            _phaseRetryTimer = 0f;
        else
            _phaseRetryTimer += stepDt;

        if (_activeSolve.Elapsed > SolveTimeoutSeconds)
        {
            FailSolve($"timed out after {SolveTimeoutSeconds:0}s ({DescribeCurrentSolvePhase(_activeSolve)})");
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

    public void RegisterIntent(WiresIntentDefinition definition)
    {
        if (string.IsNullOrWhiteSpace(definition.Name))
            throw new ArgumentException("Intent name cannot be empty.", nameof(definition));

        _intents[definition.Name] = definition;
    }

    public bool TryBuildPlan(EntityUid target, string intentName, out SolvedWiresPlan plan)
        => TryBuildPlan(target, intentName, out plan, out _);

    public bool TryBuildPlan(
        EntityUid target,
        string intentName,
        out SolvedWiresPlan plan,
        out WiresSolveFailureReason failureReason)
    {
        plan = default!;
        failureReason = WiresSolveFailureReason.None;
        TrySyncOpenWiresState(target);
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

        var context = new IntentExpansionContext(target, state)
        {
            DeferredBoltWireId = _activeSolve?.Target == target ? _activeSolve.DeferredBoltWireId : null
        };
        if (!ExpandIntent(intentName, context))
        {
            failureReason = WiresSolveFailureReason.MissingSolution;
            return false;
        }

        plan = new SolvedWiresPlan(target, intentName, context.Actions);
        return true;
    }

    public bool SolveWiresPanelComponent(EntityUid target, string intentName)
        => TryStartSolve(target, intentName, out _);

    public bool TryStartSolve(
        EntityUid target,
        string intentName,
        out WiresSolveFailureReason failureReason,
        bool allowNestedFromConstruction = false)
    {
        failureReason = WiresSolveFailureReason.None;
        if (!IsWirePanelEntity(target))
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

        // If the Wires BUI is not currently open, clear any stale cached state so OpenWireUi opens it fresh;
        // if it IS already open, sync the live state immediately so we don't toggle the open BUI closed.
        if (!TrySyncOpenWiresState(target))
            _states.Remove(target);

        _activeSolve = new ActiveSolve(target, intentName, allowNestedFromConstruction)
        {
            Phase = SolvePhase.OpenPanel,
            Timer = 0f
        };
        _phaseRetryTimer = 0f;
        _hudBannerTimer = 0f;
        return true;
    }

    public void StopSolving(string? reason = null)
    {
        bool wasNested = _activeSolve?.NestedFromConstruction == true;
        if (_activeSolve != null && !string.IsNullOrEmpty(reason) && !wasNested)
        {
            ReportStatusBanner("[AutoHack Aborted]", reason, isError: false, duration: 3f);
        }
        _activeSolve = null;
        _phaseRetryTimer = 0f;
        if (!wasNested)
            _itemUtility.StowActiveTrackedItem();
    }

    public void ReportStatusBanner(string header, string detail, bool isError, float duration = 5f)
    {
        _hudBannerHeader = header;
        _hudBannerDetail = detail;
        _hudBannerIsError = isError;
        _hudBannerTimer = duration;
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
        _stepExpanders[typeof(CutAllWiresStep)] = ExpandCutAllWires;
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
               (!IsDoorBolted(context.Target) || ExpandIntent(prerequisite.IntentName, context));
    }

    private bool ExpandNestedIntent(WiresIntentStep step, IntentExpansionContext context)
    {
        return step is UseWiresIntentStep nested && ExpandIntent(nested.IntentName, context);
    }

    private bool ExpandCutAllWires(WiresIntentStep step, IntentExpansionContext context)
    {
        if (step is not CutAllWiresStep)
            return false;

        var solution = GetSolution(context.Target, context.State);
        var uncutWires = context.State.WiresList
            .Where(w => !context.PlannedCuts.GetValueOrDefault(w.Id, w.IsCut))
            .OrderBy(w =>
            {
                if (context.DeferredBoltWireId.HasValue && w.Id == context.DeferredBoltWireId.Value)
                    return 2;
                if (solution.TryGetValue(new WiresWireAppearance(w.Color, w.Letter), out var mappedName))
                {
                    if (string.Equals(mappedName, PowerWireName, StringComparison.OrdinalIgnoreCase))
                        return 0;
                    if (string.Equals(mappedName, BoltWireName, StringComparison.OrdinalIgnoreCase))
                        return 2;
                }
                return 1;
            })
            .ToList();

        foreach (var wire in uncutWires)
        {
            solution.TryGetValue(new WiresWireAppearance(wire.Color, wire.Letter), out var wireName);
            context.Actions.Add(new SolvedWiresAction(
                wire.Id,
                wireName ?? string.Empty,
                WiresAction.Cut,
                SharedToolSystem.CutQuality,
                ExpectedCut: true,
                MayShock: string.Equals(wireName, PowerWireName, StringComparison.OrdinalIgnoreCase)));
            context.PlannedCuts[wire.Id] = true;
        }

        return true;
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
            solve.ToolRequested = _construction.TryEquipToolOrMultiTool(
                panel.OpeningTool,
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

        if (!_construction.EnsureHeldToolModeReady(panel.OpeningTool, out bool failedPanelCycle))
        {
            if (failedPanelCycle)
                FailSolve($"could not cycle tool to {panel.OpeningTool}");
            else
                solve.Timer = 0.25f;
            return;
        }

        ClickTarget(solve.Target);
        _phaseRetryTimer = 0f;
        solve.Phase = SolvePhase.WaitPanelOpen;
        solve.Timer = 0.25f;
        solve.Elapsed = 0f;
    }

    private void AdvancePanelOpenWait(ActiveSolve solve)
    {
        if (!TryComp<WiresPanelComponent>(solve.Target, out var panel) || !panel.Open)
        {
            if (_phaseRetryTimer >= PhaseRetryIntervalSeconds)
            {
                _phaseRetryTimer = 0f;
                solve.Phase = SolvePhase.OpenPanel;
                solve.ToolRequested = false;
                solve.Timer = 0.15f;
                return;
            }

            solve.Timer = 0f;
            return;
        }

        _phaseRetryTimer = 0f;
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

        if (solve.RemovingSecurity)
        {
            solve.RemovingSecurity = false;
            solve.ToolRequested = false;
            solve.Elapsed = 0f;
        }

        if (!TrySyncOpenWiresState(solve.Target))
            _states.Remove(solve.Target);

        if (!_states.ContainsKey(solve.Target))
        {
            if (!solve.ToolRequested)
            {
                solve.ToolRequested = _construction.TryEquipToolOrMultiTool(SharedToolSystem.CutQuality, "Wirecutters") ||
                                      _construction.TryEquipToolOrMultiTool(SharedToolSystem.PulseQuality, "Multitool");
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

            if (!_construction.EnsureHeldWireUiToolReady(out bool failedWireUiCycle))
            {
                if (failedWireUiCycle)
                    FailSolve("could not cycle tool to wirecutters or multitool mode");
                else
                    solve.Timer = 0.25f;
                return;
            }

            ClickTarget(solve.Target);
            _phaseRetryTimer = 0f;
            solve.Phase = SolvePhase.WaitWireUi;
            solve.Timer = 0.25f;
            solve.Elapsed = 0f;
            return;
        }

        StartPlan(solve);
    }

    private void AdvanceWireUiWait(ActiveSolve solve)
    {
        if (!TrySyncOpenWiresState(solve.Target))
            _states.Remove(solve.Target);

        if (!_states.ContainsKey(solve.Target))
        {
            if (_phaseRetryTimer >= PhaseRetryIntervalSeconds)
            {
                _phaseRetryTimer = 0f;
                solve.Phase = SolvePhase.OpenWireUi;
                solve.ToolRequested = false;
                solve.Timer = 0.15f;
                return;
            }

            solve.Timer = 0f;
            return;
        }

        _phaseRetryTimer = 0f;
        StartPlan(solve);
    }

    private void StartPlan(ActiveSolve solve)
    {
        TrySyncOpenWiresState(solve.Target);
        if (_intents.TryGetValue(solve.IntentName, out var definition) &&
            definition.Steps.OfType<UseIntentIfBoltedStep>().FirstOrDefault() is { } prerequisite &&
            IsDoorBolted(solve.Target))
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
                if (IsDoorBolted(solve.Target))
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
            bool wasNested = solve.NestedFromConstruction;
            var targetLabel = Exists(solve.Target) ? Name(solve.Target) : "target";
            var solvedIntent = solve.IntentName;
            _activeSolve = null;
            _phaseRetryTimer = 0f;
            if (!wasNested)
            {
                ReportStatusBanner("[AutoHack Complete]", $"Completed '{solvedIntent}' on {targetLabel}.", isError: false, duration: 2.5f);
                _itemUtility.StowActiveTrackedItem();
            }
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
            solve.ToolRequested = _construction.TryEquipToolOrMultiTool(
                action.ToolQuality,
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

        if (!_construction.EnsureHeldToolModeReady(action.ToolQuality, out bool failedActionCycle))
        {
            if (failedActionCycle)
                FailSolve($"could not cycle tool to {action.ToolQuality}");
            else
                solve.Timer = 0.25f;
            return;
        }

        solve.WasBoltedBeforeAction = IsDoorBolted(solve.Target);
        solve.BoltWireWasCutBeforeAction = TryComp<DoorBoltComponent>(solve.Target, out var bolts) && bolts.BoltWireCut;

        var currentRevision = _stateRevisions.GetValueOrDefault(solve.Target);
        if (!TryPerformWireAction(solve.Target, action.WireId, action.Action))
        {
            _states.Remove(solve.Target);
            solve.ToolRequested = false;
            solve.Phase = SolvePhase.OpenWireUi;
            solve.Timer = 0.15f;
            return;
        }

        _phaseRetryTimer = 0f;
        solve.SentWireId = action.WireId;
        solve.SentAction = action.Action;
        solve.SentStateRevision = currentRevision;
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

        TrySyncOpenWiresState(solve.Target);
        if (!_states.TryGetValue(solve.Target, out var state))
        {
            solve.Timer = 0f;
            return;
        }

        var wire = state.WiresList.FirstOrDefault(w => w.Id == solve.SentWireId.Value);
        var expected = ((SolvedWiresAction) solve.Plan.Actions[solve.ActionIndex]).ExpectedCut;
        bool boltStateChangedAfterPulse = solve.SentAction == WiresAction.Pulse &&
                                          IsDoorBolted(solve.Target) != solve.WasBoltedBeforeAction;

        if (_stateRevisions.GetValueOrDefault(solve.Target) <= solve.SentStateRevision &&
            !boltStateChangedAfterPulse &&
            (expected == null || wire == null || wire.IsCut != expected.Value))
        {
            if (_phaseRetryTimer >= PhaseRetryIntervalSeconds)
            {
                _phaseRetryTimer = 0f;
                solve.Phase = SolvePhase.ExecuteAction;
                solve.ToolRequested = false;
                solve.Timer = 0.15f;
                return;
            }

            solve.Timer = 0f;
            return;
        }

        if (wire == null || expected != null && wire.IsCut != expected.Value)
        {
            if (_phaseRetryTimer >= PhaseRetryIntervalSeconds)
            {
                _phaseRetryTimer = 0f;
                solve.Phase = SolvePhase.ExecuteAction;
                solve.ToolRequested = false;
                solve.Timer = 0.15f;
                return;
            }

            solve.Timer = 0f;
            return;
        }

        _phaseRetryTimer = 0f;

        // If cutting a wire during CutAllIntent caused the door bolts to drop (Bolt wire cut while powered),
        // learn the Bolt wire in the cache, defer it to last, and let StartPlan run "Unbolt" (Mend + Pulse) first!
        int cutWireId = solve.SentWireId.Value;
        var sentAction = solve.SentAction.Value;
        solve.SentWireId = null;
        solve.SentAction = null;

        bool boltJustCut = TryComp<DoorBoltComponent>(solve.Target, out var boltComp) &&
                           boltComp.BoltWireCut && !solve.BoltWireWasCutBeforeAction;
        bool justBolted = !solve.WasBoltedBeforeAction && IsDoorBolted(solve.Target);

        if (boltJustCut || justBolted)
            LearnWireName(solve.Target, cutWireId, BoltWireName);

        if (sentAction == WiresAction.Cut &&
            string.Equals(solve.IntentName, CutAllIntent, StringComparison.OrdinalIgnoreCase) &&
            (justBolted || boltJustCut) &&
            IsDoorBolted(solve.Target))
        {
            LearnWireName(solve.Target, cutWireId, BoltWireName);
            solve.DeferredBoltWireId = cutWireId;
            solve.ToolRequested = false;
            StartPlan(solve);
            return;
        }

        solve.ActionIndex++;
        solve.ToolRequested = false;
        solve.Phase = SolvePhase.ExecuteAction;
        solve.Timer = 0f;
        solve.Elapsed = 0f;
    }

    // --- Shared Wire & Door Inspection Helpers (Reused by RobustaAutoConstructSystem) ---

    public bool IsWirePanelEntity(EntityUid uid)
    {
        if (HasComp<WiresPanelComponent>(uid))
            return true;

        foreach (var comp in EntityManager.GetComponents(uid))
        {
            if (string.Equals(comp.GetType().Name, "WiresPanelComponent", StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    public bool IsVendingMachine(EntityUid uid)
    {
        foreach (var comp in EntityManager.GetComponents(uid))
        {
            if (comp.GetType().Name.Contains("VendingMachine", StringComparison.OrdinalIgnoreCase))
                return true;
        }

        var protoId = MetaData(uid).EntityPrototype?.ID;
        if (!string.IsNullOrEmpty(protoId) && protoId.Contains("VendingMachine", StringComparison.OrdinalIgnoreCase))
            return true;

        return false;
    }

    public bool IsWirePanelOpen(EntityUid uid)
    {
        if (TryComp<WiresPanelComponent>(uid, out var panel))
            return panel.Open;

        foreach (var comp in EntityManager.GetComponents(uid))
        {
            if (string.Equals(comp.GetType().Name, "WiresPanelComponent", StringComparison.OrdinalIgnoreCase))
                return GetBoolMember(comp, "Open");
        }
        return true;
    }

    public bool IsDoorWelded(EntityUid uid)
    {
        foreach (var comp in EntityManager.GetComponents(uid))
        {
            if (string.Equals(comp.GetType().Name, "WeldableComponent", StringComparison.OrdinalIgnoreCase))
                return GetBoolMember(comp, "IsWelded");
        }
        return true;
    }

    public bool IsDoorOpen(EntityUid uid)
    {
        if (TryComp<DoorComponent>(uid, out var door))
            return door.State != DoorState.Closed && !IsDoorWelded(uid);

        foreach (var comp in EntityManager.GetComponents(uid))
        {
            if (string.Equals(comp.GetType().Name, "DoorComponent", StringComparison.OrdinalIgnoreCase))
            {
                var stateObj = GetMemberValue(comp, "State");
                if (stateObj != null)
                {
                    var stateStr = stateObj.ToString() ?? string.Empty;
                    return !string.Equals(stateStr, "Closed", StringComparison.OrdinalIgnoreCase) &&
                           !string.Equals(stateStr, "Welded", StringComparison.OrdinalIgnoreCase);
                }
            }
        }
        return false;
    }

    public bool IsDoorBolted(EntityUid uid)
    {
        if (TryComp<DoorBoltComponent>(uid, out var bolts) && bolts.BoltsDown)
            return true;

        if (_states.TryGetValue(uid, out var state) &&
            GetLight(state, AirlockWireStatus.BoltIndicator) == StatusLightState.On)
        {
            return true;
        }

        foreach (var comp in EntityManager.GetComponents(uid))
        {
            if (string.Equals(comp.GetType().Name, "DoorBoltComponent", StringComparison.OrdinalIgnoreCase) &&
                GetBoolMember(comp, "BoltsDown"))
            {
                return true;
            }
        }
        return false;
    }

    public bool HasOpenWiresPanelWithUncutBuiWires(EntityUid uid)
    {
        if (!IsWirePanelOpen(uid))
            return false;

        TrySyncOpenWiresState(uid);
        return _states.TryGetValue(uid, out var state) &&
               state.WiresList.Length > 0 &&
               state.WiresList.Any(w => !w.IsCut);
    }

    public bool AreAllWiresCut(EntityUid uid)
    {
        TrySyncOpenWiresState(uid);
        return _states.TryGetValue(uid, out var state) &&
               state.WiresList.Length > 0 &&
               state.WiresList.All(w => w.IsCut);
    }

    public bool TrySyncOpenWiresState(EntityUid target)
    {
        if (!TryComp<UserInterfaceComponent>(target, out var uiComp))
            return false;

        bool isWiresUiOpen = false;
        foreach (var (key, bUi) in uiComp.ClientOpenInterfaces)
        {
            var keyTypeName = key.GetType().Name;
            if (bUi is not WiresBoundUserInterface &&
                key is not WiresUiKey &&
                !keyTypeName.Contains("WiresUiKey", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            isWiresUiOpen = true;
            var stateObj = GetMemberValue(bUi, "State");
            if (stateObj is WiresBoundUserInterfaceState wiresState)
            {
                if (!_states.TryGetValue(target, out var existing) || !ReferenceEquals(existing, wiresState))
                    ObserveState(target, wiresState);
                return true;
            }
        }

        return isWiresUiOpen && _states.ContainsKey(target);
    }

    private bool TryPerformWireAction(EntityUid target, int wireId, WiresAction action)
    {
        if (TryGetWireUi(target, out var ui))
        {
            ui.PerformAction(wireId, action);
            return true;
        }

        if (!TryComp<UserInterfaceComponent>(target, out var uiComp))
            return false;

        foreach (var key in uiComp.ClientOpenInterfaces.Keys)
        {
            if (key is WiresUiKey || key.GetType().Name.Contains("WiresUiKey", StringComparison.OrdinalIgnoreCase))
            {
                _uiSystem.ClientSendUiMessage((target, uiComp), key, new WiresActionMessage(wireId, action));
                return true;
            }
        }

        return false;
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
        bool wasNested = _activeSolve?.NestedFromConstruction == true;
        if (_activeSolve != null)
        {
            var targetName = Exists(_activeSolve.Target) ? Name(_activeSolve.Target) : "target";
            _log.Warning($"Wire intent {_activeSolve.IntentName} failed: {reason}.");
            ReportStatusBanner(
                wasNested ? "[Wire Cut Failed]" : $"[AutoHack Failed] {targetName}",
                $"Reason: {reason}",
                isError: true,
                duration: 6f);
        }
        _activeSolve = null;
        _phaseRetryTimer = 0f;
        if (!wasNested)
            _itemUtility.StowActiveTrackedItem();
    }

    public bool TryGetHudStatus(
        out string header,
        out string detail,
        out string? subDetail,
        out float progress,
        out bool isError,
        out bool isComplete,
        out EntityUid? targetUid)
    {
        header = string.Empty;
        detail = string.Empty;
        subDetail = null;
        progress = 0f;
        isError = false;
        isComplete = false;
        targetUid = null;

        if (_activeSolve != null && !_activeSolve.NestedFromConstruction)
        {
            var solve = _activeSolve;
            targetUid = solve.Target;
            var targetName = Exists(solve.Target) ? Name(solve.Target) : "target";

            if (solve.RemovingSecurity)
            {
                header = $"[AutoHack] {targetName} — Bypassing Panel Security";
                detail = _construction.GetActiveStepHudSummary() ?? "Removing wire panel shielding...";
                subDetail = $"Intent: {solve.IntentName} | Elapsed: {solve.Elapsed:0.0}s / {SolveTimeoutSeconds:0}s";
                progress = _construction.GetActiveStepProgress();
                return true;
            }

            if (solve.Discovery != null)
            {
                header = $"[AutoHack] {targetName} — Discovering Wires";
                int attempted = solve.Discovery.Attempted.Count;
                int totalWires = _states.TryGetValue(solve.Target, out var st) ? Math.Max(1, st.WiresList.Length) : 6;
                var wireActionDesc = solve.Discovery.Restoring ? "Restoring probed wire" : $"Probing wire {(solve.Discovery.Wire?.Id ?? 0) + 1}";
                detail = $"{wireActionDesc} ({attempted}/{totalWires} wires checked)";
                subDetail = $"Intent: {solve.IntentName} | Elapsed: {solve.Elapsed:0.0}s / {SolveTimeoutSeconds:0}s";
                progress = Math.Clamp((float)attempted / totalWires, 0.08f, 0.9f);
                return true;
            }

            header = $"[AutoHack] {targetName} ({(solve.Unbolting ? $"Unbolting -> {solve.IntentName}" : solve.IntentName)})";
            detail = DescribeCurrentSolvePhase(solve);
            subDetail = $"Elapsed: {solve.Elapsed:0.0}s / {SolveTimeoutSeconds:0}s";

            if (solve.Plan != null && solve.Plan.Actions.Count > 0)
            {
                progress = Math.Clamp((float)(solve.ActionIndex + 1) / (solve.Plan.Actions.Count + 1), 0.15f, 0.95f);
            }
            else
            {
                progress = solve.Phase switch
                {
                    SolvePhase.OpenPanel or SolvePhase.WaitPanelOpen => 0.1f,
                    SolvePhase.OpenWireUi or SolvePhase.WaitWireUi => 0.2f,
                    _ => 0.35f
                };
            }

            return true;
        }

        if (_hudBannerTimer > 0f && !string.IsNullOrEmpty(_hudBannerHeader))
        {
            header = _hudBannerHeader!;
            detail = _hudBannerDetail ?? string.Empty;
            isError = _hudBannerIsError;
            isComplete = !_hudBannerIsError;
            progress = 1f;
            return true;
        }

        return false;
    }

    private string DescribeCurrentSolvePhase(ActiveSolve solve)
    {
        return solve.Phase switch
        {
            SolvePhase.OpenPanel => "Equipping Screwdriver & opening wire panel...",
            SolvePhase.WaitPanelOpen => "Waiting for wire panel to open...",
            SolvePhase.OpenWireUi => solve.RemovingSecurity
                ? (_construction.GetActiveStepHudSummary() ?? "Removing panel security...")
                : "Equipping Wirecutters/Multitool & opening wire UI...",
            SolvePhase.WaitWireUi => "Waiting for server wire UI state...",
            SolvePhase.ExecuteAction or SolvePhase.WaitForAction => DescribeActiveWireAction(solve),
            SolvePhase.WaitForTargetTool => DescribeActiveTargetTool(solve),
            _ => solve.Phase.ToString()
        };
    }

    private static string DescribeActiveWireAction(ActiveSolve solve)
    {
        if (solve.Plan == null || solve.ActionIndex < 0 || solve.ActionIndex >= solve.Plan.Actions.Count)
            return "Finalizing wire actions...";

        var step = solve.Plan.Actions[solve.ActionIndex];
        if (step is SolvedWiresAction wireAct)
        {
            var wireLabel = !string.IsNullOrEmpty(wireAct.WireName)
                ? $"wire #{wireAct.WireId + 1} ({wireAct.WireName})"
                : $"wire #{wireAct.WireId + 1}";
            return $"Step {solve.ActionIndex + 1}/{solve.Plan.Actions.Count}: {wireAct.Action} {wireLabel} ({wireAct.ToolQuality})";
        }

        if (step is SolvedTargetToolAction toolAct)
        {
            return $"Step {solve.ActionIndex + 1}/{solve.Plan.Actions.Count}: Use {toolAct.ToolQuality} tool (-> {toolAct.ExpectedState})";
        }

        return $"Step {solve.ActionIndex + 1}/{solve.Plan.Actions.Count}";
    }

    private static string DescribeActiveTargetTool(ActiveSolve solve)
    {
        if (solve.Plan != null &&
            solve.ActionIndex >= 0 &&
            solve.ActionIndex < solve.Plan.Actions.Count &&
            solve.Plan.Actions[solve.ActionIndex] is SolvedTargetToolAction toolAct)
        {
            return $"Waiting for {toolAct.ToolQuality} tool to reach state '{toolAct.ExpectedState}'...";
        }

        return "Waiting for target tool action to complete...";
    }

    private bool IsPlayerInActiveDoAfter(EntityUid player)
    {
        if (!TryComp<DoAfterComponent>(player, out var doAfterComp))
            return false;

        if (GetMemberValue(doAfterComp, "DoAfters") is IDictionary dict)
        {
            foreach (DictionaryEntry entry in dict)
            {
                var d = entry.Value;
                if (d == null)
                    continue;

                if (!GetBoolMember(d, "Cancelled") && !GetBoolMember(d, "Completed"))
                    return true;
            }
        }

        return false;
    }

    private static object? GetMemberValue(object? obj, string name)
    {
        if (obj == null)
            return null;

        var type = obj.GetType();
        const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.IgnoreCase;

        var prop = type.GetProperty(name, flags);
        if (prop != null)
            return prop.GetValue(obj);

        var field = type.GetField(name, flags) ?? type.GetField("_" + name, flags);
        if (field != null)
            return field.GetValue(obj);

        return null;
    }

    private static bool GetBoolMember(object? obj, string name)
        => GetMemberValue(obj, name) is true;
}