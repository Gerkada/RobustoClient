using System.Numerics;
using System.Reflection;
using Robust.Client.Graphics;
using Robust.Shared.Enums;
using Robust.Shared.GameObjects;
using Robust.Shared.IoC;
using Robust.Shared.Maths;
using Robust.Client.Player;
using Robust.Client.ResourceManagement;
using RobustoClient.Systems.ESP;
using RobustoClient.Systems.PlayerState;
using RobustoClient.ImGui;
using RobustoClient.Reflection;
using System.Collections.Generic;
using Content.Shared.Inventory;
using Content.Shared.PDA;
using Content.Shared.Access.Components;
using Content.Shared.Mind.Components;
using Content.Shared.Hands.Components;
using Content.Shared.Hands.EntitySystems;
using Content.Shared.Mobs.Components;
using Robust.Shared.Player;
using Robust.Shared.Map;

namespace RobustoClient.Systems;

public sealed class RobustaEspOverlay : Overlay
{
    /// <summary>ESP rule marking a mob that no player controls. Referenced by esp.yml, so it is a constant.</summary>
    internal const string NonPlayerMobRule = "IsNonPlayerMob";

    /// <summary>
    /// Component each rule cannot match without. The category index buckets by it, so a rule missing
    /// from this table runs for every entity in view, walls included, and each of those asks the
    /// player-state layer for a snapshot. The ESP tests assert the shipped rules are all covered.
    /// </summary>
    internal static readonly IReadOnlyDictionary<string, string> RuleGates =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["IsAlive"] = "MindContainer",
            ["IsAntag"] = "MindContainer",
            ["IsDetectedAntag"] = "MindContainer",
            ["IsClone"] = "MindContainer",
            ["IsThief"] = "MindContainer",
            // Contraband sits in containers, so a crate or a locker stays covered.
            ["IsSus"] = "ContainerManager",
            ["IsCommand"] = "Inventory",
            ["IsSecurity"] = "Inventory",
            ["HasHeldItems"] = "Hands",
            ["HasImplants"] = "Implanted",
            [NonPlayerMobRule] = "MobState",
        };

    private readonly IEntityManager _entMan;
    private readonly IPlayerManager _player;
    private readonly IEyeManager _eyeManager;
    private readonly Font _font;
    private readonly EspLayoutManager _layoutManager;
    private readonly SharedTransformSystem _xformSystem;
    private readonly EntityLookupSystem _lookup;
    private readonly EspEvaluator _esp;
    
    private readonly PlayerStateUtility _playerState;
    private readonly InventorySystem _inv;
    private readonly SharedHandsSystem _hands;

    /// <summary>Entities with at least one ESP result, rebuilt every <see cref="EvaluateInterval"/> frames.</summary>
    private readonly List<(EntityUid Uid, List<EspEvaluationResult> Results)> _drawList = new();

    /// <summary>Reused query buffer, the lookup system fills it instead of allocating a set per pass.</summary>
    private readonly HashSet<EntityUid> _viewportBuffer = new();

    /// <summary>Entities whose evaluation already threw, so the log names each one once.</summary>
    private readonly HashSet<EntityUid> _faultedEntities = new();

    /// <summary>
    /// Frames between ESP passes. The rules read the player-state layer, which walks containers and
    /// station records, so they run a few times a second instead of once per entity per frame. Only
    /// entities that produced a result reach the per-frame draw loop.
    /// </summary>
    private const int EvaluateInterval = 6;

    private int _refreshCounter = EvaluateInterval;
    private MapId _lastCachedMapId = MapId.Nullspace;

    /// <summary>Session name per controlled entity, rebuilt with the viewport cache.</summary>
    private readonly Dictionary<EntityUid, string> _sessionNames = new();

    public override OverlaySpace Space => OverlaySpace.ScreenSpace;

    public RobustaEspOverlay()
    {
        _entMan = IoCManager.Resolve<IEntityManager>();
        _player = IoCManager.Resolve<IPlayerManager>();
        _eyeManager = IoCManager.Resolve<IEyeManager>();
        _xformSystem = _entMan.System<SharedTransformSystem>();
        _lookup = _entMan.System<EntityLookupSystem>();
        _esp = _entMan.System<EspEvaluator>();
        _playerState = _entMan.System<PlayerStateUtility>();
        _inv = _entMan.System<InventorySystem>();
        _hands = _entMan.System<SharedHandsSystem>();
        
        var cache = IoCManager.Resolve<IResourceCache>();
        _font = new VectorFont(cache.GetResource<FontResource>("/Fonts/NotoSans/NotoSans-Regular.ttf"), 10);
        _layoutManager = new EspLayoutManager(_font);

        RegisterHooks();
    }

    private void RegisterHooks()
    {
        _esp.RegisterVariable("PlayerName", uid =>
            _sessionNames.TryGetValue(uid, out var name) ? $"@{name}" : "");
        
        _esp.RegisterVariable("JobTitle", uid => {
            if (!_entMan.HasComponent<MindContainerComponent>(uid)) return "";
            if (_inv.TryGetSlotEntity(uid, "id", out var slotEnt) && slotEnt.HasValue) {
                EntityUid? actualIdCard = slotEnt;
                if (_entMan.TryGetComponent<PdaComponent>(slotEnt.Value, out var pda))
                    actualIdCard = pda.ContainedId;
                if (actualIdCard.HasValue && _entMan.TryGetComponent<IdCardComponent>(actualIdCard.Value, out var idCard))
                    return idCard.LocalizedJobTitle ?? "";
            }
            return "";
        });

        _esp.RegisterVariable("HeldItems", uid => {
            if (!_entMan.HasComponent<HandsComponent>(uid)) return "";
            var heldItems = new List<string>();
            foreach (var held in _hands.EnumerateHeld(uid))
            {
                if (_entMan.TryGetComponent<MetaDataComponent>(held, out var heldMeta)) 
                {
                    if (heldMeta.EntityPrototype?.ID == "VirtualItem" || 
                        heldMeta.EntityPrototype?.ID == "ActionDummy" ||
                        heldMeta.EntityName == "VIRTUAL ITEM YOU SHOULD NOT SEE THIS")
                        continue;

                    heldItems.Add(heldMeta.EntityName);
                }
            }
            return string.Join(" | ", heldItems);
        });

        _esp.RegisterVariable("Implants", uid => {
            if (!_entMan.HasComponent<MindContainerComponent>(uid)) return "";
            var implants = _playerState.GetSnapshot(uid).Syndicate?.Implants;
            if (implants is not { Count: > 0 }) return "";
            var list = new List<string>();
            foreach (var imp in implants) list.Add($"[{imp.Name}]");
            return string.Join("\n", list); 
        });

        // The antagonist the state layer recognised, hedged when the tell is a guess rather than a
        // marker component. Empty for everyone else, so the category simply does not draw. Clone and
        // thief are left out because CloneTag and ThiefTag already name them.
        _esp.RegisterVariable("Antagonist", uid =>
        {
            if (_playerState.GetSnapshot(uid).Antagonist is not { } antagonist)
                return "";

            if (antagonist.Kind is AntagonistKind.Clone or AntagonistKind.Thief)
                return "";

            var label = antagonist.Kind switch
            {
                AntagonistKind.NukeOperative => "NUKIE",
                AntagonistKind.HeadRevolutionary => "HEADREV",
                AntagonistKind.Revolutionary => "REV",
                _ => antagonist.Kind.ToString().ToUpperInvariant()
            };

            return antagonist.Hedged || antagonist.Kind.IsHedged() ? $"[{label}?]" : $"[{label}]";
        });

        // The tag takes the same color the radar gives the blip, so the two overlays agree.
        _esp.RegisterColor("AntagonistColor", uid =>
            _playerState.GetSnapshot(uid).Antagonist is { } antagonist
                ? ToColor(RobustaImGuiTheme.AntagonistColor(antagonist.Kind))
                : null);

        RegisterRule("IsAlive", uid => _playerState.GetSnapshot(uid).Flags.Has(PlayerFlag.HasMind));
        // A detected kind names the antagonist, so the generic ANTAG tag would only repeat it.
        RegisterRule("IsAntag", uid =>
        {
            var snapshot = _playerState.GetSnapshot(uid);
            return snapshot.Flags.Has(PlayerFlag.IsSyndicate) && snapshot.Antagonist is null;
        });
        RegisterRule("IsDetectedAntag", uid => _playerState.GetSnapshot(uid).Antagonist is not null);
        RegisterRule("IsClone", uid =>
            _playerState.GetSnapshot(uid).Antagonist?.Kind == AntagonistKind.Clone);
        RegisterRule("IsThief", uid => _playerState.GetSnapshot(uid).Flags.Has(PlayerFlag.IsThief));
        // A recognised antagonist already carries a tag that names it, so the hedged SUS would only
        // repeat it. Same rule the radar applies to its [SYNDICATE?] tag.
        RegisterRule("IsSus", uid =>
        {
            var snapshot = _playerState.GetSnapshot(uid);
            return snapshot.Syndicate?.HasHedgedSignal == true && snapshot.Antagonist is null;
        });
        RegisterRule("IsCommand", uid =>
            _playerState.GetSnapshot(uid).Flags.Has(PlayerFlag.IsCommand));
        RegisterRule("IsSecurity", uid =>
            _playerState.GetSnapshot(uid).Flags.Has(PlayerFlag.IsSecurity));
        RegisterRule("HasHeldItems", uid => !string.IsNullOrEmpty(_esp.ResolveVariable(uid, "HeldItems")));
        RegisterRule("HasImplants", uid =>
            _playerState.GetSnapshot(uid).Syndicate is { Implants.Count: > 0 });

        RegisterRule(NonPlayerMobRule, uid =>
            _entMan.HasComponent<MobStateComponent>(uid) &&
            !_playerState.GetSnapshot(uid).Flags.Has(PlayerFlag.HasMind));
    }

    /// <summary>Registers a rule with the component gate <see cref="RuleGates"/> declares for it.</summary>
    private void RegisterRule(string name, Func<EntityUid, bool> predicate) =>
        _esp.RegisterCondition(name, predicate, RuleGates.GetValueOrDefault(name));


    /// <summary>Converts a theme color to the engine's color type, or null when there is none.</summary>
    private static Color? ToColor(Vector4? color) =>
        color is { } value ? new Color(value.X, value.Y, value.Z, value.W) : null;

    protected override void Draw(in OverlayDrawArgs args)
    {
        if (!RobustaConfig.EspOverlayEnabled || !RobustaConfig.Esp.AnyEnabled) return;

        var screenHandle = args.ScreenHandle;
        var localPlayer = _player.LocalSession?.AttachedEntity;
        
        var eye = args.Viewport.Eye;
        if (eye == null) return;
        
        var eyeMapId = eye.Position.MapId;
        if (eyeMapId == MapId.Nullspace) return;

        _refreshCounter++;
        if (_refreshCounter >= EvaluateInterval || _lastCachedMapId != eyeMapId)
        {
            _refreshCounter = 0;
            _lastCachedMapId = eyeMapId;
            RebuildDrawList(localPlayer, eyeMapId);
        }

        foreach (var (uid, results) in _drawList)
        {
            if (!_entMan.TryGetComponent<TransformComponent>(uid, out var xform)) continue;

            var worldPos = _xformSystem.GetWorldPosition(xform);
            var screenPos = args.ViewportControl?.WorldToScreen(worldPos);
            if (screenPos == null) continue;

            _layoutManager.Reset(screenHandle, screenPos.Value);
            
            foreach (var res in results)
            {
                // Each category answers to one user-facing switch.
                if (!RobustaConfig.EspOverlayEnabled || !RobustaConfig.Esp.IsEnabled(res.Category.Feature))
                    continue;

                var color = res.Color ?? RobustaConfig.Esp.ResolveColor(res.Category.Feature, res.Category.Color);
                _layoutManager.Add(res.Category.Dock, res.RenderText, color, res.Category.Priority);
            }

            _layoutManager.DrawAll();
        }
    }

    /// <summary>
    /// Re-evaluates the rules for everything in view and keeps only the entities that produced a
    /// result. Throttled by <see cref="EvaluateInterval"/>: running this per entity per frame is what
    /// made a crowded room unplayable.
    /// </summary>
    private void RebuildDrawList(EntityUid? localPlayer, MapId eyeMapId)
    {
        _drawList.Clear();
        _viewportBuffer.Clear();

        _lookup.GetEntitiesIntersecting(eyeMapId, _eyeManager.GetWorldViewport(), _viewportBuffer,
            LookupFlags.Dynamic | LookupFlags.Sundries);

        foreach (var uid in _viewportBuffer)
        {
            if (uid == localPlayer || !_entMan.EntityExists(uid))
                continue;

            // One entity that throws must not blank the overlay for every other entity in view.
            try
            {
                var results = _esp.Evaluate(uid);
                if (results.Count > 0)
                    _drawList.Add((uid, results));
            }
            catch (Exception e)
            {
                ReportFaultedEntity(uid, e);
            }
        }

        RebuildSessionNames();
    }

    /// <summary>Logs a throwing entity once, so a broken rule is visible without flooding the log.</summary>
    private void ReportFaultedEntity(EntityUid uid, Exception e)
    {
        if (!_faultedEntities.Add(uid))
            return;

        MarseyLogger.Warn($"[ESP] Evaluating {uid} threw, it is skipped: {e}");
    }

    /// <summary>Builds the entity-to-name index the PlayerName variable reads.</summary>
    private void RebuildSessionNames()
    {
        _sessionNames.Clear();

        foreach (var session in _player.SessionsDict.Values)
        {
            if (session.AttachedEntity is { } attached)
                _sessionNames[attached] = session.Name;
        }

        // A session that has not attached yet still names its entity.
        if (Reflect.Get(_player, "_pendingStates", ReflectOptions.Any)
            is Dictionary<Robust.Shared.Network.NetUserId, Robust.Shared.GameStates.SessionState> pending)
        {
            foreach (var state in pending.Values)
            {
                if (state.ControlledEntity is not { } netEntity)
                    continue;

                if (_entMan.TryGetEntity(netEntity, out var uid) && uid is { } entity)
                    _sessionNames.TryAdd(entity, state.Name);
            }
        }
    }
}