using System.Numerics;
using Robust.Client.Graphics;
using Robust.Shared.Enums;
using Robust.Shared.GameObjects;
using Robust.Shared.IoC;
using Robust.Shared.Maths;
using Content.Shared.Mobs.Components;
using Robust.Client.Player;
using Robust.Client.ResourceManagement;
using Content.Shared.Inventory;
using Robust.Shared.Player; 
using Content.Shared.PDA; 
using Content.Shared.Access.Components; 
using Content.Shared.Mind.Components;
using Content.Shared.PAI;

using Content.Shared.Hands.Components;
using Content.Shared.Hands.EntitySystems;
using Content.Shared.Mobs.Systems;
using Content.Shared.Mobs;
using System.Collections.Generic; // Added for List

namespace RobustoClient.Systems;

public sealed class RobustaEspOverlay : Overlay
{
    private readonly IEntityManager _entMan;
    private readonly IPlayerManager _player;
    private readonly Font _font;
    private readonly SharedTransformSystem _xformSystem;
    private readonly InventorySystem _invSystem;
    private readonly SharedHandsSystem _handsSystem;
    private readonly IComponentFactory _compFactory;

    private bool _mindReflected;
    private System.Reflection.PropertyInfo? _mindProp;
    private System.Reflection.FieldInfo? _mindField;

    public override OverlaySpace Space => OverlaySpace.ScreenSpace;

    public RobustaEspOverlay()
    {
        _entMan = IoCManager.Resolve<IEntityManager>();
        _player = IoCManager.Resolve<IPlayerManager>();
        _compFactory = IoCManager.Resolve<IComponentFactory>();
        _xformSystem = _entMan.System<SharedTransformSystem>();
        _invSystem = _entMan.System<InventorySystem>();
        _handsSystem = _entMan.System<SharedHandsSystem>();
        
        var cache = IoCManager.Resolve<IResourceCache>();
        _font = new VectorFont(cache.GetResource<FontResource>("/Fonts/NotoSans/NotoSans-Regular.ttf"), 10);
    }

