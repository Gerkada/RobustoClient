using Content.Shared.Access;
using Content.Shared.Doors;
using Content.Shared.Doors.Components;
using Content.Shared.Electrocution;
using Content.Shared.Inventory;
using Content.Shared.Power;
using Content.Shared.Speech;
using Content.Shared.Tools.Systems;
using Content.Shared.VendingMachines;
using Content.Shared.Wires;

namespace RobustoClient.Systems.Hacking;

public sealed partial class RobustaWiresSolver
{
    [Dependency] private readonly InventorySystem _inventory = default!;

    private sealed class WireDiscovery
    {
        public readonly HashSet<int> Attempted = new();
        public WiresBoundUserInterfaceState? Baseline;
        public ClientWire? Wire;
        public string? Name;
        public bool Restoring;
        public bool RestoringBolts;
        public bool BoltWireWasCut;
        public readonly List<int> PowerWires = new();
        public WiresBoundUserInterfaceState? PowerBaseline;
    }

    private bool TryStartDiscovery(ActiveSolve solve)
    {
        if (!HasInsulatedGloves() || !_states.TryGetValue(solve.Target, out var state))
            return false;

        if (state.Statuses.Length == 0 || state.Statuses.Any(status => status.Key is not
            (AirlockWireStatus or AccessWireActionKey or LogWireActionKey or PowerWireActionKey or ContrabandWireKey or EjectWireKey or SpeechWireActionKey)))
            return false;

        solve.Discovery ??= new WireDiscovery();
        var discovery = solve.Discovery;
        var solution = GetSolution(solve.Target, state);
        var wire = state.WiresList.FirstOrDefault(candidate =>
            !candidate.IsCut && !discovery.Attempted.Contains(candidate.Id) &&
            (!solution.TryGetValue(new WiresWireAppearance(candidate.Color, candidate.Letter), out var name) ||
             name == PowerWireName && !HasConfirmedPowerSolution(solve.Target, state)));
        if (wire == null)
            return false;

        discovery.Attempted.Add(wire.Id);
        discovery.Baseline = state;
        discovery.Wire = wire;
        discovery.Name = null;
        discovery.Restoring = false;
        discovery.RestoringBolts = false;
        discovery.BoltWireWasCut = TryComp<DoorBoltComponent>(solve.Target, out var bolts) && bolts.BoltWireCut;
        _log.Info($"Discovering wire {wire.Id} on {ToPrettyString(solve.Target)}.");
        SetDiscoveryAction(solve, wire.Id, WiresAction.Cut);
        return true;
    }

    private bool HasInsulatedGloves()
    {
        return _playerManager.LocalEntity is { } player &&
               _inventory.TryGetSlotEntity(player, "gloves", out var gloves) &&
               TryComp<InsulatedComponent>(gloves.Value, out var insulation) &&
               insulation.Coefficient == 0f;
    }

    private void AdvanceDiscovery(ActiveSolve solve)
    {
        var discovery = solve.Discovery!;
        if (discovery.Wire == null || discovery.Baseline == null ||
            !_states.TryGetValue(solve.Target, out var state))
        {
            FailSolve("discovery lost its panel state");
            return;
        }

        if (!discovery.Restoring)
        {
            discovery.Name = IdentifyProbedWire(discovery.Baseline, state);
            if (TryComp<DoorBoltComponent>(solve.Target, out var boltState) &&
                boltState.BoltWireCut && !discovery.BoltWireWasCut)
                discovery.Name = BoltWireName;

            if (discovery.Name == PowerWireName &&
                (!TryComp<DoorBoltComponent>(solve.Target, out var bolts) || !bolts.BoltsDown))
            {
                discovery.PowerBaseline ??= discovery.Baseline;
                discovery.PowerWires.Add(discovery.Wire.Id);
                LearnWireName(solve.Target, discovery.Wire.Id, PowerWireName);
                if (GetLight(state, PowerWireActionKey.Status) != StatusLightState.Off)
                {
                    if (!TryStartDiscovery(solve))
                        FailSolve("power discovery exhausted candidates before power was disabled");
                    return;
                }

                _confirmedPowerSolutions.Add(GetSolution(solve.Target, state));
                if (TryFinishUnpoweredIntent(solve, state))
                    return;

                discovery.Baseline = discovery.PowerBaseline;
                discovery.Restoring = true;
                solve.Plan = new SolvedWiresPlan(solve.Target, solve.IntentName,
                    discovery.PowerWires.Select(id => (SolvedWiresStep) new SolvedWiresAction(
                        id, PowerWireName, WiresAction.Mend, SharedToolSystem.CutQuality, false, true)).ToArray());
                solve.ActionIndex = 0;
                solve.ToolRequested = false;
                solve.Phase = SolvePhase.ExecuteAction;
                solve.Elapsed = 0f;
                return;
            }

            discovery.Restoring = true;
            SetDiscoveryAction(solve, discovery.Wire.Id, WiresAction.Mend);
            return;
        }

        // Mending BOLT restores the circuit, not the raised bolts.
        if (!discovery.RestoringBolts && discovery.Name == BoltWireName &&
            GetLight(discovery.Baseline, AirlockWireStatus.BoltIndicator) == StatusLightState.Off &&
            GetLight(state, AirlockWireStatus.BoltIndicator) == StatusLightState.On)
        {
            discovery.RestoringBolts = true;
            SetDiscoveryAction(solve, discovery.Wire.Id, WiresAction.Pulse);
            return;
        }

        if (!StatusesRestored(discovery.Baseline, state))
        {
            FailSolve("probe was mended but its status lights did not return to their original state");
            return;
        }

        if (discovery.Name == PowerWireName)
        {
            discovery.PowerWires.Clear();
            discovery.PowerBaseline = null;
        }

        if (discovery.Name != null)
        {
            if (!LearnWireName(solve.Target, discovery.Wire.Id, discovery.Name))
            {
                FailSolve("discovered wire conflicts with the cached mapping");
                return;
            }
            _log.Info($"Confirmed wire {discovery.Wire.Id}: {discovery.Name}.");
        }

        if (TryComp<DoorBoltComponent>(solve.Target, out var currentBolts) && currentBolts.BoltsDown &&
            TryBuildPlan(solve.Target, "Unbolt", out _, out _) ||
            TryBuildPlan(solve.Target, solve.IntentName, out _, out _))
        {
            solve.Discovery = null;
            StartPlan(solve);
            return;
        }

        if (!TryStartDiscovery(solve))
            FailSolve("no further observable wires to discover, or insulated gloves were removed");
    }

