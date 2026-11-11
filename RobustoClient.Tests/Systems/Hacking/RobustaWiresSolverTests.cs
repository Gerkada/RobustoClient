using System.Collections.Generic;
using System.Linq;
using Content.Shared.Access;
using Content.Shared.Doors;
using Content.Shared.Doors.Components;
using Content.Shared.Power;
using Content.Shared.Tools.Systems;
using Content.Shared.Wires;
using FluentAssertions;
using Robust.Shared.GameObjects;
using Robust.Shared.Maths;
using RobustoClient.Systems.Hacking;
using Xunit;

namespace RobustoClient.Tests.Systems.Hacking;

public class RobustaWiresSolverTests
{
    private static WiresBoundUserInterfaceState CreateAirlockState(
        bool powerCut = false,
        bool boltCut = false,
        StatusLightState powerLight = StatusLightState.On,
        StatusLightState boltLight = StatusLightState.Off,
        StatusLightState logLight = StatusLightState.On,
        StatusLightState aiLight = StatusLightState.On,
        StatusLightState safetyLight = StatusLightState.On,
        StatusLightState timingLight = StatusLightState.On,
        StatusLightState accessLight = StatusLightState.On)
    {
        ClientWire[] wires =
        [
            new(0, powerCut, WireColor.Red, WireLetter.α),
            new(1, powerCut, WireColor.Blue, WireLetter.β),
            new(2, boltCut, WireColor.Green, WireLetter.γ),
            new(3, false, WireColor.Orange, WireLetter.δ),
            new(4, false, WireColor.Brown, WireLetter.ε),
            new(5, false, WireColor.Gold, WireLetter.ζ)
        ];

        StatusEntry[] statuses =
        [
            new(PowerWireActionKey.Status, new StatusLightData(Color.Yellow, powerLight, "POWR")),
            new(AirlockWireStatus.BoltIndicator, new StatusLightData(Color.Red, boltLight, "BOLT")),
            new(LogWireActionKey.Status, new StatusLightData(Color.Blue, logLight, "LOG")),
            new(AirlockWireStatus.AiControlIndicator, new StatusLightData(Color.Purple, aiLight, "AI")),
            new(AirlockWireStatus.SafetyIndicator, new StatusLightData(Color.Green, safetyLight, "SAFE")),
            new(AirlockWireStatus.TimingIndicator, new StatusLightData(Color.Orange, timingLight, "TIME")),
            new(AccessWireActionKey.Status, new StatusLightData(Color.Cyan, accessLight, "ACC"))
        ];

        return new WiresBoundUserInterfaceState(wires, statuses, "Airlock", "1234", 0);
    }

    [Fact]
    public void DisablePower_UnderUnknownAndKnownMappings_BuildsExpectedPlan()
    {
        var solver = new RobustaWiresSolver();
        var target = new EntityUid(100);
        var state = CreateAirlockState();
        var snapshot = new WiresTargetSnapshot("Airlock", IsDoor: true, IsBolted: false, BoltWireCut: false);

        solver.ObserveState(target, state, snapshot);

        solver.TryBuildPlan(target, "DisablePower", out _, out var reason).Should().BeFalse();
        reason.Should().Be(WiresSolveFailureReason.MissingSolution);

        solver.LearnWireNames(target, new Dictionary<WiresWireAppearance, string>
        {
            [new(WireColor.Red, WireLetter.α)] = RobustaWiresSolver.PowerWireName,
            [new(WireColor.Blue, WireLetter.β)] = RobustaWiresSolver.PowerWireName
        }).Should().BeTrue();

        solver.TryBuildPlan(target, "DisablePower", out var plan, out reason).Should().BeTrue();
        reason.Should().Be(WiresSolveFailureReason.None);
        plan.Actions.Should().Equal(
            new SolvedWiresAction(0, RobustaWiresSolver.PowerWireName, WiresAction.Cut, SharedToolSystem.CutQuality, true, true),
            new SolvedWiresAction(1, RobustaWiresSolver.PowerWireName, WiresAction.Cut, SharedToolSystem.CutQuality, true, true));
    }

