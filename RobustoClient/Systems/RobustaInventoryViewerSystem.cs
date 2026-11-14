using System;
using System.Collections.Generic;
using System.Linq;
using Content.Shared.Inventory;
using Content.Shared.Hands.Components;
using Content.Shared.Hands.EntitySystems;
using Content.Shared.Mobs.Components;
using Content.Shared.Verbs;
using Robust.Client.GameObjects;
using Robust.Client.Graphics;
using Robust.Client.Input;
using Robust.Client.Player;
using Robust.Client.UserInterface;
using Robust.Shared.Containers;
using Robust.Shared.GameObjects;
using Robust.Shared.IoC;
using Robust.Shared.Map;
using Robust.Shared.Player;
using Robust.Shared.Utility;
using RobustoClient.UI;
using RobustoClient.UI.ImGui;

namespace RobustoClient.Systems;

public sealed class InventoryEntry
{
    public string ContainerName { get; set; } = "";
    public string ItemName { get; set; } = "";
    public string PrototypeId { get; set; } = "";
    public EntityUid Uid { get; set; }
    public bool IsSyndicate { get; set; }
    public string ContrabandSeverity { get; set; } = "";
    public int Depth { get; set; }
}

public sealed class RobustaInventoryViewerSystem : EntitySystem
{
    [Dependency] private readonly IPlayerManager _player = default!;
    [Dependency] private readonly SharedHandsSystem _hands = default!;
    [Dependency] private readonly InventorySystem _inventory = default!;
    [Dependency] private readonly SharedContainerSystem _containers = default!;
    [Dependency] private readonly RobustaSyndicateDetectorSystem _syndi = default!;

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<GetVerbsEvent<Verb>>(OnGetVerbs);
        SubscribeLocalEvent<GetVerbsEvent<ExamineVerb>>(OnGetExamineVerbs);
    }

    private void OnGetVerbs(GetVerbsEvent<Verb> args)
    {
        if (!HasComp<MobStateComponent>(args.Target))
            return;

        var target = args.Target;

        args.Verbs.Add(new Verb
        {
            Text = "View Inventory (Robusto)",
            Category = VerbCategory.Examine,
            Icon = new SpriteSpecifier.Texture(new ResPath("/Textures/Interface/VerbIcons/settings.svg.192dpi.png")),
            Act = () => OpenInventoryWindow(target),
            ClientExclusive = true,
            Priority = 10
        });
    }

    private void OnGetExamineVerbs(GetVerbsEvent<ExamineVerb> args)
    {
        if (!HasComp<MobStateComponent>(args.Target))
            return;

        var target = args.Target;

        args.Verbs.Add(new ExamineVerb
        {
            Text = "View Inventory (Robusto)",
            Category = VerbCategory.Examine,
            Icon = new SpriteSpecifier.Texture(new ResPath("/Textures/Interface/VerbIcons/settings.svg.192dpi.png")),
            Act = () => OpenInventoryWindow(target),
            ClientExclusive = true,
            Priority = 10
        });
    }

    public void OpenForHoverOrTarget(EntityUid? specificTarget = null)
    {
        if (specificTarget.HasValue && EntityManager.EntityExists(specificTarget.Value))
        {
            OpenInventoryWindow(specificTarget.Value);
            return;
        }

        var aim = EntityManager.System<RobustaAimSystem>();
        if (aim.LockedTarget.HasValue && EntityManager.EntityExists(aim.LockedTarget.Value))
        {
            OpenInventoryWindow(aim.LockedTarget.Value);
            return;
        }

        var input = IoCManager.Resolve<IInputManager>();
        var eye = IoCManager.Resolve<IEyeManager>();
        var mousePos = eye.ScreenToMap(input.MouseScreenPosition);

        if (mousePos.MapId != MapId.Nullspace)
        {
            var lookup = EntityManager.System<EntityLookupSystem>();
            var entities = lookup.GetEntitiesInRange(mousePos, 1.0f);

            var mob = entities.FirstOrDefault(e => HasComp<MobStateComponent>(e));
            if (mob != default && EntityManager.EntityExists(mob))
            {
                OpenInventoryWindow(mob);
                return;
            }
        }

        var localPlayer = _player.LocalSession?.AttachedEntity;
        if (localPlayer.HasValue && EntityManager.EntityExists(localPlayer.Value))
        {
            OpenInventoryWindow(localPlayer.Value);
        }
    }

    public void OpenInventoryWindow(EntityUid target)
    {
        var entries = GetFullInventory(target);
        var name = MetaData(target).EntityName;

        RobustaImGuiManager.InventoryViewer.SetData(target, name, entries);
    }

    public List<InventoryEntry> GetFullInventory(EntityUid target)
    {
        var results = new List<InventoryEntry>();
        var scanned = new HashSet<EntityUid>();

        // 1. Hands
        if (TryComp<HandsComponent>(target, out var hands))
        {
            foreach (var handName in hands.Hands.Keys)
            {
                EntityUid? held = null;

                var type = _hands.GetType();
                var method3 = type.GetMethod("GetHeldItem", new[] { typeof(Entity<HandsComponent>), typeof(string), typeof(bool) });
                if (method3 != null)
                    held = (EntityUid?)method3.Invoke(_hands, new object?[] { new Entity<HandsComponent>(target, hands), handName, false });
                else
                {
                    var method2 = type.GetMethod("GetHeldItem", new[] { typeof(Entity<HandsComponent>), typeof(string) });
                    if (method2 != null)
                        held = (EntityUid?)method2.Invoke(_hands, new object?[] { new Entity<HandsComponent>(target, hands), handName });
                }

                if (held != null && held.Value.IsValid())
                {
                    AddItem(held.Value, $"Hand ({handName})", results, scanned, 0);
                }
            }
        }

        // 2. Inventory slots
        if (TryComp<InventoryComponent>(target, out _))
        {
            string[] slots = { "id", "belt", "back", "pocket1", "pocket2", "suitstorage", "outerClothing", "gloves", "shoes", "head", "mask", "eyes", "ears", "neck" };

            foreach (var slot in slots)
            {
                if (_inventory.TryGetSlotEntity(target, slot, out var slotEnt) && slotEnt.HasValue)
                {
                    AddItem(slotEnt.Value, Capitalize(slot), results, scanned, 0);
                }
            }
        }

        return results;
    }

    private void AddItem(EntityUid uid, string containerName, List<InventoryEntry> results, HashSet<EntityUid> scanned, int depth)
    {
        if (!scanned.Add(uid))
            return;

        if (!TryComp<MetaDataComponent>(uid, out var meta))
            return;

        var protoId = meta.EntityPrototype?.ID ?? "";

        // Skip internal engine entities and UI action buttons
        if (protoId == "VirtualItem" ||
            protoId == "ActionDummy" ||
            protoId.StartsWith("Action") ||
            meta.EntityName == "VIRTUAL ITEM YOU SHOULD NOT SEE THIS")
            return;

        // 1. Check ContrabandComponent severity directly on THIS item
        string severity = "";
        if (TryComp<Content.Shared.Contraband.ContrabandComponent>(uid, out var contra))
        {
            severity = contra.Severity.ToString();
        }

        // 2. Check if THIS item is a disguised Uplink
        var status = _syndi.CheckPlayerStatus(uid);

        var protoLower = protoId.ToLowerInvariant();
        bool isEmag = protoLower.StartsWith("emag") || protoLower.Contains("_emag") || protoLower.Contains("emag_") || protoLower == "emag";

        // Exclude specific structures/props from being treated as Syndicate contraband
        bool isExcluded = protoLower.Contains("airlocksyndicateglass") ||
                          protoLower.Contains("poster") ||
                          protoLower.Contains("toy") ||
                          protoLower.Contains("plushie");

        if (isExcluded && severity.Equals("Syndicate", StringComparison.OrdinalIgnoreCase))
        {
            severity = "";
        }

        var isSyndi = false;
        if (!isExcluded && (
            status.Uplink ||
            severity.Equals("Syndicate", StringComparison.OrdinalIgnoreCase) ||
            isEmag ||
            protoLower.Contains("esword") ||
            protoLower.Contains("c4") ||
            protoLower.Contains("syndicate") ||
            protoLower.Contains("stealthbox")))
        {
            isSyndi = true;
            if (string.IsNullOrEmpty(severity))
                severity = "Syndicate";
        }

        results.Add(new InventoryEntry
        {
            ContainerName = containerName,
            ItemName = meta.EntityName,
            PrototypeId = protoId,
            Uid = uid,
            IsSyndicate = isSyndi,
            ContrabandSeverity = severity,
            Depth = depth
        });

        ScanDeep(uid, meta.EntityName, results, scanned, depth + 1);
    }

    private void ScanDeep(EntityUid parent, string parentName, List<InventoryEntry> results, HashSet<EntityUid> scanned, int depth)
    {
        if (depth > 6) return;

        if (TryComp<ContainerManagerComponent>(parent, out var manager))
        {
            foreach (var (cId, container) in manager.Containers)
            {
                if (cId.Equals("actions", StringComparison.OrdinalIgnoreCase))
                    continue;

                var cName = $"{parentName} -> {cId}";

                foreach (var ent in container.ContainedEntities)
                {
                    AddItem(ent, cName, results, scanned, depth + 1);
                }
            }
        }
    }

    private static string Capitalize(string s)
    {
        if (string.IsNullOrEmpty(s)) return s;
        return char.ToUpper(s[0]) + s[1..];
    }
}