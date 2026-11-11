using System;
using System.Collections.Generic;
using System.Linq;
using Content.Shared.Doors.Components;
using Content.Shared.Wires;
using Robust.Shared.GameObjects;

namespace RobustoClient.Systems.Hacking;

public sealed partial class RobustaWiresSolver
{
    private readonly Dictionary<string, List<Dictionary<WiresWireAppearance, string>>> _solutions = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<EntityUid, WiresBoundUserInterfaceState> _states = new();
    private readonly Dictionary<EntityUid, WiresTargetSnapshot> _snapshots = new();
    private readonly Dictionary<EntityUid, int> _stateRevisions = new();
    private readonly HashSet<Dictionary<WiresWireAppearance, string>> _confirmedPowerSolutions = new();

    private bool HasConfirmedPowerSolution(EntityUid target, WiresBoundUserInterfaceState state)
    {
        return _confirmedPowerSolutions.Contains(GetSolution(target, state));
    }

    /// <summary>Describes the current confidence in a cached panel mapping.</summary>
    public enum WiresSolutionStatus
    {
        Unknown,
        Partial,
        Complete,
        Conflicted
    }

    /// <summary>
    /// Records one confirmed upstream wire name in the cache for the target panel profile.
    /// </summary>
    public bool LearnWireName(EntityUid target, int wireId, string wireName)
    {
        if (!_states.TryGetValue(target, out var state))
            return false;

        var wire = state.WiresList.FirstOrDefault(candidate => candidate.Id == wireId);
        if (wire == null || string.IsNullOrWhiteSpace(wireName))
            return false;

        return LearnWireMapping(target, state, new WiresWireAppearance(wire.Color, wire.Letter), wireName);
    }

    /// <summary>
    /// Records a complete set of confirmed upstream wire names in the cache.
    /// </summary>
    public bool LearnWireNames(EntityUid target, IReadOnlyDictionary<WiresWireAppearance, string> wireNames)
    {
        if (!_states.TryGetValue(target, out var state))
            return false;

        var solution = GetSolution(target, state);
        foreach (var (appearance, wireName) in wireNames)
        {
            if (string.IsNullOrWhiteSpace(wireName) ||
                solution.TryGetValue(appearance, out var existingName) &&
                !string.Equals(existingName, wireName, StringComparison.OrdinalIgnoreCase))
                return false;
        }

        foreach (var (appearance, wireName) in wireNames)
            LearnWireMapping(target, state, appearance, wireName);

        return true;
    }

    /// <summary>Returns whether the current panel profile has at least one learned mapping.</summary>
    public bool HasSolution(EntityUid target)
    {
        return _states.TryGetValue(target, out var state) && GetSolution(target, state).Count > 0;
    }

    /// <summary>Gets the current cache status for the target's panel profile.</summary>
    public WiresSolutionStatus GetSolutionStatus(EntityUid target)
    {
        if (!_states.TryGetValue(target, out var state))
            return WiresSolutionStatus.Unknown;

        var solution = GetSolution(target, state);
        return solution.Count == 0 ? WiresSolutionStatus.Unknown : WiresSolutionStatus.Partial;
    }

    /// <summary>Supplies the latest state received from the upstream wire BUI.</summary>
    public void ObserveState(EntityUid target, WiresBoundUserInterfaceState state, WiresTargetSnapshot? snapshot = null)
    {
        _states[target] = state;
        if (snapshot != null)
            _snapshots[target] = snapshot;
        else
            _snapshots.Remove(target);
        _stateRevisions[target] = _stateRevisions.GetValueOrDefault(target) + 1;
    }

    /// <summary>Notifies the solver that an upstream BUI action was sent for an active solve.</summary>
    public void ObserveAction(EntityUid target, int wireId, WiresAction action)
    {
        if (_activeSolve?.Target != target || _activeSolve.Phase != SolvePhase.ExecuteAction)
            return;

        _phaseRetryTimer = 0f;
        _activeSolve.SentWireId = wireId;
        _activeSolve.SentAction = action;
        _activeSolve.SentStateRevision = _stateRevisions.GetValueOrDefault(target);
        _activeSolve.Phase = SolvePhase.WaitForAction;
        _activeSolve.Elapsed = 0f;
        _activeSolve.Timer = 0f;
    }

    private Dictionary<WiresWireAppearance, string> GetSolution(EntityUid target, WiresBoundUserInterfaceState state)
    {
        var key = GetProfileKey(target, state);
        if (!_solutions.TryGetValue(key, out var candidates))
        {
            candidates = [];
            _solutions[key] = candidates;
        }

        var solution = candidates.FirstOrDefault(candidate => MatchesSolution(candidate, state));
        if (solution != null)
            return solution;

        solution = new Dictionary<WiresWireAppearance, string>();
        candidates.Add(solution);
        return solution;
    }

    private static bool MatchesSolution(Dictionary<WiresWireAppearance, string> solution, WiresBoundUserInterfaceState state)
    {
        return solution.Keys.All(appearance => state.WiresList.Any(wire =>
            wire.Color == appearance.Color && wire.Letter == appearance.Letter));
    }

    private bool LearnWireMapping(
        EntityUid target,
        WiresBoundUserInterfaceState state,
        WiresWireAppearance appearance,
        string wireName)
    {
        var solution = GetSolution(target, state);
        if (solution.TryGetValue(appearance, out var existingName))
            return string.Equals(existingName, wireName, StringComparison.OrdinalIgnoreCase);

        solution[appearance] = wireName;
        if (_activeSolve?.Discovery == null &&
            string.Equals(wireName, PowerWireName, StringComparison.OrdinalIgnoreCase))
        {
            _confirmedPowerSolutions.Add(solution);
        }
        return true;
    }

    private string GetProfileKey(EntityUid target, WiresBoundUserInterfaceState state)
    {
        var profile = GetPanelProfile(target);
        return GetLayoutProfileKey(profile, state);
    }

    private static string GetLayoutProfileKey(string profile, WiresBoundUserInterfaceState state)
    {
        var statusKey = string.Join(',', state.Statuses
            .Select(status => $"{status.Key.GetType().FullName}:{status.Key}")
            .OrderBy(key => key, StringComparer.Ordinal));
        return $"{profile}|{state.BoardName}|{state.WiresList.Length}|{statusKey}";
    }

    private string GetPanelProfile(EntityUid target)
    {
        if (_snapshots.TryGetValue(target, out var snapshot))
            return snapshot.ProfileName;

        if (EntityManager == null)
            return "Airlock";

        if (IsVendingMachine(target))
            return "VendingMachine";

        if (HasComp<AirlockComponent>(target))
            return "Airlock";

        return MetaData(target).EntityPrototype?.ID ?? "unknown";
    }
}