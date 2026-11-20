using System;
using System.Collections.Generic;
using System.Linq;
using Content.Shared.Store.Components;
using Content.Shared.Tag;
using Content.Shared.Contraband;
using Content.Shared.PDA;
using Content.Shared.CriminalRecords;
using Content.Shared.Emag.Components;
using Content.Shared.Emag.Systems;
using Content.Shared.Security;
using Content.Shared.Station.Components;
using Content.Shared.StationRecords;
using Content.Shared.StationRecords.Components;
using Content.Shared.Thief.Components;
using Robust.Shared.GameObjects;
using Robust.Shared.IoC;
using Robust.Shared.Map;
using Robust.Shared.Prototypes;
using Robust.Shared.Timing;
using Robust.Shared.Containers;
using RobustoClient.Systems.Abstract;
using Content.Shared.Actions;
using Content.Shared.Actions.Components;
using Content.Shared.Forensics.Components;
using Content.Shared.GameTicking;
using Content.Shared.Humanoid;
using Content.Shared.Implants.Components;
using Content.Shared.Inventory;
using Content.Shared.NPC.Components;
using Content.Shared.NPC.Prototypes;
using Content.Shared.Ninja.Components;
using Content.Shared.NukeOps;
using Content.Shared.RatKing;
using Content.Shared.Revolutionary.Components;
using Content.Shared.Zombies;
using RobustoClient.Reflection;
using RobustoClient.Systems.PlayerState;

namespace RobustoClient.Systems;

public record struct ImplantInfo(string Name, string Category);

/// <summary>Signal the detector can report.</summary>
public enum SyndicateSignal
{
    Uplink,
    Contraband,
    Implants
}

/// <summary>Ghost-role antagonist recognisable from replicated state.</summary>
public enum AntagonistKind
{
    Clone,
    Skeleton,
    Ninja,
    Changeling,
    Dragon,
    Carp,
    RatKing,
    Thief,
    Zombie,
    InitialInfected,
    HeadRevolutionary,
    Revolutionary,
    Wizard,
    NukeOperative
}

/// <summary>Whether the kind is a gear guess, so the UI hedges its tag.</summary>
public static class AntagonistKindExtensions
{
    public static bool IsHedged(this AntagonistKind kind) => kind switch
    {
        AntagonistKind.HeadRevolutionary => true,
        AntagonistKind.Revolutionary => true,
        // A granted spell makes a wizard certain, so the hedge travels with the verdict.
        // A clone is only the later sighting of a shared forensics pair.
        AntagonistKind.Clone => true,
        _ => false
    };
}

public sealed class RobustaSyndicateDetectorSystem : EntitySystem
{
    [Dependency] private readonly TagSystem _tagSystem = default!;
    [Dependency] private readonly IGameTiming _timing = default!;
    [Dependency] private readonly IComponentFactory _compFactory = default!;

    private readonly Dictionary<EntityUid, (bool Uplink, bool Contra, List<ImplantInfo> Implants, TimeSpan LastCheck)> _cache = new();
    private readonly TimeSpan _cacheLifetime = TimeSpan.FromSeconds(1);

    private readonly Dictionary<EntityUid, (AntagonistKind? Kind, TimeSpan Stamp, bool Retained, bool Hedged)> _antagonistCache = new();

    /// <summary>Name to status per station, rebuilt at most once a second.</summary>
    private readonly Dictionary<EntityUid, (Dictionary<string, SecurityStatus> Map, TimeSpan Stamp)> _wantedCache = new();
    private const string AntagonistKey = "Antagonist";

    /// <summary>Whether the retained verdict was a gear guess.</summary>
    private const string AntagonistHedgedKey = "AntagonistHedged";
    private static readonly TimeSpan WantedCacheLifetime = TimeSpan.FromSeconds(1);

    public override void Initialize()
    {
        base.Initialize();
        SubscribeNetworkEvent<RoundRestartCleanupEvent>(_ => ClearCaches());
    }

    /// <summary>Drops the per-entity caches on round restart.</summary>
    private void ClearCaches()
    {
        _cache.Clear();
        _antagonistCache.Clear();
        _wantedCache.Clear();
    }

    /// <summary>Bound on the antagonist cache.</summary>
    private const int AntagonistCacheLimit = 128;

    /// <summary>Bound on the per-entity status cache.</summary>
    private const int StatusCacheLimit = 256;

    /// <summary>Registration name of the horror form component, which only upstream ships.</summary>
    internal const string ChangelingHorrorComponentName = "ChangelingHorror";

    /// <summary>Registration name of the melee battery counter, which only upstream ships.</summary>
    internal const string MeleeBatteryHitsLeftComponentName = "MeleeBatteryHitsLeft";

    /// <summary>Registration name of the station record set component.</summary>
    internal const string StationRecordsComponentName = "StationRecords";

    /// <summary>Species id that marks a skeleton mob.</summary>
    internal const string SkeletonSpecies = "Skeleton";

    /// <summary>Species component names, newest upstream name first.</summary>
    internal static readonly string[] SpeciesComponentNames =
    [
        "HumanoidProfile",
        "HumanoidAppearance"
    ];

    /// <summary>Dragon prototype, so the usual case needs no chain walk.</summary>
    private const string MobDragonPrototype = "MobDragon";

    /// <summary>Parent shared by the dragon and its variants.</summary>
    private const string DragonBasePrototype = "BaseMobDragon";