    [Fact]
    public void PryingOpen_OnUnboltedDoor_CutsLogAiAndPowerThenPries()
    {
        var solver = new RobustaWiresSolver();
        var target = new EntityUid(101);
        var state = CreateAirlockState();
        var snapshot = new WiresTargetSnapshot("Airlock", IsDoor: true, IsBolted: false, BoltWireCut: false);

        solver.ObserveState(target, state, snapshot);
        solver.LearnWireNames(target, new Dictionary<WiresWireAppearance, string>
        {
            [new(WireColor.Red, WireLetter.α)] = RobustaWiresSolver.PowerWireName,
            [new(WireColor.Blue, WireLetter.β)] = RobustaWiresSolver.PowerWireName,
            [new(WireColor.Green, WireLetter.γ)] = RobustaWiresSolver.BoltWireName,
            [new(WireColor.Orange, WireLetter.δ)] = RobustaWiresSolver.LogWireName,
            [new(WireColor.Brown, WireLetter.ε)] = RobustaWiresSolver.AiInteractWireName
        }).Should().BeTrue();

        solver.TryBuildPlan(target, "PryingOpen", out var plan, out var reason).Should().BeTrue();
        reason.Should().Be(WiresSolveFailureReason.None);

        plan.Actions.Should().Equal(
            new SolvedWiresAction(3, RobustaWiresSolver.LogWireName, WiresAction.Cut, SharedToolSystem.CutQuality, true, false),
            new SolvedWiresAction(4, RobustaWiresSolver.AiInteractWireName, WiresAction.Cut, SharedToolSystem.CutQuality, true, false),
            new SolvedWiresAction(0, RobustaWiresSolver.PowerWireName, WiresAction.Cut, SharedToolSystem.CutQuality, true, true),
            new SolvedWiresAction(1, RobustaWiresSolver.PowerWireName, WiresAction.Cut, SharedToolSystem.CutQuality, true, true),
            new SolvedTargetToolAction("Prying", DoorState.Open));
    }

    [Fact]
    public void PryingOpen_OnBoltedDoorWithCutBoltWire_MendsAndPulsesBoltBeforeCuttingPowerAndPrying()
    {
        var solver = new RobustaWiresSolver();
        var target = new EntityUid(102);
        var state = CreateAirlockState(boltCut: true, boltLight: StatusLightState.On);
        var snapshot = new WiresTargetSnapshot("Airlock", IsDoor: true, IsBolted: true, BoltWireCut: true);

        solver.ObserveState(target, state, snapshot);
        solver.LearnWireNames(target, new Dictionary<WiresWireAppearance, string>
        {
            [new(WireColor.Red, WireLetter.α)] = RobustaWiresSolver.PowerWireName,
            [new(WireColor.Blue, WireLetter.β)] = RobustaWiresSolver.PowerWireName,
            [new(WireColor.Green, WireLetter.γ)] = RobustaWiresSolver.BoltWireName,
            [new(WireColor.Orange, WireLetter.δ)] = RobustaWiresSolver.LogWireName,
            [new(WireColor.Brown, WireLetter.ε)] = RobustaWiresSolver.AiInteractWireName
        }).Should().BeTrue();

        solver.TryBuildPlan(target, "PryingOpen", out var plan, out var reason).Should().BeTrue();
        reason.Should().Be(WiresSolveFailureReason.None);

        plan.Actions.Should().Equal(
            new SolvedWiresAction(2, RobustaWiresSolver.BoltWireName, WiresAction.Mend, SharedToolSystem.CutQuality, false, false),
            new SolvedWiresAction(2, RobustaWiresSolver.BoltWireName, WiresAction.Pulse, SharedToolSystem.PulseQuality, null, false),
            new SolvedWiresAction(3, RobustaWiresSolver.LogWireName, WiresAction.Cut, SharedToolSystem.CutQuality, true, false),
            new SolvedWiresAction(4, RobustaWiresSolver.AiInteractWireName, WiresAction.Cut, SharedToolSystem.CutQuality, true, false),
            new SolvedWiresAction(0, RobustaWiresSolver.PowerWireName, WiresAction.Cut, SharedToolSystem.CutQuality, true, true),
            new SolvedWiresAction(1, RobustaWiresSolver.PowerWireName, WiresAction.Cut, SharedToolSystem.CutQuality, true, true),
            new SolvedTargetToolAction("Prying", DoorState.Open));
    }

