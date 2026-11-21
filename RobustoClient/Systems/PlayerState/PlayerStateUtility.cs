using Content.Shared.Access.Components;
using Content.Shared.Contraband;
using Content.Shared.Chemistry.Components;
using Content.Shared.Chemistry.Reagent;
using Content.Shared.GameTicking;
using Content.Shared.Hands.EntitySystems;
using Content.Shared.Inventory;
using Content.Shared.PDA;
using Content.Shared.Roles;
using Content.Shared.Weapons.Melee;
using Content.Shared.Wieldable.Components;
using Robust.Client.Player;
using Robust.Shared.Containers;
using Robust.Shared.GameObjects;
using Robust.Shared.Localization;
using Robust.Shared.Prototypes;
using Robust.Shared.Timing;
using RobustoClient.Systems.Abstract;

namespace RobustoClient.Systems.PlayerState;

/// <summary>
/// The single API for entity state: flags with the trigger behind each one, typed data blocks per
/// category, and one cached, round-retained snapshot per entity.
/// </summary>
/// <remarks>
/// This class decides what is true, the Info panel and the overlays decide how it looks. Scanners sit
/// below it and return primitives.
/// </remarks>
public sealed partial class PlayerStateUtility : EntitySystem
{
    [Dependency] private readonly InventorySystem _inventory = default!;
    [Dependency] private readonly SharedHandsSystem _hands = default!;
    [Dependency] private readonly IPrototypeManager _prototypes = default!;
    [Dependency] private readonly IGameTiming _timing = default!;
    [Dependency] private readonly IPlayerManager _player = default!;
    [Dependency] private readonly SharedContainerSystem _container = default!;

    private readonly Dictionary<string, PlayerFlagDefinition> _flags = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<EntityUid, (PlayerFlagReport Report, TimeSpan Stamp)> _cache = new();

    /// <summary>
    /// Checks that already threw, so the log names each one once instead of every frame. A check that
    /// throws is a bug in that check, not a reason to lose every other flag and block for the entity.
    /// </summary>
    private readonly HashSet<string> _faultedChecks = new(StringComparer.Ordinal);

    /// <summary>Command role per entity, cached alongside the flag cache.</summary>
    private readonly Dictionary<EntityUid, (string? Role, TimeSpan Stamp)> _commandRoleCache = new();

    private static readonly TimeSpan CacheLifetime = TimeSpan.FromSeconds(0.5);

    /// <summary>Flags registered so far, in registration order.</summary>
    public IReadOnlyCollection<PlayerFlagDefinition> RegisteredFlags => _flags.Values;

    public override void Initialize()
    {
        base.Initialize();
        RegisterDefaultFlags();
        SubscribeNetworkEvent<RoundRestartCleanupEvent>(_ => ClearCache());
    }

    public void RegisterFlag(PlayerFlagDefinition definition) => _flags[definition.Flag.Id] = definition;

    public bool HasAnyFlag(EntityUid player, params PlayerFlag[] flags)
    {
        var report = EvaluateCached(player);
        return flags.Any(report.Has);
    }

    public bool HasAllFlags(EntityUid player, params PlayerFlag[] flags)
    {
        var report = EvaluateCached(player);
        return flags.All(report.Has);
    }

    /// <summary>
    /// The player's ID card, looked for where upstream looks: the held item, the entity itself, then
    /// the ID slot.
    /// </summary>
    public IdCardComponent? FindIdCard(EntityUid player) =>
        TryGetIdCard(player, Context(player), out _, out var card) ? card : null;

    /// <summary>
    /// Whether the player's ID clears the item: upstream's allowed-department and allowed-job rule.
    /// </summary>
    public bool IsAllowedToCarry(EntityUid item, EntityUid player)
    {
        if (!EntityManager.TryGetComponent<ContrabandComponent>(item, out var contraband))
            return false;

        return IsAllowedForPlayer(contraband, Context(player));
    }

    private FlagContext Context(EntityUid player) =>
        new(player, EntityManager, _inventory, _hands, _prototypes);

    /// <summary>
    /// Full report for every registered flag, always freshly evaluated.
    /// </summary>
    public PlayerFlagReport Evaluate(EntityUid player)
    {
        var report = Evaluate(player, _flags.Values);

        // A scan of an entity that is already gone must not overwrite what was last known.
        if (EntityManager.EntityExists(player))
            Store(player, report);

        return report;
    }

    public PlayerFlagReport Evaluate(EntityUid player, IReadOnlyList<PlayerFlag> flags)
    {
        var definitions = flags
            .Select(f => _flags.TryGetValue(f.Id, out var def) ? def : null)
            .Where(def => def != null)
            .Select(def => def!)
            .ToList();

        return Evaluate(player, definitions);
    }

    /// <summary>
    /// Cached path for overlays that ask per entity per frame.
    /// </summary>
    /// <remarks>
    /// An entity that has left PVS answers from the retained record rather than an empty report.
    /// </remarks>
    private PlayerFlagReport EvaluateCached(EntityUid player)
    {
        if (_cache.TryGetValue(player, out var cached) &&
            _timing.CurTime - cached.Stamp < CacheLifetime)
        {
            return cached.Report;
        }

        if (!EntityManager.EntityExists(player))
            return GetCachedReport(player) ?? EmptyReport();

        var report = Evaluate(player);

        if (_cache.Count >= MaxCachedPlayers)
        {
            CacheUtility.RemoveOlderThan(_cache, entry => entry.Stamp, _timing.CurTime - CacheLifetime);

            if (_cache.Count >= MaxCachedPlayers)
                CacheUtility.RemoveOldest(_cache, entry => entry.Stamp);
        }

        _cache[player] = (report, _timing.CurTime);
        return report;
    }

    private PlayerFlagReport Evaluate(EntityUid player, IEnumerable<PlayerFlagDefinition> definitions)
    {
        if (!EntityManager.EntityExists(player))
            return PlayerFlagReport.Empty;

        // Same reasoning as GetSnapshot: an entity that can hold no mind, body or container has every
        // flag false, and evaluating it costs a container walk and a detector pass.
        if (!CanCarryState(player))
            return PlayerFlagReport.Empty;

        // A flag that asks a scanner which asks this class back for a flag would recurse until the
        // stack is gone, and a StackOverflowException cannot be caught: the client just dies. Answer
        // from retained data instead and let the outer evaluation finish.
        if (!_evaluationGate.TryEnter(player))
            return GetCachedReport(player) ?? PlayerFlagReport.Empty;

        try
        {
            var results = new List<PlayerFlagResult>();
            var context = Context(player);

            foreach (var definition in definitions)
            {
                var evidence = new List<PlayerFlagTrigger>();
                foreach (var check in definition.Checks)
                {
                    // A check that throws must not cost the entity every other flag: the overlays read
                    // this report, and an exception here blanks the radar and the ESP at once.
                    try
                    {
                        if (Matches(player, check, context, evidence))
                            break;
                    }
                    catch (Exception e)
                    {
                        ReportFaultedCheck(definition.Flag.Id, check, e);
                        break;
                    }
                }

                var result = new PlayerFlagResult(
                    definition.Flag,
                    definition.Category,
                    evidence.Count > 0,
                    evidence,
                    definition.Display);

                results.Add(_flagRetention.Apply(player, definition, result));
            }

            return new PlayerFlagReport(results);
        }
        finally
        {
            _evaluationGate.Exit(player);
        }
    }
}
