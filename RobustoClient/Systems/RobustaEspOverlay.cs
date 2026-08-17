using System.Numerics;
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
    private readonly SharedTransformSystem _xformSystem;
    private readonly EntityLookupSystem _lookup;
    private readonly EspEvaluator _esp;
    
    private readonly RobustaSyndicateDetectorSystem _syndi;
    private readonly InventorySystem _inv;
    private readonly SharedHandsSystem _hands;

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
        
        RegisterHooks();
    }

    private void RegisterHooks()
    {
        _esp.RegisterVariable("PlayerName", uid => _entMan.TryGetComponent<ActorComponent>(uid, out var actor) ? $"@{actor.PlayerSession.Name}" : "");
        
        _esp.RegisterVariable("JobTitle", uid => {
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
            if (!_entMan.TryGetComponent<HandsComponent>(uid, out var h)) return "";
            var heldItems = new List<string>();
            foreach (var handName in h.Hands.Keys)
            {
                EntityUid? held = null;
                var type = _hands.GetType();
                var method3 = type.GetMethod("GetHeldItem", new[] { typeof(Entity<HandsComponent>), typeof(string), typeof(bool) });
                if (method3 != null) held = (EntityUid?)method3.Invoke(_hands, new object?[] { new Entity<HandsComponent>(uid, h), handName, false });
                else {
                    var method2 = type.GetMethod("GetHeldItem", new[] { typeof(Entity<HandsComponent>), typeof(string) });
                    if (method2 != null) held = (EntityUid?)method2.Invoke(_hands, new object?[] { new Entity<HandsComponent>(uid, h), handName });
                }
                if (held != null && _entMan.TryGetComponent<MetaDataComponent>(held.Value, out var heldMeta)) 
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
            var status = _syndi.CheckPlayerStatus(uid);
            if (status.Implants.Count == 0) return "";
            var list = new List<string>();
            foreach (var imp in status.Implants) list.Add($"[{imp.Name}]");
            return string.Join("\n", list); 
        });

        _esp.RegisterCondition("IsAlive", uid => {
            if (!_entMan.TryGetComponent<MindContainerComponent>(uid, out var mind)) return false;
            var type = mind.GetType();
            var prop = type.GetProperty("HasMind");
            if (prop != null) return (bool)(prop.GetValue(mind) ?? false);
            var field = type.GetField("HasMind");
            if (field != null) return (bool)(field.GetValue(mind) ?? false);
            return false;
        });

        _esp.RegisterCondition("IsAntag", uid => _syndi.CheckPlayerStatus(uid).Uplink);
        _esp.RegisterCondition("IsSus", uid => _syndi.CheckPlayerStatus(uid).Contra);
        _esp.RegisterCondition("HasHeldItems", uid => !string.IsNullOrEmpty(_esp.ResolveVariable(uid, "HeldItems")));
        _esp.RegisterCondition("HasImplants", uid => _syndi.CheckPlayerStatus(uid).Implants.Count > 0);
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

        var worldViewport = _eyeManager.GetWorldViewport();
        var entities = _lookup.GetEntitiesIntersecting(eyeMapId, worldViewport);

        foreach (var uid in entities)
        {
            if (uid == localPlayer) continue;
            
            var results = _esp.Evaluate(uid);
            if (results.Count == 0) continue;

            if (!_entMan.TryGetComponent<TransformComponent>(uid, out var xform)) continue;

            var worldPos = _xformSystem.GetWorldPosition(xform);
            var screenPos = args.ViewportControl?.WorldToScreen(worldPos);
            if (screenPos == null) continue;

            var layout = new EspLayoutManager(screenHandle, _font, screenPos.Value);
            
            foreach (var res in results)
            {
                layout.Add(res.Category.Dock, res.RenderText, res.Category.Color, res.Category.Priority);
            }

            layout.DrawAll();
        }
    }
}