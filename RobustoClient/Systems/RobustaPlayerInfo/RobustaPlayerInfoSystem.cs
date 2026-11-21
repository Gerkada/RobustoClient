using Robust.Shared.GameObjects;
using Robust.Shared.IoC;
using RobustoClient.Systems.PlayerState;

namespace RobustoClient.Systems;

/// <summary>
/// The radar Info panel: reads a <see cref="PlayerSnapshot"/> and turns it into sections. It owns no
/// game truth and no scanners, so every rule it applies is formatting.
/// </summary>
public sealed partial class RobustaPlayerInfoSystem : EntitySystem
{
    [Dependency] private readonly PlayerStateUtility _state = default!;

    /// <summary>
    /// Last rendered report, keyed on the snapshot instance, so formatting runs once per state
    /// refresh rather than once per frame.
    /// </summary>
    private (EntityUid Entity, PlayerSnapshot Snapshot, PlayerInfoReport Report, bool Identified)? _memo;

    /// <summary>The report for a target, or null when no state is known for it.</summary>
    public PlayerInfoSnapshot? GetCachedReport(EntityUid target)
    {
        var snapshot = _state.GetSnapshot(target);
        var identified = IsIdentified(snapshot);

        if (_memo is { } memo && memo.Entity == target)
        {
            if (ReferenceEquals(memo.Snapshot, snapshot))
                return new PlayerInfoSnapshot(memo.Report, snapshot.Stale);

            // A mob can outlive its contents in PVS, keep the last identified report rather than
            // replacing it with an empty one.
            if (!identified && memo.Identified)
                return new PlayerInfoSnapshot(memo.Report, true);
        }

        var report = Compose(snapshot);
        _memo = (target, snapshot, report, identified);
        return new PlayerInfoSnapshot(report, snapshot.Stale);
    }

    /// <summary>Whether the snapshot says enough about the entity to be worth showing.</summary>
    private static bool IsIdentified(PlayerSnapshot snapshot) =>
        !string.IsNullOrEmpty(snapshot.Identity?.JobTitle) ||
        snapshot.Inventory is { Entries.Count: > 0 } ||
        snapshot.Syndicate is not null ||
        snapshot.Antagonist is not null ||
        snapshot.Command is not null;
}
