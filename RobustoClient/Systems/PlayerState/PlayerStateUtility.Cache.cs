using Robust.Shared.GameObjects;
using Robust.Shared.Timing;
using RobustoClient.Systems.Abstract;

namespace RobustoClient.Systems.PlayerState;

/// <summary>
/// Persistent per-entity state, kept after an entity leaves PVS.
/// </summary>
public sealed partial class PlayerStateUtility
{
    /// <summary>Bound on the store.</summary>
    private const int MaxCachedPlayers = 256;

    private readonly Dictionary<EntityUid, PlayerStateRecord> _records = new();

    /// <summary>
    /// Latched per-entity values, kept out of the evictable record cache so a value seen once stays
    /// reported until the round ends.
    /// </summary>
    private readonly Dictionary<EntityUid, Dictionary<string, object?>> _latches = new();

    /// <summary>Last known report for an entity, or null when it has never been evaluated.</summary>
    public PlayerFlagReport? GetCachedReport(EntityUid player) =>
        _records.TryGetValue(player, out var record) ? record.Report : null;

    /// <summary>
    /// Entities whose latched value equals <paramref name="value"/>.
    /// </summary>
    public IReadOnlyList<EntityUid> GetPlayersWithCustom(string key, object? value) =>
        _latches
            .Where(entry => entry.Value.TryGetValue(key, out var held) && Equals(held, value))
            .Select(entry => entry.Key)
            .ToList();

    public T? GetCustom<T>(EntityUid player, string key) =>
        _latches.TryGetValue(player, out var values) &&
        values.TryGetValue(key, out var value) &&
        value is T typed
            ? typed
            : default;

    /// <summary>
    /// Latches caller-owned data that is not a flag. Separate from the report cache, so it survives
    /// both PVS loss and the eviction that trims the reports.
    /// </summary>
    public void SetCustom<T>(EntityUid player, string key, T value)
    {
        if (!_latches.TryGetValue(player, out var values))
            _latches[player] = values = new Dictionary<string, object?>(StringComparer.Ordinal);

        values[key] = value;
    }

    private readonly FlagRetentionStore _flagRetention = new();

    /// <summary>Refuses a second flag evaluation for the entity that is already being evaluated.</summary>
    private readonly EvaluationGate _evaluationGate = new();

    /// <summary>Refuses a second snapshot build for the entity that is already being built.</summary>
    private readonly EvaluationGate _snapshotGate = new();

    /// <summary>Drops every retained snapshot, flag latch and the hot cache. Called on round restart.</summary>
    public void ClearCache()
    {
        _records.Clear();
        _latches.Clear();
        _cache.Clear();
        _commandRoleCache.Clear();
        _snapshots.Clear();
        _flagRetention.Clear();
        _evaluationGate.Clear();
        _snapshotGate.Clear();
    }

    /// <summary>Retains a fresh full report, creating or refreshing the record.</summary>
    private void Store(EntityUid player, PlayerFlagReport report)
    {
        if (_records.TryGetValue(player, out var record))
        {
            record.Report = report;
            record.LastSeen = _timing.CurTime;
            return;
        }

        Insert(player, new PlayerStateRecord(EntityManager, player, report, _timing.CurTime));
    }

    private void Insert(EntityUid player, PlayerStateRecord record)
    {
        if (_records.Count >= MaxCachedPlayers)
            EvictOldest();

        _records[player] = record;
    }

    /// <summary>Drops the least recently refreshed record.</summary>
    private void EvictOldest() => CacheUtility.RemoveOldest(_records, record => record.LastSeen);

    private static PlayerFlagReport EmptyReport() => PlayerFlagReport.Empty;
}

/// <summary>
/// Last known state for one entity, kept after it leaves PVS.
/// </summary>
public sealed class PlayerStateRecord
{
    private readonly IEntityManager _entities;

    internal PlayerStateRecord(
        IEntityManager entities,
        EntityUid player,
        PlayerFlagReport report,
        TimeSpan lastSeen)
    {
        _entities = entities;
        Player = player;
        Report = report;
        LastSeen = lastSeen;
    }

    public EntityUid Player { get; }

    public PlayerFlagReport Report { get; internal set; }

    public TimeSpan LastSeen { get; internal set; }

    /// <summary>True when the entity no longer exists client-side, so this is the last known state.</summary>
    public bool Stale => !_entities.EntityExists(Player);
}
