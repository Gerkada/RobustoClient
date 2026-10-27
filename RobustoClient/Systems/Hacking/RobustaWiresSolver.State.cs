using Content.Client.Wires.UI;
using Content.Shared.Wires;

namespace RobustoClient.Systems.Hacking;

public sealed partial class RobustaWiresSolver
{
    private delegate bool StepExpander(WiresIntentStep step, IntentExpansionContext context);

    private sealed class IntentExpansionContext(EntityUid target, WiresBoundUserInterfaceState state)
    {
        public EntityUid Target { get; } = target;
        public WiresBoundUserInterfaceState State { get; } = state;
        public Dictionary<string, List<ResolvedWiresWire>> Bindings { get; } = new(StringComparer.OrdinalIgnoreCase);
        public List<SolvedWiresStep> Actions { get; } = new();
        public HashSet<string> IntentStack { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<int, bool> PlannedCuts { get; } = state.WiresList.ToDictionary(wire => wire.Id, wire => wire.IsCut);
        public WiresIntentDefinition? Definition { get; set; }
    }

    private sealed class ActiveSolve(EntityUid target, string intentName)
    {
        public EntityUid Target { get; } = target;
        public string IntentName { get; } = intentName;
        public SolvePhase Phase;
        public float Timer;
        public float Elapsed;
        public bool ToolRequested;
        public SolvedWiresPlan? Plan;
        public int ActionIndex;
        public int? SentWireId;
        public WiresAction? SentAction;
        public int SentStateRevision;
        public WireDiscovery? Discovery;
        public bool Unbolting;
        public string? SecurityExamine;
        public bool RemovingSecurity;
        public WiresBoundUserInterfaceState? LastObservedState;
    }
}