    /// <summary>Component whose authored faction set names the dragon's minions.</summary>
    private const string NpcFactionComponentName = "NpcFactionMember";

    /// <summary>Faction every carp and dragon minion belongs to.</summary>
    private const string DragonFaction = "Dragon";

    /// <summary>Registration name of the mindshield verdict component (suffix dropped).</summary>
    internal const string MindShieldStatusComponentName = "MindShieldStatus";

    /// <summary>Registration name of the older build's shield marker.</summary>
    internal const string MindShieldComponentName = "MindShield";

    /// <summary>[GOOB] GoobStation's changeling marker. Upstream lacks it, so it is resolved by name.</summary>
    internal const string GoobChangelingComponentName = "Changeling";

    /// <summary>[GOOB] GoobStation's changeling identity component. Upstream lacks it.</summary>
    internal const string GoobChangelingIdentityComponentName = "ChangelingIdentity";

    /// <summary>Upstream's slug form component. Goob has none, so it is resolved by name.</summary>
    internal const string ChangelingSlugComponentName = "ChangelingSlug";

    /// <summary>Reflection options for members older builds lack, so a miss is expected.</summary>
    internal static readonly ReflectOptions OptionalMember =
        new() { NonPublic = true, IgnoreCase = true, WarnOnMissing = false };

    /// <summary>Currency only a changeling's store holds.</summary>
    internal const string ChangelingCurrency = "ChangelingDNA";

    /// <summary>Actions only a changeling is granted.</summary>
    private static readonly string[] _changelingActions =
    [
        // Upstream.
        "ActionChangelingStore",
        "ActionChangelingStasis",
        "ActionChangelingStasisWeak",

        // [GOOB] GoobStation's set. Neither build has the other's.
        "ActionChangelingTransform",
        "ActionChangelingTransformCycle"
    ];

    /// <summary>Currency only a wizard's grimoire holds.</summary>
    internal const string WizardCurrency = "WizCoin";

    /// <summary>
    /// Prototypes only a wizard's starting gear hands out. The fake hat and robe are excluded because
    /// anyone can wear them.
    /// </summary>
    private static readonly string[] _wizardItems =
    [
        "WizardsGrimoire",
        "ClothingBeltWand",
        "WizardTeleportScroll",
        "WizardPDA",
        "ClothingHeadsetAltWizard"
    ];

    /// <summary>Prototypes <c>HeadRevGear</c> hands out together.</summary>
    private static readonly string[] _headRevItems =
    [
        "Flash",
        "ClothingEyesGlassesSunglasses"
    ];

    /// <summary>Actions only a wizard's spellbook grants.</summary>
    private static readonly string[] _wizardActions =
    [
        "ActionFireball",
        "ActionFireballII",
        "ActionFireballIII",
        "ActionBlink",
        "ActionForceWall",
        "ActionKnock",
        "ActionMindSwap",
        "ActionRepulse",
        "ActionSmoke",
        "ActionChargeSpell",
        "ActionItemRecall",
        "ActionAnimateSpell",
        "ActionSpawnMagicarpSpell",
        "ActionSummonGhosts",
        "ActionSummonGuns",
        "ActionSummonMagic",
        "ActionFlashRune",
        "ActionExplosionRune",
        "ActionIgniteRune",
        "ActionStunRune",
        "ActionSmite",
        "ActionCluwne",
        "ActionSlippery",
        "ActionVoidApplause"
    ];

    /// <summary>Store key for the DNA a paradox clone inherits.</summary>
    private const string DnaKey = "Dna";
    /// <summary>Store key for the fingerprint a paradox clone inherits.</summary>
    private const string FingerprintKey = "Fingerprint";

    /// <summary>Store key for the round time an entity was first scanned.</summary>
    private const string FirstSeenKey = "FirstSeen";

    /// <summary>
    /// Round time before which no clone can exist. <c>ParadoxCloneSpawn</c> sets
    /// <c>earliestStart: 20</c> minutes, and a player must then take the ghost role.
    /// </summary>
    private static readonly TimeSpan CloneEligibleAfter = TimeSpan.FromMinutes(20);

    /// <summary>Store key for a thief match.</summary>
    private const string ThiefKey = "Thief";

    /// <summary>Store key for a confirmed uplink.</summary>
    private const string UplinkKey = "Uplink";

    /// <summary>Status shown while the uplink tell is visible.</summary>
    internal const string OpenUplinkStatus = "OPEN UPLINK";

    /// <summary>Status shown once the uplink tell is gone.</summary>
    internal const string ClosedUplinkStatus = "CLOSED UPLINK";

    /// <summary>The persistent player-state store, resolved lazily.</summary>
    private PlayerStateUtility PlayerState => EntityManager.System<PlayerStateUtility>();
    private static readonly Type? StoreDiscountType =
        Reflect.FindType("Content.Shared.StoreDiscount.Components.StoreDiscountComponent");

    /// <summary>Currencies that belong only to antagonists.</summary>
    private readonly HashSet<string> _antagCurrencies = new(StringComparer.OrdinalIgnoreCase)
    {
        "Telecrystal",
        "StolenEssence",
        "WizCoin",
        "ChangelingDNA"
    };

    /// <summary>Items excluded from the gear check.</summary>
    private static readonly string[] _syndicateGearExclusions =
    [
        "toy",
        "plushie",
        "lighter",
        "pyjama",
        "scarf",
        "winter",
        "coatsyndie",
        "headhatsyndie",
        "uniformjumpsuit",
        "uniformjumpskirt",
        "catears",
    ];