    [Fact]
    public void PryingOpen_OnUnpoweredUnboltedAirlock_SchedulesImmediatePryingWithoutWireMappings()
    {
        var solver = new RobustaWiresSolver();
        var target = new EntityUid(103);
        var unpoweredState = CreateAirlockState(powerLight: StatusLightState.Off);
        var snapshot = new WiresTargetSnapshot("Airlock", IsDoor: true, IsBolted: false, BoltWireCut: false);

        solver.ObserveState(target, unpoweredState, snapshot);

        solver.TryBuildPlan(target, "PryingOpen", out var plan, out var reason).Should().BeTrue();
        reason.Should().Be(WiresSolveFailureReason.None);
        plan.Actions.Should().ContainSingle()
            .Which.Should().Be(new SolvedTargetToolAction("Prying", DoorState.Open));
    }

    [Fact]
    public void CutAll_SeversPowerWiresFirstAndBoltWireLast()
    {
        var solver = new RobustaWiresSolver();
        var target = new EntityUid(104);
        var state = CreateAirlockState();
        var snapshot = new WiresTargetSnapshot("Airlock", IsDoor: true, IsBolted: false, BoltWireCut: false);

        solver.ObserveState(target, state, snapshot);
        solver.LearnWireNames(target, new Dictionary<WiresWireAppearance, string>
        {
            [new(WireColor.Red, WireLetter.α)] = RobustaWiresSolver.SafetyWireName,
            [new(WireColor.Blue, WireLetter.β)] = RobustaWiresSolver.BoltWireName,
            [new(WireColor.Green, WireLetter.γ)] = RobustaWiresSolver.PowerWireName,
            [new(WireColor.Orange, WireLetter.δ)] = RobustaWiresSolver.LogWireName,
            [new(WireColor.Brown, WireLetter.ε)] = RobustaWiresSolver.PowerWireName,
            [new(WireColor.Gold, WireLetter.ζ)] = RobustaWiresSolver.TimingWireName
        }).Should().BeTrue();

        solver.TryBuildPlan(target, RobustaWiresSolver.CutAllIntent, out var plan, out var reason).Should().BeTrue();
        reason.Should().Be(WiresSolveFailureReason.None);

        var cutActions = plan.Actions.Cast<SolvedWiresAction>().ToArray();
        cutActions.Should().HaveCount(6);

        cutActions[0].WireId.Should().Be(2);
        cutActions[0].WireName.Should().Be(RobustaWiresSolver.PowerWireName);
        cutActions[1].WireId.Should().Be(4);
        cutActions[1].WireName.Should().Be(RobustaWiresSolver.PowerWireName);

        cutActions[^1].WireId.Should().Be(1);
        cutActions[^1].WireName.Should().Be(RobustaWiresSolver.BoltWireName);
    }

