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
    private readonly IEntityManager _entMan;
    private readonly IPlayerManager _player;
    private readonly IEyeManager _eyeManager;
    private readonly Font _font;
    private readonly EspLayoutManager _layoutManager;
    private readonly SharedTransformSystem _xformSystem;
    private readonly EntityLookupSystem _lookup;
    private readonly EspEvaluator _esp;
    
    private readonly RobustaSyndicateDetectorSystem _syndi;
    private readonly InventorySystem _inv;
    private readonly SharedHandsSystem _hands;

    private readonly FieldInfo? _pendingStatesField;
    private readonly PropertyInfo? _hasMindProp;
    private readonly FieldInfo? _hasMindField;
    private readonly List<EntityUid> _cachedViewportEntities = new();
    private int _entityCacheFrameCounter = 10;
    private MapId _lastCachedMapId = MapId.Nullspace;

    public override OverlaySpace Space => OverlaySpace.ScreenSpace;

    public RobustaEspOverlay()
    {
        _entMan = IoCManager.Resolve<IEntityManager>();
        _player = IoCManager.Resolve<IPlayerManager>();
        _eyeManager = IoCManager.Resolve<IEyeManager>();
        _xformSystem = _entMan.System<SharedTransformSystem>();
        _lookup = _entMan.System<EntityLookupSystem>();
        _esp = _entMan.System<EspEvaluator>();
        _syndi = _entMan.System<RobustaSyndicateDetectorSystem>();
        _inv = _entMan.System<InventorySystem>();
        _hands = _entMan.System<SharedHandsSystem>();
        
        var cache = IoCManager.Resolve<IResourceCache>();
        _font = new VectorFont(cache.GetResource<FontResource>("/Fonts/NotoSans/NotoSans-Regular.ttf"), 10);
        _layoutManager = new EspLayoutManager(_font);

        _pendingStatesField = _player.GetType().GetField("_pendingStates", BindingFlags.NonPublic | BindingFlags.Instance);
        _hasMindProp = typeof(MindContainerComponent).GetProperty("HasMind");
        _hasMindField = typeof(MindContainerComponent).GetField("HasMind");
        
        RegisterHooks();
    }

    private void RegisterHooks()
    {
        _esp.RegisterVariable("PlayerName", uid => {
            foreach (var session in _player.SessionsDict.Values)
            {
                if (session.AttachedEntity == uid)
                    return $"@{session.Name}";
            }
            
            var netUid = _entMan.GetNetEntity(uid);
            if (_pendingStatesField != null)
            {
                var pending = _pendingStatesField.GetValue(_player) as Dictionary<Robust.Shared.Network.NetUserId, Robust.Shared.GameStates.SessionState>;
                if (pending != null)
                {
                    foreach (var state in pending.Values)
                    {
                        if (state.ControlledEntity == netUid)
                            return $"@{state.Name}";
                    }
                }
            }
            return "";
        });
        
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
            var status = _syndi.CheckPlayerStatus(uid);
            if (status.Implants.Count == 0) return "";
            var list = new List<string>();
            foreach (var imp in status.Implants) list.Add($"[{imp.Name}]");
            return string.Join("\n", list); 
        });

        _esp.RegisterCondition("IsAlive", uid => {
            if (!_entMan.TryGetComponent<MindContainerComponent>(uid, out var mind)) return false;
            if (_hasMindProp != null) return (bool)(_hasMindProp.GetValue(mind) ?? false);
            if (_hasMindField != null) return (bool)(_hasMindField.GetValue(mind) ?? false);
            return false;
        });

        _esp.RegisterCondition("IsAntag", uid => _entMan.HasComponent<MindContainerComponent>(uid) && _syndi.CheckPlayerStatus(uid).Uplink);
        _esp.RegisterCondition("IsSus", uid => _entMan.HasComponent<MindContainerComponent>(uid) && _syndi.CheckPlayerStatus(uid).Contra);
        _esp.RegisterCondition("HasHeldItems", uid => !string.IsNullOrEmpty(_esp.ResolveVariable(uid, "HeldItems")));
        _esp.RegisterCondition("HasImplants", uid => _entMan.HasComponent<MindContainerComponent>(uid) && _syndi.CheckPlayerStatus(uid).Implants.Count > 0);
    }

    protected override void Draw(in OverlayDrawArgs args)
    {
        if (!RobustaConfig.EspEnabled) return;

        var screenHandle = args.ScreenHandle;
        var localPlayer = _player.LocalSession?.AttachedEntity;
        
        var eye = args.Viewport.Eye;
        if (eye == null) return;
        
        var eyeMapId = eye.Position.MapId;
        if (eyeMapId == MapId.Nullspace) return;

        _entityCacheFrameCounter++;
        if (_entityCacheFrameCounter >= 10 || _lastCachedMapId != eyeMapId)
        {
            _entityCacheFrameCounter = 0;
            _lastCachedMapId = eyeMapId;
            _cachedViewportEntities.Clear();
            var worldViewport = _eyeManager.GetWorldViewport();
            foreach (var ent in _lookup.GetEntitiesIntersecting(eyeMapId, worldViewport, LookupFlags.Dynamic | LookupFlags.Sundries))
            {
                _cachedViewportEntities.Add(ent);
            }
        }

        foreach (var uid in _cachedViewportEntities)
        {
            if (uid == localPlayer || !_entMan.EntityExists(uid)) continue;
            
            var results = _esp.Evaluate(uid);
            if (results.Count == 0) continue;

            if (!_entMan.TryGetComponent<TransformComponent>(uid, out var xform)) continue;

            var worldPos = _xformSystem.GetWorldPosition(xform);
            var screenPos = args.ViewportControl?.WorldToScreen(worldPos);
            if (screenPos == null) continue;

            _layoutManager.Reset(screenHandle, screenPos.Value);
            
            foreach (var res in results)
            {
                _layoutManager.Add(res.Category.Dock, res.RenderText, res.Category.Color, res.Category.Priority);
            }

            _layoutManager.DrawAll();
        }
    }
}