    /// <summary>
    /// A store's balance, or null when this build keeps it where the client cannot read it. Read
    /// through <see cref="Reflect"/> rather than a cached <c>GetField</c> because a fork may expose
    /// the balance as a property instead of upstream's public field, and a null field then silently
    /// disabled every uplink check.
    /// </summary>
    internal static System.Collections.IDictionary? StoreBalance(StoreComponent store) =>
        Reflect.Get(store, "Balance", ReflectOptions.Any) as System.Collections.IDictionary;

#region Detection entry points

    /// <summary>Antagonist the entity is, or null when it is not one.</summary>
    public AntagonistKind? DetectAntagonist(EntityUid uid)
    {
        var now = _timing.CurTime;

        if (_antagonistCache.TryGetValue(uid, out var cached) && now - cached.Stamp < _cacheLifetime)
            return cached.Kind;

        // Expired entries go first. Only a still full cache loses its oldest.
        if (_antagonistCache.Count >= AntagonistCacheLimit)
        {
            CacheUtility.RemoveOlderThan(_antagonistCache, entry => entry.Stamp, now - _cacheLifetime);

            if (_antagonistCache.Count >= AntagonistCacheLimit)
                CacheUtility.RemoveOldest(_antagonistCache, entry => entry.Stamp);
        }

        // Retain the forensics the clone check keys on, so it survives PVS loss.
        RememberForensics(uid);

        var kind = ClassifyAntagonist(uid, out var hedged);
        var retained = false;

        if (kind is { } detected)
        {
            // The verdict outranks the tell, so a rev who drops the flash stays named.
            PlayerState.SetCustom(uid, AntagonistKey, detected.ToString());
            PlayerState.SetCustom(uid, AntagonistHedgedKey, hedged);
        }
        else if (PlayerState.GetCustom<string>(uid, AntagonistKey) is { } latched &&
                 Enum.TryParse<AntagonistKind>(latched, out var kept))
        {
            kind = kept;
            hedged = PlayerState.GetCustom<bool>(uid, AntagonistHedgedKey);
            retained = true;
        }

        _antagonistCache[uid] = (kind, now, retained, hedged);
        return kind;
    }

    /// <summary>Whether the verdict comes from the round latch rather than a live tell.</summary>
    public bool IsAntagonistRetained(EntityUid uid)
    {
        if (_antagonistCache.TryGetValue(uid, out var cached) &&
            _timing.CurTime - cached.Stamp < _cacheLifetime)
        {
            return cached.Retained;
        }

        DetectAntagonist(uid);
        return _antagonistCache.TryGetValue(uid, out var refreshed) && refreshed.Retained;
    }

    /// <summary>
    /// Whether the verdict rests on gear, so the tag hedges. The kind alone cannot say, because a
    /// wizard is certain once a spell is granted.
    /// </summary>
    public bool IsAntagonistHedged(EntityUid uid)
    {
        if (_antagonistCache.TryGetValue(uid, out var cached) &&
            _timing.CurTime - cached.Stamp < _cacheLifetime)
        {
            return cached.Hedged;
        }

        DetectAntagonist(uid);
        return _antagonistCache.TryGetValue(uid, out var refreshed) && refreshed.Hedged;
    }

    /// <summary>Copies the entity's DNA and fingerprint into the player-state store.</summary>
    private void RememberForensics(EntityUid uid)
    {
        // The first sighting decides which half of a shared forensics pair is the original, so it is
        // latched once and never refreshed.
        if (PlayerState.GetCustom<TimeSpan?>(uid, FirstSeenKey) is null)
            PlayerState.SetCustom<TimeSpan?>(uid, FirstSeenKey, _timing.CurTime);

        if (TryComp<DnaComponent>(uid, out var dna) && !string.IsNullOrEmpty(dna.DNA))
            PlayerState.SetCustom(uid, DnaKey, dna.DNA);

        if (TryComp<FingerprintComponent>(uid, out var prints) && !string.IsNullOrEmpty(prints.Fingerprint))
            PlayerState.SetCustom(uid, FingerprintKey, prints.Fingerprint);
    }

#endregion

#region Classification

    private AntagonistKind? ClassifyAntagonist(EntityUid uid, out bool hedged)
    {
        hedged = false;

        if (IsClone(uid))
            return AntagonistKind.Clone;

        if (HasComp<NukeOperativeComponent>(uid))
            return AntagonistKind.NukeOperative;

        if (HasComp<SpaceNinjaComponent>(uid))
            return AntagonistKind.Ninja;

        if (HasComp<RatKingComponent>(uid))
            return AntagonistKind.RatKing;

        if (IsChangeling(uid))
            return AntagonistKind.Changeling;

        // Zombie outranks Initial Infected, matching upstream's icon rule.
        if (HasComp<ZombieComponent>(uid))
            return AntagonistKind.Zombie;

        if (HasComp<InitialInfectedComponent>(uid))
            return AntagonistKind.InitialInfected;

        if (IsSkeletonSpecies(uid))
            return AntagonistKind.Skeleton;

        if (TryComp<MetaDataComponent>(uid, out var meta) &&
            meta.EntityPrototype is { } prototype &&
            ClassifyDragonSide(
                IsDragonPrototype(prototype),
                InDragonFaction(prototype)) is { } dragonSide)
        {
            return dragonSide;
        }

        // The real rev components only reach a client that is itself a rev, so they win when present.
        if (HasComp<HeadRevolutionaryComponent>(uid))
            return AntagonistKind.HeadRevolutionary;

        if (HasComp<RevolutionaryComponent>(uid))
            return AntagonistKind.Revolutionary;

        if (IsKnownThief(uid))
            return AntagonistKind.Thief;

        return ClassifyFromGear(uid, out hedged);
    }

#endregion

#region Skeleton