    private void SetDiscoveryAction(ActiveSolve solve, int wireId, WiresAction action)
    {
        var quality = action == WiresAction.Pulse ? SharedToolSystem.PulseQuality : SharedToolSystem.CutQuality;
        bool? expectedCut = action == WiresAction.Pulse ? null : action == WiresAction.Cut;
        solve.Plan = new SolvedWiresPlan(solve.Target, solve.IntentName,
            [new SolvedWiresAction(wireId, "", action, quality, expectedCut, true)]);
        solve.ActionIndex = 0;
        solve.ToolRequested = false;
        solve.Phase = SolvePhase.ExecuteAction;
        solve.Timer = 0f;
        solve.Elapsed = 0f;
    }

    private bool TryFinishUnpoweredIntent(ActiveSolve solve, WiresBoundUserInterfaceState state)
    {
        if (!CanFinishUnpoweredIntent(state,
                TryComp<DoorBoltComponent>(solve.Target, out var bolts) && bolts.BoltsDown) ||
            !_intents.TryGetValue(solve.IntentName, out var intent))
            return false;

        var finish = intent.Steps.OfType<UseToolOnTargetStep>()
            .FirstOrDefault(step => step.ExpectedState == Content.Shared.Doors.Components.DoorState.Open);
        if (finish == null)
            return false;

        // Keep partial mappings: the next powered panel can discover the remaining miscellaneous wires.
        solve.Discovery = null;
        solve.Plan = new SolvedWiresPlan(solve.Target, solve.IntentName,
            [new SolvedTargetToolAction(finish.ToolQuality, finish.ExpectedState)]);
        solve.ActionIndex = 0;
        solve.ToolRequested = false;
        solve.Phase = SolvePhase.ExecuteAction;
        solve.Timer = 0f;
        solve.Elapsed = 0f;
        return true;
    }

    private static bool CanFinishUnpoweredIntent(WiresBoundUserInterfaceState state, bool boltsDown)
    {
        return !boltsDown && GetLight(state, PowerWireActionKey.Status) == StatusLightState.Off;
    }

    private static StatusLightState? GetLight(WiresBoundUserInterfaceState state, object key)
    {
        foreach (var status in state.Statuses)
        {
            if (Equals(status.Key, key) && status.Value is StatusLightData data)
                return data.State;
        }
        return null;
    }

    private static bool HasWireFeedback(WiresBoundUserInterfaceState state, string name)
    {
        object? key = name switch
        {
            LogWireName => LogWireActionKey.Status,
            AiInteractWireName => AirlockWireStatus.AiControlIndicator,
            _ => null
        };
        return key == null || state.Statuses.Any(status => Equals(status.Key, key));
    }

    private static bool StatusesRestored(WiresBoundUserInterfaceState before, WiresBoundUserInterfaceState after)
    {
        return before.Statuses.All(status => GetLight(before, status.Key) == GetLight(after, status.Key));
    }

    private static string? IdentifyProbedWire(WiresBoundUserInterfaceState before, WiresBoundUserInterfaceState after)
    {
        var changed = before.Statuses.Where(status =>
            GetLight(before, status.Key) != GetLight(after, status.Key)).ToArray();
        // A single power wire turns every powered light off; redundant power wires blink POWR instead.
        if (GetLight(before, PowerWireActionKey.Status) != GetLight(after, PowerWireActionKey.Status) &&
            GetLight(after, PowerWireActionKey.Status) is StatusLightState.BlinkingSlow or StatusLightState.Off)
            return PowerWireName;

        if (changed.Length != 1)
            return null;

        return changed[0].Key switch
        {
            AccessWireActionKey.Status => AccessWireName,
            LogWireActionKey.Status => LogWireName,
            ContrabandWireKey.StatusKey => VendingManagerWireName,
            EjectWireKey.StatusKey => "wire-name-vending-eject",
            SpeechWireActionKey.StatusKey => "wire-name-speech",
            AirlockWireStatus.BoltIndicator => BoltWireName,
            AirlockWireStatus.BoltLightIndicator => BoltLightWireName,
            AirlockWireStatus.SafetyIndicator => SafetyWireName,
            AirlockWireStatus.TimingIndicator => TimingWireName,
            AirlockWireStatus.AiControlIndicator => AiInteractWireName,
            _ => null
        };
    }
}