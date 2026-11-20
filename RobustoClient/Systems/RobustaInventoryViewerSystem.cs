using System;
using System.Collections.Generic;
using Content.Shared.Mobs.Components;
using Content.Shared.Verbs;
using Robust.Shared.Containers;
using Robust.Shared.GameObjects;
using Robust.Shared.Utility;
using RobustoClient.Systems.Abstract;
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

        if (EntityManager.System<MouseUtilitySystem>()
            .TryResolveTarget(out var target, whitelist: [typeof(MobStateComponent)]))
        {
            OpenInventoryWindow(target);
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

        // Every container the mob owns, which is the same set the state layer's carried walk sees.
        // A fixed slot list cannot do this: it misses jumpsuit and any slot a downstream build adds,
        // and those blind spots were reported by the detector but never listed here.
        if (!TryComp<ContainerManagerComponent>(target, out var containers))
            return results;

        foreach (var (containerId, container) in containers.Containers)
        {
            if (!ShouldListContainer(containerId))
                continue;

            var label = Capitalize(containerId);

            foreach (var contained in container.ContainedEntities)
                AddItem(contained, label, results, scanned, 0);
        }

        return results;
    }

    /// <summary>
    /// Whether a container holds possessions rather than UI. The action bar is a set of button
    /// entities, so listing it would fill the window with actions. Pure so it is testable.
    /// </summary>
    internal static bool ShouldListContainer(string containerId) =>
        !containerId.Equals("actions", StringComparison.OrdinalIgnoreCase);

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

        // 1. Contraband severity comes straight off the item.
        string severity = "";
        if (TryComp<Content.Shared.Contraband.ContrabandComponent>(uid, out var contra))
            severity = contra.Severity.ToString();

        // 2. Whether the item counts as syndicate gear is the detector's call, so the viewer and the
        //    player flags cannot drift apart on what "syndicate gear" means.
        bool isSyndi = _syndi.IsSyndicateGear(uid);

        if (isSyndi && string.IsNullOrEmpty(severity))
            severity = "Syndicate";

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