    /// <summary>
    /// Whether the entity has a component, by registration name. Use this for a component only some
    /// builds ship, because naming the type throws TypeLoadException on a build without it.
    /// </summary>
    internal bool HasComponentNamed(EntityUid uid, string componentName) =>
        _compFactory.TryGetRegistration(componentName, out var registration) &&
        HasComp(uid, registration.Type);

    /// <summary>The component by registration name, or null when this build lacks it.</summary>
    internal IComponent? GetComponentNamed(EntityUid uid, string componentName) =>
        _compFactory.TryGetRegistration(componentName, out var registration) &&
        TryComp(uid, registration.Type, out var component)
            ? component
            : null;

    /// <summary>
    /// Whether the entity is the skeleton species. The species component was renamed upstream, so it
    /// is resolved by name, because either name throws on the other build.
    /// </summary>
    private bool IsSkeletonSpecies(EntityUid uid)
    {
        foreach (var name in SpeciesComponentNames)
        {
            if (!_compFactory.TryGetRegistration(name, out var registration) ||
                !TryComp(uid, registration.Type, out var component))
            {
                continue;
            }

            if (Reflect.TryGet(component, "Species", out var species, OptionalMember) &&
                ReflectConvert.TryGetId(species) == SkeletonSpecies)
            {
                return true;
            }
        }

        return false;
    }

#endregion

#region Dragon

    /// <summary>
    /// Which dragon side kind a mob is. The dragon wins over the faction because every carp shares its
    /// faction, so the faction alone would rename the dragon to its own brood. Pure so it is testable.
    /// </summary>
    internal static AntagonistKind? ClassifyDragonSide(bool isDragon, bool inDragonFaction) =>
        isDragon ? AntagonistKind.Dragon : inDragonFaction ? AntagonistKind.Carp : null;

    /// <summary>
    /// Whether the prototype is the dragon itself. The marking component is server-only and the client
    /// registry drops it, so the prototype name and parents are the only evidence.
    /// </summary>
    private static bool IsDragonPrototype(EntityPrototype prototype) =>
        prototype.ID is MobDragonPrototype or DragonBasePrototype ||
        prototype.Parents?.Contains(MobDragonPrototype) == true ||
        prototype.Parents?.Contains(DragonBasePrototype) == true;

    /// <summary>
    /// Whether the prototype places the mob in the Dragon faction. The live set is not networked, so
    /// the authored value is the only membership a client can read.
    /// </summary>
    private static bool InDragonFaction(EntityPrototype prototype) =>
        prototype.Components.TryGetComponent(NpcFactionComponentName, out var component) &&
        component is NpcFactionMemberComponent faction &&
        faction.Factions.Contains(DragonFaction);

#endregion

#region Head revolutionary

    /// <summary>
    /// Whether head-rev gear identifies a head revolutionary. The server cancels a conversion on a
    /// mindshielded target, so a mindshield makes the gear ordinary kit. Pure so it is testable.
    /// </summary>
    internal static bool ShouldClassifyHeadRev(bool hasGear, bool mindshielded) =>
        hasGear && !mindshielded;

    /// <summary>
    /// Whether a mindshield protects the entity. The types are read by name because upstream split them
    /// into <c>MindShieldSystem</c> plus a status component, and the older build has neither, which
    /// fails the IoC graph at startup.
    /// </summary>
    public bool IsMindshielded(EntityUid uid) => IsMindshielded(uid, null);

    /// <summary>Same, reusing a container walk the caller already did.</summary>
    private bool IsMindshielded(EntityUid uid, IReadOnlyList<EntityUid>? carried)
    {
        // Current upstream resolves the verdict onto a dedicated networked component.
        if (_compFactory.TryGetRegistration(MindShieldStatusComponentName, out var status) &&
            TryComp(uid, status.Type, out var statusComponent) &&
            Reflect.TryGet(statusComponent, "IsMindshielded", out var shielded, OptionalMember))
        {
            return shielded is true;
        }

        // Older upstream only marks the holder, on the mob or on worn or implanted gear.
        if (!_compFactory.TryGetRegistration(MindShieldComponentName, out var marker))
            return false;

        if (carried == null)
        {
            var walk = new List<EntityUid>();
            CollectCarried(uid, walk, new HashSet<EntityUid>(), 0);
            carried = walk;
        }

        foreach (var item in carried)
        {
            if (!TryComp(item, marker.Type, out var component))
                continue;

            // A broken shield no longer protects, so it must not suppress the head-rev verdict.
            if (Reflect.TryGet(component, "Broken", out var broken, OptionalMember) && broken is true)
                continue;

            return true;
        }

        return false;
    }