    protected override void Draw(in OverlayDrawArgs args)
    {
        if (!RobustaConfig.EspEnabled) return;

        var screenHandle = args.ScreenHandle;
        var localPlayer = _player.LocalSession?.AttachedEntity;
        var eyeMapId = args.Viewport.Eye?.Position.MapId;
        
        var syndiDetector = _entMan.System<RobustaSyndicateDetectorSystem>();

        // ==========================================
        // BLOCK 1: ALIVE PLAYERS
        // ==========================================
        var query = _entMan.EntityQueryEnumerator<MobStateComponent, MindContainerComponent, TransformComponent, MetaDataComponent>();
        
        while (query.MoveNext(out var uid, out var mob, out var mind, out var xform, out var meta))
        {
            if (uid == localPlayer || xform.MapID != eyeMapId) continue;
            
            if (!_mindReflected)
            {
                var type = mind.GetType();
                _mindProp = type.GetProperty("HasMind");
                _mindField = type.GetField("HasMind");
                _mindReflected = true;
            }

            bool hasMind = false;
            if (_mindProp != null)
                hasMind = (bool)(_mindProp.GetValue(mind) ?? false);
            else if (_mindField != null)
                hasMind = (bool)(_mindField.GetValue(mind) ?? false);

            if (!hasMind) continue;
            
            if (_entMan.HasComponent<PAIComponent>(uid)) continue;

            var worldPos = _xformSystem.GetWorldPosition(xform);
            var screenPos = args.ViewportControl?.WorldToScreen(worldPos);
            if (screenPos == null) continue;

            // --- JOB AND NAME ---
            string jobTitle = "";
            if (_invSystem.TryGetSlotEntity(uid, "id", out var slotEnt) && slotEnt.HasValue)
            {
                EntityUid? actualIdCard = slotEnt;
                if (_entMan.TryGetComponent<PdaComponent>(slotEnt.Value, out var pda))
                    actualIdCard = pda.ContainedId;

                if (actualIdCard.HasValue && _entMan.TryGetComponent<IdCardComponent>(actualIdCard.Value, out var idCard))
                    jobTitle = idCard.LocalizedJobTitle ?? "";
            }

            // --- UNIFIED RENDERING ---
            var basePos = screenPos.Value;
            float leftX = basePos.X - 40;
            float rightX = basePos.X + 40;
            float topY = basePos.Y - 45;
            float bottomY = basePos.Y + 10;
            
            // 1. Name and Job (Top to bottom from head)
            var nameColor = Color.White;
            if (jobTitle.Contains("Security") || jobTitle.Contains("Officer") || jobTitle.Contains("Warden") || jobTitle.Contains("Captain"))
                nameColor = Color.FromHex("#7070ff");
            else if (jobTitle.Contains("Medical") || jobTitle.Contains("Doctor"))
                nameColor = Color.FromHex("#70ff70");
            else if (jobTitle.Contains("Engineer"))
                nameColor = Color.FromHex("#ffff70");

            var displayName = string.IsNullOrEmpty(jobTitle) ? meta.EntityName : $"[{jobTitle}] {meta.EntityName}";
            screenHandle.DrawString(_font, new Vector2(leftX, topY), displayName, nameColor);
            topY -= 12;

            if (_entMan.TryGetComponent<ActorComponent>(uid, out var actor))
            {
                screenHandle.DrawString(_font, new Vector2(leftX, topY), $"@{actor.PlayerSession.Name}", Color.DarkGray);
                topY -= 12;
            }

            // 2. Item in hands (Bottom)
            if (_entMan.TryGetComponent<HandsComponent>(uid, out var hands))
            {
                var heldItems = new List<string>();
                foreach (var handName in hands.Hands.Keys)
                {
                    EntityUid? held = null;
                    var type = _handsSystem.GetType();
                    
                    var method3 = type.GetMethod("GetHeldItem", new[] { typeof(Entity<HandsComponent>), typeof(string), typeof(bool) });
                    if (method3 != null)
                    {
                        held = (EntityUid?)method3.Invoke(_handsSystem, new object?[] { new Entity<HandsComponent>(uid, hands), handName, false });
                    }
                    else
                    {
                        var method2 = type.GetMethod("GetHeldItem", new[] { typeof(Entity<HandsComponent>), typeof(string) });
                        if (method2 != null)
                        {
                            held = (EntityUid?)method2.Invoke(_handsSystem, new object?[] { new Entity<HandsComponent>(uid, hands), handName });
                        }
                    }

                    if (held != null && _entMan.TryGetComponent<MetaDataComponent>(held.Value, out var heldMeta))
                    {
                        heldItems.Add(heldMeta.EntityName);
                    }
                }

                if (heldItems.Count > 0)
                {
                    var text = ">> " + string.Join(" | ", heldItems) + " <<";
                    screenHandle.DrawString(_font, new Vector2(leftX, bottomY), text, Color.Orange);
                }
            }

            // 3. Status and Implants (Right side)
            var syndiStatus = syndiDetector.CheckPlayerStatus(uid);
            float sideY = basePos.Y - 30;

            if (syndiStatus.Uplink)
            {
                screenHandle.DrawString(_font, new Vector2(rightX, sideY), "ANTAG", Color.Red);
                sideY += 12;
            }
            else if (syndiStatus.Contra)
            {
                screenHandle.DrawString(_font, new Vector2(rightX, sideY), "SUS", Color.Orange);
                sideY += 12;
            }

            foreach (var implant in syndiStatus.Implants)
            {
                var impColor = implant.Category switch
                {
                    "Syndicate" => Color.Red,
                    "NT" => Color.Cyan,
                    _ => Color.LightGray
                };
                screenHandle.DrawString(_font, new Vector2(rightX, sideY), $"[{implant.Name}]", impColor);
                sideY += 12;
            }
        }

        // ==========================================
        // BLOCK 2: ADMINS AND GHOSTS (DYNAMIC LOOKUP)
        // ==========================================
        
        // Dynamically get the Ghost component type to avoid TypeLoadException on older game versions
        if (!_compFactory.TryGetRegistration("Ghost", out var ghostRegistration))
            return;
            
        var ghostType = ghostRegistration.Type;
        
        var allEntities = _entMan.EntityQueryEnumerator<TransformComponent, MetaDataComponent>();
        while (allEntities.MoveNext(out var uid, out var xform, out var meta))
        {
            if (uid == localPlayer || xform.MapID != eyeMapId) continue;
            
            // Check if entity has the dynamically resolved Ghost component
            if (!_entMan.HasComponent(uid, ghostType)) continue;

            var worldPos = _xformSystem.GetWorldPosition(xform);
            var screenPos = args.ViewportControl?.WorldToScreen(worldPos);
            if (screenPos == null) continue;

            if (_entMan.TryGetComponent<ActorComponent>(uid, out var actor))
            {
                var isAdmin = meta.EntityPrototype?.ID.Contains("Admin") ?? false;
                var color = isAdmin ? Color.Red : Color.Gray;
                var prefix = isAdmin ? "[!!! ADMIN !!!]" : "[GHOST]";
                screenHandle.DrawString(_font, screenPos.Value, $"{prefix} {actor.PlayerSession.Name}", color);
            }
        }
    }
}