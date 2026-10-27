using Content.Client.Wires.UI;
using Content.Shared.Doors.Components;
using Content.Shared.VendingMachines.Components;
using Content.Shared.Wires;
using Robust.Shared.GameObjects;

namespace RobustoClient.Systems.Hacking;

public sealed partial class RobustaWiresSolver
{
    private readonly Dictionary<string, List<Dictionary<WiresWireAppearance, string>>> _solutions = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<EntityUid, WiresBoundUserInterfaceState> _states = new();
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
    /// <param name="target">The entity whose latest observed BUI state contains the wire.</param>
    /// <param name="wireId">The upstream wire ID from the observed BUI state.</param>
    /// <param name="wireName">The upstream wire localization identifier.</param>
    /// <returns><see langword="true"/> when the mapping was added or already matched.</returns>
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
    /// <param name="target">The entity whose latest observed BUI state identifies the panel profile.</param>
    /// <param name="wireNames">Confirmed visible-wire mappings using upstream localization identifiers.</param>
    /// <returns><see langword="true"/> when all mappings were accepted without conflict.</returns>
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
            solution[appearance] = wireName;

        return true;
    }

    /// <summary>Returns whether the current panel profile has at least one learned mapping.</summary>
    /// <param name="target">The panel entity.</param>
    /// <returns><see langword="true"/> when the profile has cached mappings.</returns>
    public bool HasSolution(EntityUid target)
    {
        return _states.TryGetValue(target, out var state) && GetSolution(target, state).Count > 0;
    }

    /// <summary>Gets the current cache status for the target's panel profile.</summary>
    /// <param name="target">The panel entity.</param>
    /// <returns>The current cached mapping status.</returns>
    public WiresSolutionStatus GetSolutionStatus(EntityUid target)
    {
        if (!_states.TryGetValue(target, out var state))
            return WiresSolutionStatus.Unknown;

        var solution = GetSolution(target, state);
        return solution.Count == 0 ? WiresSolutionStatus.Unknown : WiresSolutionStatus.Partial;
    }

    /// <summary>Supplies the latest state received from the upstream wire BUI.</summary>
    /// <param name="target">The BUI owner.</param>
    /// <param name="state">The upstream wire-panel state.</param>
    public void ObserveState(EntityUid target, WiresBoundUserInterfaceState state)
    {
        _states[target] = state;
        _stateRevisions[target] = _stateRevisions.GetValueOrDefault(target) + 1;
    }

    /// <summary>Notifies the solver that an upstream BUI action was sent for an active solve.</summary>
    /// <param name="target">The BUI owner.</param>
    /// <param name="wireId">The upstream wire ID.</param>
    /// <param name="action">The upstream action sent.</param>
    public void ObserveAction(EntityUid target, int wireId, WiresAction action)
    {
        if (_activeSolve?.Target != target || _activeSolve.Phase != SolvePhase.ExecuteAction)
            return;

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
        // Upstream reuses real-wire appearances by LayoutId, but may regenerate dummy appearances.
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
        if (HasComp<VendingMachineComponent>(target))
            return "VendingMachine";

        if (HasComp<AirlockComponent>(target))
            return "Airlock";

        return MetaData(target).EntityPrototype?.ID ?? "unknown";
    }
}