    /// <summary>
    /// Hedged classification from carried gear, plus the one certain wizard tell. A granted spell is
    /// checked first because it needs no container walk.
    /// </summary>
    private AntagonistKind? ClassifyFromGear(EntityUid uid, out bool hedged)
    {
        hedged = false;

        // The spellbook is the only source of these actions and the action list is networked, so a
        // granted spell is certain.
        if (HasWizardSpell(uid))
            return AntagonistKind.Wizard;

        // Only mobs with an inventory can carry gear, so everything else skips the walk.
        if (!HasComp<InventoryComponent>(uid))
            return null;

        var carried = new List<EntityUid>();
        CollectCarried(uid, carried, new HashSet<EntityUid>(), 0);

        var ids = new List<string>(carried.Count);
        foreach (var item in carried)
        {
            if (TryComp<MetaDataComponent>(item, out var meta) && meta.EntityPrototype is { } proto)
                ids.Add(proto.ID);
        }

        // Anyone can pick up a dropped grimoire, so wizard gear alone only makes the kind a guess.
        if (ContainsAnyPrototype(ids, _wizardItems) || CarriesWizardStore(carried))
        {
            hedged = true;
            return AntagonistKind.Wizard;
        }

        if (ShouldClassifyHeadRev(ContainsAllPrototypes(ids, _headRevItems), IsMindshielded(uid, carried)))
            return AntagonistKind.HeadRevolutionary;

        return null;
    }

#endregion

#region Wizard

