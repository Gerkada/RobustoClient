using Robust.Shared.GameObjects;

namespace RobustoClient.Systems.PlayerState;

/// <summary>
/// Guards one evaluation per entity at a time. A flag that asks a scanner which then asks the flag
/// engine back for the same entity would otherwise recurse until the stack is gone, which kills the
/// client with no catchable exception.
/// </summary>
internal sealed class EvaluationGate
{
    private readonly HashSet<EntityUid> _active = new();

    /// <summary>Starts an evaluation, or false when the entity is already being evaluated.</summary>
    public bool TryEnter(EntityUid entity) => _active.Add(entity);

    /// <summary>Ends an evaluation. Must run in a finally so an exception cannot wedge the gate.</summary>
    public void Exit(EntityUid entity) => _active.Remove(entity);

    public void Clear() => _active.Clear();
}