    [Fact]
    public void IdentifyProbedWire_AndStatusesRestored_CorrectlyMapStatusLightDeltas()
    {
        var baseline = CreateAirlockState();

        var afterPowerBlink = CreateAirlockState(powerLight: StatusLightState.BlinkingSlow);
        var afterPowerOff = CreateAirlockState(powerLight: StatusLightState.Off);
        var afterSafetyOff = CreateAirlockState(safetyLight: StatusLightState.Off);
        var afterTimingOff = CreateAirlockState(timingLight: StatusLightState.Off);
        var afterBoltOn = CreateAirlockState(boltLight: StatusLightState.On);
        var afterLogOff = CreateAirlockState(logLight: StatusLightState.Off);
        var afterAccessBlink = CreateAirlockState(accessLight: StatusLightState.BlinkingFast);

        RobustaWiresSolver.IdentifyProbedWire(baseline, afterPowerBlink).Should().Be(RobustaWiresSolver.PowerWireName);
        RobustaWiresSolver.IdentifyProbedWire(baseline, afterPowerOff).Should().Be(RobustaWiresSolver.PowerWireName);
        RobustaWiresSolver.IdentifyProbedWire(baseline, afterSafetyOff).Should().Be(RobustaWiresSolver.SafetyWireName);
        RobustaWiresSolver.IdentifyProbedWire(baseline, afterTimingOff).Should().Be(RobustaWiresSolver.TimingWireName);
        RobustaWiresSolver.IdentifyProbedWire(baseline, afterBoltOn).Should().Be(RobustaWiresSolver.BoltWireName);
        RobustaWiresSolver.IdentifyProbedWire(baseline, afterLogOff).Should().Be(RobustaWiresSolver.LogWireName);
        RobustaWiresSolver.IdentifyProbedWire(baseline, afterAccessBlink).Should().Be(RobustaWiresSolver.AccessWireName);

        var snapBefore = new WiresTargetSnapshot("Airlock", IsDoor: true, IsBolted: true, BoltWireCut: false);
        var snapAfter = new WiresTargetSnapshot("Airlock", IsDoor: true, IsBolted: true, BoltWireCut: true);
        RobustaWiresSolver.IdentifyProbedWire(baseline, baseline, snapBefore, snapAfter).Should().Be(RobustaWiresSolver.BoltWireName);

        RobustaWiresSolver.StatusesRestored(baseline, afterSafetyOff).Should().BeFalse();
        RobustaWiresSolver.StatusesRestored(baseline, CreateAirlockState()).Should().BeTrue();
    }

    [Fact]
    public void CacheRetentionAndConflictRejection_RetainsAcrossEntitiesAndRejectsConflicts()
    {
        var solver = new RobustaWiresSolver();
        var doorA = new EntityUid(201);
        var doorB = new EntityUid(202);
        var state = CreateAirlockState();
        var snapshot = new WiresTargetSnapshot("Airlock", IsDoor: true, IsBolted: false, BoltWireCut: false);

        solver.ObserveState(doorA, state, snapshot);
        solver.GetSolutionStatus(doorA).Should().Be(RobustaWiresSolver.WiresSolutionStatus.Unknown);

        solver.LearnWireName(doorA, 0, RobustaWiresSolver.PowerWireName).Should().BeTrue();
        solver.LearnWireName(doorA, 1, RobustaWiresSolver.PowerWireName).Should().BeTrue();
        solver.HasSolution(doorA).Should().BeTrue();
        solver.GetSolutionStatus(doorA).Should().Be(RobustaWiresSolver.WiresSolutionStatus.Partial);

        solver.LearnWireName(doorA, 0, RobustaWiresSolver.PowerWireName).Should().BeTrue();

        solver.LearnWireName(doorA, 0, RobustaWiresSolver.BoltWireName).Should().BeFalse();
        solver.LearnWireNames(doorA, new Dictionary<WiresWireAppearance, string>
        {
            [new(WireColor.Red, WireLetter.α)] = RobustaWiresSolver.SafetyWireName
        }).Should().BeFalse();

        solver.ObserveState(doorB, state, snapshot);
        solver.HasSolution(doorB).Should().BeTrue();
        solver.TryBuildPlan(doorB, "DisablePower", out var planB, out _).Should().BeTrue();
        planB.Actions.Should().HaveCount(2);
    }
}