    /// <summary>
    /// Whether the entity holds a wizard spell. The spellbook grants these onto the mob's
    /// <see cref="ActionsComponent"/>, whose action entities are networked.
    /// </summary>
    private bool HasWizardSpell(EntityUid uid)
    {
        if (!TryComp<ActionsComponent>(uid, out var actions))
            return false;

        foreach (var action in actions.Actions)
        {
            if (TryComp<MetaDataComponent>(action, out var meta) &&
                meta.EntityPrototype is { } proto &&
                IsWizardAction(proto.ID))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Whether any carried item is a store holding the wizard-only currency.</summary>
    private bool CarriesWizardStore(IReadOnlyList<EntityUid> carried)
    {
        foreach (var item in carried)
        {
            if (TryComp<StoreComponent>(item, out var store) &&
                HeldCurrencies(store).Contains(WizardCurrency))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Every entity the player carries, containers included.</summary>
    private void CollectCarried(EntityUid parent, List<EntityUid> found, HashSet<EntityUid> scanned, int depth)
    {
        if (depth > 5 || !scanned.Add(parent))
            return;

        found.Add(parent);

        if (!TryComp<ContainerManagerComponent>(parent, out var containers))
            return;

        foreach (var (containerId, container) in containers.Containers)
        {
            if (containerId.Equals("actions", StringComparison.OrdinalIgnoreCase))
                continue;

            foreach (var contained in container.ContainedEntities)
                CollectCarried(contained, found, scanned, depth + 1);
        }
    }

    /// <summary>Whether the carried prototype ids include any of the wanted ones.</summary>
    internal static bool ContainsAnyPrototype(IEnumerable<string> carried, IReadOnlyList<string> wanted)
    {
        var set = new HashSet<string>(carried, StringComparer.OrdinalIgnoreCase);

        foreach (var id in wanted)
        {
            if (set.Contains(id))
                return true;
        }

        return false;
    }

    /// <summary>Whether the carried prototype ids include every one of the wanted ones.</summary>
    internal static bool ContainsAllPrototypes(IEnumerable<string> carried, IReadOnlyList<string> wanted)
    {
        var set = new HashSet<string>(carried, StringComparer.OrdinalIgnoreCase);

        foreach (var id in wanted)
        {
            if (!set.Contains(id))
                return false;
        }

        return true;
    }

    /// <summary>
    /// Whether any granted action is one only a wizard's spellbook hands out. Static and ECS-free so
    /// the allow-list is testable.
    /// </summary>
    internal static bool HasWizardAction(IEnumerable<EntProtoId> actions)
    {
        foreach (var action in actions)
        {
            if (IsWizardAction(action.Id))
                return true;
        }

        return false;
    }

    /// <summary>Whether a prototype id is one of the wizard's spells.</summary>
    internal static bool IsWizardAction(string prototypeId)
    {
        foreach (var wizardAction in _wizardActions)
        {
            if (string.Equals(prototypeId, wizardAction, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    /// <summary>Whether the entity is a thief, from a tell latched this round.</summary>
#endregion

#region Thief

    /// <summary>Whether the entity carries the thief's own gear.</summary>
    public bool IsKnownThief(EntityUid uid)
    {
        if (PlayerState.GetCustom<bool>(uid, ThiefKey))
            return true;

        if (PlayerState.GetCachedReport(uid)?.Has(PlayerFlag.IsThief) == true)
        {
            PlayerState.SetCustom(uid, ThiefKey, true);
            return true;
        }

        if (!CarriesThiefGear(uid))
            return false;

        PlayerState.SetCustom(uid, ThiefKey, true);
        return true;
    }

    /// <summary>A thief beacon, or the thief's satchel, anywhere in the container tree.</summary>
    private bool CarriesThiefGear(EntityUid uid)
    {
        if (!HasComp<InventoryComponent>(uid))
            return false;

        var carried = new List<EntityUid>();
        CollectCarried(uid, carried, new HashSet<EntityUid>(), 0);

        foreach (var item in carried)
        {
            if (HasComp<ThiefBeaconComponent>(item))
                return true;

            if (TryComp<MetaDataComponent>(item, out var meta) &&
                string.Equals(meta.EntityPrototype?.ID, PlayerStateUtility.ThiefSatchelPrototype,
                    StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Uplink state for the entity, latched for the round.</summary>
    public string? GetUplinkStatus(EntityUid uid)
    {
        if (CheckPlayerStatus(uid).Uplink)
        {
            PlayerState.SetCustom(uid, UplinkKey, true);
            return OpenUplinkStatus;
        }

        return PlayerState.GetCustom<bool>(uid, UplinkKey) ? ClosedUplinkStatus : null;
    }

#endregion

#region Clone

    /// <summary>Whether the entity is a paradox clone, from a shared DNA or fingerprint value.</summary>
    private bool IsClone(EntityUid uid) =>
        IsCloneByKey(uid, DnaKey) || IsCloneByKey(uid, FingerprintKey);

    private bool IsCloneByKey(EntityUid uid, string key)
    {
        var value = PlayerState.GetCustom<string>(uid, key);
        if (string.IsNullOrEmpty(value))
            return false;

        var bucket = PlayerState.GetPlayersWithCustom(key, value);
        if (bucket.Count < 2)
            return false;

        var sightings = bucket
            .Select(member => (Uid: member, FirstSeen: PlayerState.GetCustom<TimeSpan?>(member, FirstSeenKey)))
            .ToList();

        return IsCloneInBucket(uid, sightings, CloneEligibleAfter);
    }

    /// <summary>
    /// Whether <paramref name="candidate"/> is the clone in a bucket sharing one forensic value. The
    /// earliest sighting is the original, unless nothing predates the point where a clone can exist.
    /// Then no member can be proven original and every one is hedged. Same frame sightings fall back
    /// to creation order. Pure so it is testable.
    /// </summary>
    internal static bool IsCloneInBucket(
        EntityUid candidate,
        IReadOnlyList<(EntityUid Uid, TimeSpan? FirstSeen)> bucket,
        TimeSpan cloneEligibleAfter)
    {
        if (bucket.Count < 2)
            return false;

        var original = bucket[0];
        foreach (var entry in bucket)
        {
            var entrySeen = entry.FirstSeen ?? TimeSpan.MaxValue;
            var originalSeen = original.FirstSeen ?? TimeSpan.MaxValue;

            // A tie means both showed up in one scan, so creation order settles it.
            if (entrySeen < originalSeen ||
                (entrySeen == originalSeen && entry.Uid.Id < original.Uid.Id))
            {
                original = entry;
            }
        }

        // Nothing seen before a clone can exist proves an original, so the whole bucket is a guess.
        if (original.FirstSeen is not { } seen || seen >= cloneEligibleAfter)
            return true;

        return original.Uid != candidate;
    }

#endregion

#region Changeling

    public IReadOnlyList<string> GetChangelingTriggers(EntityUid uid)
    {
        var triggers = new List<string>();

        // [GOOB] GoobStation's changeling marker, networked on the mob itself.
        if (HasComponentNamed(uid, GoobChangelingComponentName))
            triggers.Add("Changeling");

        // [GOOB] GoobStation's identity component, which carries the absorbed DNA list.
        if (HasComponentNamed(uid, GoobChangelingIdentityComponentName))
            triggers.Add("Absorbed DNA");

        // Upstream's slug form. Goob has no equivalent.
        if (HasComponentNamed(uid, ChangelingSlugComponentName))
            triggers.Add("Slug form");

        // Only on a ling mid transformation, or on the off map identity it stores.
        if (HasComponentNamed(uid, ChangelingHorrorComponentName))
            triggers.Add("Horror form");

        // Every full changeling and slug is granted these on assignment.
        if (TryComp<ActionGrantComponent>(uid, out var grant) && HasChangelingAction(grant.Actions))
            triggers.Add("Granted actions");

        // The store survives a disguise, so this catches one whose actions were stripped.
        if (TryComp<StoreComponent>(uid, out var store) && HeldCurrencies(store).Contains(ChangelingCurrency))
            triggers.Add("DNA store");

        return triggers;
    }

    private bool IsChangeling(EntityUid uid) => GetChangelingTriggers(uid).Count > 0;

    /// <summary>
    /// Whether any granted action is one only a changeling receives. Static and ECS-free so the
    /// allow-list is testable.
    /// </summary>
    internal static bool HasChangelingAction(IEnumerable<EntProtoId> actions)
    {
        foreach (var action in actions)
        {
            foreach (var changelingAction in _changelingActions)
            {
                if (string.Equals(action.Id, changelingAction, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Currencies a store holds a positive balance of. Read through reflection because FixedPoint2
    /// binds differently across Wizden and GoobStation.
    /// </summary>
    private static IEnumerable<string> HeldCurrencies(StoreComponent store) =>
        StoreBalance(store) is { } balance ? HeldCurrencies(balance) : [];

    /// <summary>Currencies a balance holds a positive amount of. Pure so it is testable.</summary>
    internal static IEnumerable<string> HeldCurrencies(System.Collections.IDictionary balance)
    {
        foreach (System.Collections.DictionaryEntry entry in balance)
        {
            var keyId = entry.Key?.ToString();
            if (keyId == null)
                continue;

            if (ReflectConvert.TryToDouble(entry.Value, out var amount) && amount > 0d)
                yield return keyId;
        }
    }

#endregion

#region Syndicate status

    public (bool Uplink, bool Contra, List<ImplantInfo> Implants) CheckPlayerStatus(EntityUid uid)
    {
        var now = _timing.CurTime;

        if (_cache.TryGetValue(uid, out var cached) && (now - cached.LastCheck) < _cacheLifetime)
        {
            return (cached.Uplink, cached.Contra, cached.Implants);
        }

        bool hasUplink = false;
        bool hasContra = false;
        var implants = new List<ImplantInfo>();
        var scannedEntities = new HashSet<EntityUid>();

        ScanEntity(uid, ref hasUplink, ref hasContra, scannedEntities);

        if (TryComp<ImplantedComponent>(uid, out var implanted))
        {
            bool foundSpecific = false;

            if (implanted.ImplantContainer != null)
            {
                foreach (var implant in implanted.ImplantContainer.ContainedEntities)
                {
                    if (TryComp<MetaDataComponent>(implant, out var meta) && meta.EntityPrototype != null)
                    {
                        var id = meta.EntityPrototype.ID;
                        var name = meta.EntityName.Replace(" implant", "");
                        string category = "Neutral";

                        if (id.Contains("Uplink") || id.Contains("MicroBomb") || id.Contains("MacroBomb") ||
                            id.Contains("Emp") || id.Contains("Scram") || id.Contains("Freedom") ||
                            id.Contains("Storage") || id.Contains("VoiceMask") || id.Contains("DnaScrambler") ||
                            id.Contains("Chameleon") || id.Contains("DeathAcidifier") || id.Contains("DeathRattleImplant") && !id.Contains("Centcomm") ||
                            id.Contains("FakeMindShield"))
                        {
                            category = "Syndicate";
                            if (id.Contains("Uplink")) hasUplink = true;
                        }
                        else if (id.Contains("MindShield") || id.Contains("Tracking") || id.Contains("Centcomm"))
                        {
                            category = "NT";
                        }

                        implants.Add(new ImplantInfo(name, category));
                        foundSpecific = true;
                    }
                }
            }

            // Fallback when no implant entities are visible.
            if (!foundSpecific)
            {
                implants.Add(new ImplantInfo("Unknown Implant", "Neutral"));
            }
        }

        if (_cache.Count >= StatusCacheLimit)
        {
            CacheUtility.RemoveOlderThan(_cache, entry => entry.LastCheck, now - _cacheLifetime);

            if (_cache.Count >= StatusCacheLimit)
                CacheUtility.RemoveOldest(_cache, entry => entry.LastCheck);
        }

        _cache[uid] = (hasUplink, hasContra, implants, now);
        return (hasUplink, hasContra, implants);
    }

    private bool CheckUplink(EntityUid target)
    {
        // A store on the entity itself, holding an antagonist currency. This is the original vector,
        // and it answers on the builds that keep the uplink store on the PDA.
        //
        // Current upstream does not: the ringer uplink spawns the store as a nullspace entity and
        // the PDA only carries a link to it. That link and the unlock flag are not networked, and a
        // nullspace entity never enters PVS, so a client has no state left to read. Do not add a PDA
        // component check here to "restore" it, because none of those fields leaves the server.
        if (TryComp<StoreComponent>(target, out var store) && HeldCurrencies(store).Any(_antagCurrencies.Contains))
            return true;

        // Hidden discounts. Older downstreams lack StoreDiscountComponent.
        if (StoreDiscountType != null &&
            EntityManager.TryGetComponent(target, StoreDiscountType, out var discountComponent) &&
            Reflect.Get(discountComponent, "Discounts") is System.Collections.IEnumerable discounts)
        {
            foreach (var discount in discounts)
            {
                if (discount == null)
                    continue;

                var categoryId = Reflect.GetString(discount, "DiscountCategory");
                if (categoryId != null &&
                    (categoryId.Contains("syndicate", StringComparison.OrdinalIgnoreCase) ||
                     categoryId.Contains("traitor", StringComparison.OrdinalIgnoreCase)))
                    return true;
            }
        }

        return false;
    }

    /// <summary>Whether a prototype id is on the exclusion list.</summary>
    internal static bool IsExcludedSyndicateGear(string prototypeId) =>
        IsExcludedSyndicateGearLowered(prototypeId.ToLowerInvariant());

    /// <summary>The same check against an already-lowered id.</summary>
    private static bool IsExcludedSyndicateGearLowered(string loweredId)
    {
        if (loweredId.Contains("armored") || loweredId.Contains("chameleon"))
            return false;

        foreach (var excluded in _syndicateGearExclusions)
        {
            if (loweredId.Contains(excluded))
                return true;
        }

        return false;
    }

    /// <summary>Whether this single item counts as syndicate gear.</summary>
    public bool IsSyndicateGear(EntityUid item)
    {
        if (!TryComp<MetaDataComponent>(item, out var meta) || meta.EntityPrototype == null)
            return false;

        var id = meta.EntityPrototype.ID.ToLowerInvariant();

        if (IsExcludedSyndicateGearLowered(id))
            return false;

        if (TryComp<ContrabandComponent>(item, out var contra) && contra.Severity == "Syndicate")
            return true;

        if (_tagSystem.HasTag(item, "Syndicate"))
            return true;

        bool isEmag = id.StartsWith("emag") || id.Contains("_emag") || id.Contains("emag_") || id == "emag";

        return id.Contains("syndicate") || isEmag || id.Contains("esword") || id.Contains("c4") ||
               id.Contains("stealthbox");
    }

    /// <summary>Every syndicate item the entity carries, containers included.</summary>
    public List<EntityUid> GetSyndicateGear(EntityUid player)
    {
        var found = new List<EntityUid>();
        IterateSyndicateGear(player, found, new HashSet<EntityUid>(), 0);
        return found;
    }

    private void IterateSyndicateGear(EntityUid parent, List<EntityUid> found, HashSet<EntityUid> scanned, int depth)
    {
        if (depth > 5 || !scanned.Add(parent))
            return;

        if (IsSyndicateGear(parent))
            found.Add(parent);

        if (!TryComp<ContainerManagerComponent>(parent, out var containerManager))
            return;

        foreach (var (containerId, container) in containerManager.Containers)
        {
            if (containerId.Equals("actions", StringComparison.OrdinalIgnoreCase))
                continue;

            foreach (var entity in container.ContainedEntities)
            {
                IterateSyndicateGear(entity, found, scanned, depth + 1);
            }
        }
    }

    private void ScanEntity(EntityUid parent, ref bool hasUplink, ref bool hasContraband, HashSet<EntityUid> scanned, int depth = 0)
    {
        if (depth > 5) return;
        if (hasUplink && hasContraband) return;
        if (!scanned.Add(parent)) return;

        if (!hasUplink && CheckUplink(parent)) hasUplink = true;
        if (!hasContraband && IsSyndicateGear(parent)) hasContraband = true;

        if (TryComp<ContainerManagerComponent>(parent, out var containerManager))
        {
            foreach (var (cId, container) in containerManager.Containers)
            {
                if (cId.Equals("actions", StringComparison.OrdinalIgnoreCase))
                    continue;

                foreach (var entity in container.ContainedEntities)
                {
                    ScanEntity(entity, ref hasUplink, ref hasContraband, scanned, depth + 1);
                }
            }
        }
    }

#endregion

#region Emag

    /// <summary>
    /// Whether an emag has subverted the entity. The law provider and its owner stay on the server, so
    /// this networked marker is the only emag evidence a client gets.
    /// </summary>
    public bool IsEmagged(EntityUid target) =>
        TryComp<EmaggedComponent>(target, out var emagged) && IsEmaggedFlag(emagged.EmagType);

    /// <summary>
    /// Whether an emag type carries the interaction flag. Airlocks only set
    /// <see cref="EmagType.Access"/>, which says nothing about a silicon's laws.
    /// </summary>
    internal static bool IsEmaggedFlag(EmagType type) =>
        (type & EmagType.Interaction) == EmagType.Interaction;

#endregion

#region Wanted

    public bool IsWanted(EntityUid target) => IsWantedStatus(GetWantedStatus(target));

    /// <summary>The entity's criminal status, or <see cref="SecurityStatus.None"/> when it has no record.</summary>
    public SecurityStatus GetWantedStatus(EntityUid target)
    {
        if (!EntityManager.EntityExists(target))
            return SecurityStatus.None;

        if (GetStationInMap(Transform(target).MapID) is not { } station)
            return SecurityStatus.None;

        return GetWantedMap(station).GetValueOrDefault(Name(target), SecurityStatus.None);
    }

    /// <summary>Whether a status counts as wanted. Suspected is upstream's weaker hedge.</summary>
    internal static bool IsWantedStatus(SecurityStatus status) =>
        status is SecurityStatus.Wanted or SecurityStatus.Hostile;

    private Dictionary<string, SecurityStatus> GetWantedMap(EntityUid station)
    {
        if (_wantedCache.TryGetValue(station, out var cached) &&
            _timing.CurTime - cached.Stamp < WantedCacheLifetime)
        {
            return cached.Map;
        }

        var map = BuildWantedMap(station);
        _wantedCache[station] = (map, _timing.CurTime);
        return map;
    }

    /// <summary>
    /// Every non-<see cref="SecurityStatus.None"/> status on the station, keyed by record name.
    /// </summary>
    /// <remarks>
    /// The component is resolved by name because its namespace differs between builds and a generic
    /// <c>TryComp</c> bakes the full type name into the IL. The record types share a namespace on both
    /// builds, so they are used directly.
    /// </remarks>
    private Dictionary<string, SecurityStatus> BuildWantedMap(EntityUid station)
    {
        var map = new Dictionary<string, SecurityStatus>(StringComparer.Ordinal);

        if (!_compFactory.TryGetRegistration(StationRecordsComponentName, out var registration) ||
            !TryComp(station, registration.Type, out var component) ||
            Reflect.Get(component, "Records") is not StationRecordSet records)
        {
            return map;
        }

        foreach (var (id, general) in records.GetRecordsOfType<GeneralStationRecord>())
        {
            if (!records.TryGetRecordEntry<CriminalRecord>(id, out var criminal))
                continue;

            if (criminal.Status != SecurityStatus.None)
                map[general.Name] = criminal.Status;
        }

        return map;
    }

    /// <summary>
    /// The station whose grids cover the map, or null. Walks the station component directly because
    /// the station system's name differs between builds.
    /// </summary>
    private EntityUid? GetStationInMap(MapId map)
    {
        var query = EntityQueryEnumerator<StationDataComponent>();
        while (query.MoveNext(out var uid, out var data))
        {
            foreach (var grid in data.Grids)
            {
                if (Transform(grid).MapID == map)
                    return uid;
            }
        }

        return null;
    }

#endregion
}
