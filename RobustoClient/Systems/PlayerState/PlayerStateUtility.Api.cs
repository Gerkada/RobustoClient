using Content.Shared.Mind.Components;
using Content.Shared.Mobs.Components;
using Robust.Shared.Containers;
using Robust.Shared.GameObjects;
using RobustoClient.Systems.Abstract;

namespace RobustoClient.Systems.PlayerState;

/// <summary>
/// The query surface for entity state: one call for a whole data collection, one for a single block,
/// or one for a flag with the trigger that fired it. Every entry point works for any entity, not just
/// mobs, and keeps answering from retained data after the entity leaves PVS.
/// </summary>
public sealed partial class PlayerStateUtility
{
    /// <summary>Bound on retained snapshots. Only entities that can hold state are ever stored.</summary>
    private const int SnapshotLimit = 512;

    /// <summary>
    /// Per-entity staleness window.
    /// </summary>
    private static readonly TimeSpan SnapshotLifetime = TimeSpan.FromSeconds(0.5);

    private readonly Dictionary<EntityUid, PlayerSnapshot> _snapshots = new();

    /// <summary>
    /// Everything known about the entity.
    /// </summary>
    public PlayerSnapshot GetSnapshot(EntityUid entity)
    {
        var now = _timing.CurTime;
        var known = _snapshots.TryGetValue(entity, out var cached);

        if (!EntityManager.EntityExists(entity))
        {
            if (known)
            {
                cached!.Stale = true;
                return cached;
            }

            return BuildSnapshot(entity, now, stale: true);
        }

        if (known && now - cached!.Stamp < SnapshotLifetime)
            return cached;

        if (!CanCarryState(entity))
        {
            var empty = new PlayerSnapshot(entity, PlayerFlagReport.Empty, now, _ => null);

            // Stored only while there is room
            if (_snapshots.Count < SnapshotLimit)
                _snapshots[entity] = empty;

            return empty;
        }

        if (!known && _snapshots.Count >= SnapshotLimit)
            EvictSnapshot(now);

        // A block builder that asked for this entity's snapshot again would recurse forever, so the
        // second request answers with whatever is already retained.
        if (!_snapshotGate.TryEnter(entity))
            return known ? cached! : new PlayerSnapshot { Entity = entity, Stale = true };
        try
        {
            var snapshot = BuildSnapshot(entity, now, stale: false);
            _snapshots[entity] = snapshot;
            return snapshot;
        }
        finally
        {
            _snapshotGate.Exit(entity);
        }
    }

    /// <summary>
    /// Whether the entity can hold state this class reports: a mind, a mob body, or containers.
    /// </summary>
    private bool CanCarryState(EntityUid entity) =>
        HasComp<MindContainerComponent>(entity) ||
        HasComp<MobStateComponent>(entity) ||
        HasComp<ContainerManagerComponent>(entity);

    /// <summary>
    /// Makes room for a new snapshot.
    /// </summary>
    private void EvictSnapshot(TimeSpan now)
    {
        CacheUtility.RemoveOlderThan(_snapshots, snapshot => snapshot.Stamp, now - SnapshotLifetime);

        if (_snapshots.Count >= SnapshotLimit)
            CacheUtility.RemoveOldest(_snapshots, snapshot => snapshot.Stamp);
    }

    /// <summary>The block for a category, or null when it does not apply to the entity.</summary>
    public IPlayerData? GetData(EntityUid entity, PlayerStateData category) =>
        GetSnapshot(entity).GetData(category);

    /// <summary>The block for a category, typed.</summary>
    public T? GetData<T>(EntityUid entity) where T : class, IPlayerData =>
        GetSnapshot(entity).GetData<T>();

    /// <summary>
    /// One flag, with the reason and the entities responsible when it fired.
    /// </summary>
    public PlayerFlagResult GetFlag(EntityUid entity, PlayerFlag flag) =>
        GetSnapshot(entity).Flags.Result(flag) ?? PlayerFlagResult.Miss(flag, CategoryOf(flag));

    /// <summary>Every registered flag for the entity.</summary>
    public PlayerFlagReport GetFlags(EntityUid entity) => GetSnapshot(entity).Flags;

    /// <summary>The requested flags, or the ones that fired plus the ones asked for.</summary>
    public PlayerFlagReport GetFlags(EntityUid entity, params PlayerFlag[] flags)
    {
        var report = GetSnapshot(entity).Flags;
        var results = new List<PlayerFlagResult>(flags.Length);

        foreach (var flag in flags)
            results.Add(report.Result(flag) ?? PlayerFlagResult.Miss(flag, CategoryOf(flag)));

        return new PlayerFlagReport(results);
    }

    /// <summary>Drops the retained snapshot, e.g. when an entity is reshaped.</summary>
    public void Invalidate(EntityUid entity) => _snapshots.Remove(entity);

    private PlayerSnapshot BuildSnapshot(EntityUid entity, TimeSpan stamp, bool stale)
    {
        var flags = Evaluate(entity);

        // One inventory scan per snapshot, shared by every block that needs it and built only if one
        // of them is asked for.
        IReadOnlyList<InventoryEntry>? inventory = null;
        IReadOnlyList<InventoryEntry> Inventory() =>
            inventory ??= EntityManager.System<RobustaInventoryViewerSystem>().GetFullInventory(entity);

        return new PlayerSnapshot(entity, flags, stamp, category => BuildBlock(category, entity, flags, Inventory))
        {
            Stale = stale
        };
    }

    private IPlayerData? BuildBlock(
        PlayerStateData category,
        EntityUid entity,
        PlayerFlagReport flags,
        Func<IReadOnlyList<InventoryEntry>> inventory)
    {
        // A block that throws must not blank the whole snapshot: the radar and the ESP read several
        // blocks per entity, and one bad block would otherwise take the rest down with it.
        try
        {
            return category switch
            {
                PlayerStateData.Identity => BuildIdentity(entity, flags),
                PlayerStateData.Vitality => BuildVitality(entity, flags),
                PlayerStateData.Command => BuildCommand(entity, flags, inventory),
                PlayerStateData.Security => BuildSecurity(entity, flags),
                PlayerStateData.Syndicate => BuildSyndicate(entity, flags),
                PlayerStateData.Antagonist => BuildAntagonist(entity, flags),
                PlayerStateData.Theft => BuildTheft(entity, flags, inventory),
                PlayerStateData.Chemistry => BuildChemistry(entity, flags, inventory),
                PlayerStateData.Equipment => BuildEquipment(entity, inventory),
                PlayerStateData.Inventory => BuildInventory(entity),
                _ => null
            };
        }
        catch (Exception e)
        {
            ReportFaultedBlock(category, e);
            return null;
        }
    }

    /// <summary>Logs a throwing check once</summary>
    private void ReportFaultedCheck(string flagId, PlayerFlagCheck check, Exception e)
    {
        if (!_faultedChecks.Add($"check:{flagId}:{check.GetType().Name}"))
            return;

        MarseyLogger.Warn($"[PlayerState] Check {check.GetType().Name} for flag {flagId} threw, " +
                          $"the flag is reported as not fired: {e}");
    }

    /// <summary>Logs a throwing block once</summary>
    private void ReportFaultedBlock(PlayerStateData category, Exception e)
    {
        if (!_faultedChecks.Add($"block:{category}"))
            return;

        MarseyLogger.Warn($"[PlayerState] Block {category} threw, it is reported as absent: {e}");
    }

    private PlayerStateData CategoryOf(PlayerFlag flag) =>
        _flags.TryGetValue(flag.Id, out var definition) ? definition.Category : PlayerStateData.Identity;
}
