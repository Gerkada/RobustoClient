using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Content.Shared.Hands;
using Content.Shared.Hands.Components;
using Content.Shared.Hands.EntitySystems;
using Content.Shared.Input;
using Content.Shared.Inventory;
using Content.Shared.Inventory.Events;
using Content.Shared.Stacks;
using Content.Shared.Storage;
using Content.Shared.Storage.Components;
using Robust.Client.GameObjects;
using Robust.Client.Graphics;
using Robust.Client.Input;
using Robust.Client.Player;
using Robust.Shared.Containers;
using Robust.Shared.GameObjects;
using Robust.Shared.Input;
using Robust.Shared.IoC;
using Robust.Shared.Log;
using Robust.Shared.Map;
using Robust.Shared.Prototypes;
using Robust.Shared.Timing;

namespace RobustoClient.Systems.Abstract;

public enum ItemSource
{
    Hand,
    Slot,
    Storage,
    Vicinity
}

public sealed class ItemQuery
{
    public string? ProtoId { get; set; }
    public bool IncludeChildren { get; set; }
    public string? StackType { get; set; }
    public int? StackAmount { get; set; }

    public ItemQuery()
    {
    }

    public ItemQuery(string? protoId = null, bool includeChildren = false, string? stackType = null, int? stackAmount = null)
    {
        ProtoId = protoId;
        IncludeChildren = includeChildren;
        StackType = stackType;
        StackAmount = stackAmount;
    }
}

public sealed record ItemLocation(
    EntityUid Entity,
    ItemSource Source,
    string? SlotOrHand = null,
    EntityUid? StorageEntity = null,
    int StackCount = 1,
    float Distance = 0f);

public sealed class ItemUtilitySystem : EntitySystem
{
    [Dependency] private readonly IEntityManager _entMan = default!;
    [Dependency] private readonly IPlayerManager _player = default!;
    [Dependency] private readonly IPrototypeManager _proto = default!;
    [Dependency] private readonly IInputManager _input = default!;
    [Dependency] private readonly IGameTiming _timing = default!;
    [Dependency] private readonly IEyeManager _eye = default!;
    [Dependency] private readonly SharedTransformSystem _transform = default!;
    [Dependency] private readonly EntityLookupSystem _lookup = default!;
    [Dependency] private readonly SharedHandsSystem _hands = default!;
    [Dependency] private readonly InventorySystem _inventory = default!;
    [Dependency] private readonly SharedContainerSystem _container = default!;

    public const float StepDelay = 0.25f;
    public const float DefaultVicinityRange = 2f;

    private static readonly string[] InventorySlotsToScan =
        ["belt", "back", "pocket1", "pocket2", "pocket3", "pocket4", "suitstorage", "id", "outerClothing", "gloves", "shoes", "head", "eyes", "ears", "mask", "neck", "jumpsuit"];

    private readonly Queue<(float Delay, Action Action)> _steps = new();
    private readonly Dictionary<EntityUid, (ItemSource Source, string? SlotOrHand, EntityUid? StorageEntity)> _itemOrigins = new();
    private float _wait;
    private ISawmill _sawmill = default!;

    // --- Cross-version EntityPrototype.Components reflection (avoids ComponentRegistryEntry struct/class TypeLoadException) ---
    private static readonly PropertyInfo? ProtoComponentsProp =
        typeof(EntityPrototype).GetProperty("Components", BindingFlags.Public | BindingFlags.Instance);
    private static PropertyInfo? _entryComponentProp;
    private static FieldInfo? _entryComponentField;
    private static bool _entryReflected;

    // --- Cross-version StackComponent.StackTypeId reflection (handles string vs ProtoId<StackPrototype>) ---
    private static readonly PropertyInfo? StackTypeIdProp =
        typeof(StackComponent).GetProperty("StackTypeId", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
    private static readonly FieldInfo? StackTypeIdField =
        typeof(StackComponent).GetField("StackTypeId", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);

    private static string? GetStackTypeIdString(StackComponent stack)
    {
        var raw = StackTypeIdProp?.GetValue(stack) ?? StackTypeIdField?.GetValue(stack);
        return raw?.ToString();
    }

    // --- Cross-version SharedHandsSystem.GetHeldItem reflection ---
    private MethodInfo? _getHeldItemMethod3;
    private MethodInfo? _getHeldItemMethod2;
    private bool _handsReflected;

    public bool IsBusy => _steps.Count > 0 || _wait > 0f;

    public override void Initialize()
    {
        base.Initialize();
        _sawmill = Logger.GetSawmill("itemutil");
    }

    public override void FrameUpdate(float frameTime)
    {
        base.FrameUpdate(frameTime);

        if (_wait > 0f)
        {
            _wait -= frameTime;
            if (_wait > 0f)
                return;
        }

        while (_steps.Count > 0)
        {
            var (delay, action) = _steps.Dequeue();
            try
            {
                action();
            }
            catch (Exception ex)
            {
                _sawmill.Error($"Step execution failed: {ex}");
            }

            if (delay > 0f)
            {
                _wait = delay;
                break;
            }
        }
    }

    public void CancelAll()
    {
        _steps.Clear();
        _wait = 0f;
    }

    public void EnqueueStep(float delay, Action action)
    {
        _steps.Enqueue((delay, action));
    }

    public void EnqueueClickEntity(EntityUid target, float delay = StepDelay)
    {
        _steps.Enqueue((delay, () =>
        {
            var player = _player.LocalSession?.AttachedEntity;
            if (player.HasValue && Exists(target))
                ClickEntity(player.Value, target);
        }));
    }

    public void EnqueueClickCoordinates(EntityCoordinates coordinates, float delay = StepDelay)
    {
        _steps.Enqueue((delay, () =>
        {
            var player = _player.LocalSession?.AttachedEntity;
            if (player.HasValue && coordinates.IsValid(_entMan))
                ClickCoordinates(coordinates);
        }));
    }

    // --- Cross-version Prototype Component Inspection ---

    private static IDictionary? GetProtoComponentsDict(EntityPrototype proto)
        => ProtoComponentsProp?.GetValue(proto) as IDictionary;

    public bool HasPrototypeComponent(EntityPrototype proto, string compName)
    {
        var dict = GetProtoComponentsDict(proto);
        return dict != null && dict.Contains(compName);
    }

    public bool TryGetPrototypeComponent<T>(EntityPrototype proto, string compName, out T? component) where T : class
    {
        component = null;
        var dict = GetProtoComponentsDict(proto);
        if (dict == null || !dict.Contains(compName))
            return false;

        var entry = dict[compName];
        if (entry == null)
            return false;

        if (!_entryReflected)
        {
            var entryType = entry.GetType();
            const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
            _entryComponentProp = entryType.GetProperty("Component", flags);
            _entryComponentField = entryType.GetField("Component", flags);
            _entryReflected = true;
        }

        var compObj = _entryComponentProp?.GetValue(entry) ?? _entryComponentField?.GetValue(entry);
        component = compObj as T;
        return component != null;
    }

    // --- Prototype Hierarchy ---

    public HashSet<string> GetDescendantIds(string parentProtoId)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { parentProtoId };
        bool added = true;
        while (added)
        {
            added = false;
            foreach (var proto in _proto.EnumeratePrototypes<EntityPrototype>())
            {
                if (result.Contains(proto.ID))
                    continue;
                if (proto.Parents != null && proto.Parents.Any(p => result.Contains(p)))
                {
                    result.Add(proto.ID);
                    added = true;
                }
            }
        }
        return result;
    }

    // --- Query / Predicate Matching ---

    private Func<EntityUid, bool> BuildMatcher(ItemQuery query)
    {
        HashSet<string>? allowedProtos = null;
        if (!string.IsNullOrEmpty(query.ProtoId))
        {
            allowedProtos = query.IncludeChildren
                ? GetDescendantIds(query.ProtoId)
                : new HashSet<string>(StringComparer.OrdinalIgnoreCase) { query.ProtoId };
        }

        string? requiredStackType = query.StackType;
        if (string.IsNullOrEmpty(requiredStackType) && !string.IsNullOrEmpty(query.ProtoId) && query.StackAmount.HasValue)
        {
            if (TryResolveMax(query.ProtoId, out var resolvedStackType, out _))
                requiredStackType = resolvedStackType;
        }

        return uid =>
        {
            if (!Exists(uid))
                return false;

            if (!string.IsNullOrEmpty(requiredStackType))
            {
                if (!TryComp<StackComponent>(uid, out var stack))
                    return false;
                if (!string.Equals(GetStackTypeIdString(stack), requiredStackType, StringComparison.OrdinalIgnoreCase))
                    return false;
                if (allowedProtos != null && string.IsNullOrEmpty(query.StackType))
                    return true;
            }

            if (allowedProtos != null)
            {
                var protoId = MetaData(uid).EntityPrototype?.ID;
                if (protoId == null || !allowedProtos.Contains(protoId))
                    return false;
            }

            return true;
        };
    }

    private bool TryResolveMax(string protoId, out string stackTypeId, out int maxCount)
    {
        stackTypeId = string.Empty;
        maxCount = 1;

        if (!_proto.TryIndex<EntityPrototype>(protoId, out var proto))
            return false;

        if (!TryGetPrototypeComponent<StackComponent>(proto, "Stack", out var stackComp) || stackComp == null)
            return false;

        var resolvedId = GetStackTypeIdString(stackComp);
        if (string.IsNullOrEmpty(resolvedId))
            return false;

        stackTypeId = resolvedId;
        if (_proto.TryIndex<StackPrototype>(stackTypeId, out var stackProto))
            maxCount = stackProto.MaxCount ?? 30;
        else
            maxCount = 30;

        return true;
    }

    private int GetStackCount(EntityUid uid)
        => TryComp<StackComponent>(uid, out var stack) ? stack.Count : 1;

    private EntityUid? GetHeldInHand(EntityUid player, HandsComponent hands, string handName)
    {
        if (!_handsReflected)
        {
            var handsType = _hands.GetType();
            _getHeldItemMethod3 = handsType.GetMethod("GetHeldItem", [typeof(Entity<HandsComponent>), typeof(string), typeof(bool)]);
            _getHeldItemMethod2 = handsType.GetMethod("GetHeldItem", [typeof(Entity<HandsComponent>), typeof(string)]);
            _handsReflected = true;
        }

        if (_getHeldItemMethod3 != null)
            return (EntityUid?)_getHeldItemMethod3.Invoke(_hands, [new Entity<HandsComponent>(player, hands), handName, false]);
        if (_getHeldItemMethod2 != null)
            return (EntityUid?)_getHeldItemMethod2.Invoke(_hands, [new Entity<HandsComponent>(player, hands), handName]);

        if (_container.TryGetContainer(player, handName, out var container) && container.ContainedEntities.Count > 0)
            return container.ContainedEntities[0];

        return null;
    }

    // --- Public Search API ---

    public List<ItemLocation> FindItems(ItemQuery query)
    {
        var results = new List<ItemLocation>();
        results.AddRange(FindItemsInventory(query));
        results.AddRange(FindItemsVicinity(query));
        return results;
    }

    public ItemLocation? FindItem(ItemQuery query)
        => FindItems(query).FirstOrDefault();

    public List<ItemLocation> FindItemsInventory(ItemQuery query)
    {
        var matcher = BuildMatcher(query);
        return FindItemsInventoryCore(matcher);
    }

    public ItemLocation? FindItemInventory(ItemQuery query)
        => FindItemsInventory(query).FirstOrDefault();

    public List<ItemLocation> FindItemsVicinity(ItemQuery query, float range = DefaultVicinityRange)
    {
        var matcher = BuildMatcher(query);
        return FindItemsVicinityCore(matcher, range);
    }

    public ItemLocation? FindItemVicinity(ItemQuery query, float range = DefaultVicinityRange)
        => FindItemsVicinity(query, range).FirstOrDefault();

    public List<ItemLocation> FindItemsByPredicate(Func<EntityUid, bool> predicate, int? stackAmount = null, float vicinityRange = DefaultVicinityRange)
    {
        var results = new List<ItemLocation>();
        results.AddRange(FindItemsInventoryCore(predicate));
        results.AddRange(FindItemsVicinityCore(predicate, vicinityRange));
        if (stackAmount.HasValue)
        {
            return results
                .OrderByDescending(r => r.StackCount >= stackAmount.Value)
                .ThenBy(r => r.Source)
                .ThenByDescending(r => r.StackCount)
                .ThenBy(r => r.Distance)
                .ToList();
        }
        return results;
    }

    public ItemLocation? FindItemByPredicate(Func<EntityUid, bool> predicate, int? stackAmount = null, float vicinityRange = DefaultVicinityRange)
        => FindItemsByPredicate(predicate, stackAmount, vicinityRange).FirstOrDefault();

    private List<ItemLocation> FindItemsInventoryCore(Func<EntityUid, bool> matcher)
    {
        var results = new List<ItemLocation>();
        var player = _player.LocalSession?.AttachedEntity;
        if (player == null)
            return results;

        var seen = new HashSet<EntityUid>();

        // 1. Hands (active hand first)
        if (TryComp<HandsComponent>(player.Value, out var hands))
        {
            var activeHandId = _hands.GetActiveHand(player.Value);
            var orderedHands = hands.Hands.Keys
                .OrderByDescending(h => h == activeHandId)
                .ToList();

            foreach (var handName in orderedHands)
            {
                var held = GetHeldInHand(player.Value, hands, handName);
                if (held.HasValue && seen.Add(held.Value))
                {
                    if (matcher(held.Value))
                        results.Add(new ItemLocation(held.Value, ItemSource.Hand, handName, null, GetStackCount(held.Value)));

                    // Also scan inside held storage containers (e.g. toolbox/backpack in hand)
                    if (TryComp<StorageComponent>(held.Value, out var heldStorage))
                    {
                        foreach (var stored in heldStorage.Container.ContainedEntities)
                        {
                            if (seen.Add(stored) && matcher(stored))
                                results.Add(new ItemLocation(stored, ItemSource.Storage, handName, held.Value, GetStackCount(stored)));
                        }
                    }
                }
            }
        }

        // 2. Direct inventory slots & storage inside slots
        foreach (var slotName in InventorySlotsToScan)
        {
            if (!_inventory.TryGetSlotEntity(player.Value, slotName, out var slotEnt) || !slotEnt.HasValue)
                continue;

            if (seen.Add(slotEnt.Value) && matcher(slotEnt.Value))
                results.Add(new ItemLocation(slotEnt.Value, ItemSource.Slot, slotName, null, GetStackCount(slotEnt.Value)));

            if (TryComp<StorageComponent>(slotEnt.Value, out var storage))
            {
                foreach (var stored in storage.Container.ContainedEntities)
                {
                    if (seen.Add(stored) && matcher(stored))
                        results.Add(new ItemLocation(stored, ItemSource.Storage, slotName, slotEnt.Value, GetStackCount(stored)));
                }
            }
        }

        return results;
    }

    private List<ItemLocation> FindItemsVicinityCore(Func<EntityUid, bool> matcher, float range)
    {
        var results = new List<ItemLocation>();
        var player = _player.LocalSession?.AttachedEntity;
        if (player == null)
            return results;

        var origin = _transform.GetMapCoordinates(player.Value);
        if (origin.MapId == MapId.Nullspace)
            return results;

        var entities = _lookup.GetEntitiesInRange(origin, range, LookupFlags.Uncontained);
        var sorted = entities
            .Where(e => e != player.Value && matcher(e))
            .OrderBy(e =>
            {
                var eCoords = _transform.GetMapCoordinates(e);
                if (eCoords.MapId != origin.MapId)
                    return float.MaxValue;
                return (eCoords.Position - origin.Position).LengthSquared();
            });

        foreach (var ent in sorted)
        {
            var eCoords = _transform.GetMapCoordinates(ent);
            if (eCoords.MapId != origin.MapId)
                continue;
            var dist = (eCoords.Position - origin.Position).Length();
            results.Add(new ItemLocation(ent, ItemSource.Vicinity, null, null, GetStackCount(ent), dist));
        }

        return results;
    }

    // --- Equip / Stack Merging Workflow ---

    public bool TryEquipItem(ItemQuery query)
    {
        var matcher = BuildMatcher(query);
        return TryEquipInternal(matcher, query.ProtoId ?? query.StackType ?? "item", query.StackAmount);
    }

    public bool TryEquipItem(string protoOrStackId, int? stackAmount = null, bool includeChildren = false)
        => TryEquipItem(new ItemQuery(protoId: protoOrStackId, includeChildren: includeChildren, stackType: protoOrStackId, stackAmount: stackAmount));

    public bool TryEquipItem(EntProtoId protoId, int? stackAmount = null, bool includeChildren = false)
        => TryEquipItem(new ItemQuery(protoId: protoId.Id, includeChildren: includeChildren, stackAmount: stackAmount));

    public bool TryEquipItem(ProtoId<StackPrototype> stackType, int? stackAmount = null)
        => TryEquipItem(new ItemQuery(stackType: stackType.Id, stackAmount: stackAmount));

    public bool TryEquipByPredicate(Func<EntityUid, bool> predicate, string description = "custom", int? stackAmount = null)
        => TryEquipInternal(predicate, description, stackAmount);

    private bool TryEquipInternal(Func<EntityUid, bool> matcher, string description, int? requestedAmount)
    {
        var player = _player.LocalSession?.AttachedEntity;
        if (player == null || !TryComp<HandsComponent>(player.Value, out var hands))
            return false;

        var candidates = new List<ItemLocation>();
        candidates.AddRange(FindItemsInventoryCore(matcher));
        candidates.AddRange(FindItemsVicinityCore(matcher, DefaultVicinityRange));

        if (candidates.Count == 0)
        {
            _sawmill.Warning($"[ItemUtil] Could not find '{description}'.");
            return false;
        }

        int needed = requestedAmount ?? 1;
        int totalAvailable = candidates.Sum(c => c.StackCount);
        if (totalAvailable < needed)
        {
            _sawmill.Warning($"[ItemUtil] Insufficient '{description}': need {needed}, found {totalAvailable}.");
            return false;
        }

        // Prefer a single stack/item that already satisfies `needed` (prefer belt over backpack when both satisfy)
        var singleMatch = candidates
            .Where(c => c.StackCount >= needed)
            .OrderBy(c => c.Source)
            .ThenBy(c => c.SlotOrHand == "belt" ? 0 : 1)
            .ThenByDescending(c => c.StackCount)
            .ThenBy(c => c.Distance)
            .FirstOrDefault();

        if (singleMatch != null)
        {
            // If already in a hand, ensure active hand is set to that hand
            if (singleMatch.Source == ItemSource.Hand && singleMatch.SlotOrHand != null)
            {
                var currentActive = _hands.GetActiveHand(player.Value);
                if (currentActive != singleMatch.SlotOrHand)
                {
                    var targetHand = singleMatch.SlotOrHand;
                    _steps.Enqueue((StepDelay, () => RaisePredictiveEvent(new RequestSetHandEvent(targetHand))));
                }
                return true;
            }

            if (!EnsureEmptyOrTargetHand(player.Value, hands, singleMatch))
                return false;

            EnqueueFetchLocation(player.Value, singleMatch);
            return true;
        }

        // Multi-stack merge: gather multiple smaller stacks up to `needed`
        int accumulated = 0;
        ItemLocation? baseStack = candidates.FirstOrDefault(c => c.Source == ItemSource.Hand);

        if (baseStack != null && baseStack.SlotOrHand != null)
        {
            var currentActive = _hands.GetActiveHand(player.Value);
            if (currentActive != baseStack.SlotOrHand)
            {
                var handToSelect = baseStack.SlotOrHand;
                _steps.Enqueue((StepDelay, () => RaisePredictiveEvent(new RequestSetHandEvent(handToSelect))));
            }
            accumulated += baseStack.StackCount;
        }
        else
        {
            baseStack = candidates[0];
            if (!EnsureEmptyOrTargetHand(player.Value, hands, baseStack))
                return false;

            EnqueueFetchLocation(player.Value, baseStack);
            accumulated += baseStack.StackCount;
        }

        foreach (var extra in candidates)
        {
            if (accumulated >= needed)
                break;
            if (extra.Entity == baseStack.Entity)
                continue;

            EnqueueMergeFromLocation(player.Value, extra);
            accumulated += extra.StackCount;
        }

        return true;
    }

    public void StowActiveTrackedItem()
    {
        var player = _player.LocalSession?.AttachedEntity;
        if (player == null || !TryComp<HandsComponent>(player.Value, out var hands))
            return;

        var activeHand = _hands.GetActiveHand(player.Value);
        if (activeHand == null)
            return;

        var held = GetHeldInHand(player.Value, hands, activeHand);
        if (held.HasValue && (_itemOrigins.ContainsKey(held.Value) || IsToolOrWelder(held.Value)))
        {
            EnqueueStowHeldItem(player.Value, held.Value, allowHandSwitchFallback: false);
        }
    }

    private bool EnsureEmptyOrTargetHand(EntityUid player, HandsComponent hands, ItemLocation? incomingTarget = null)
    {
        var activeHand = _hands.GetActiveHand(player);
        if (activeHand == null)
            return false;

        var heldInActive = GetHeldInHand(player, hands, activeHand);
        if (heldInActive == null)
            return true;

        // If active hand holds a tool or an item we previously fetched (and not the container we're pulling from),
        // stow it back before taking the next item so we never fill up both hands with tools.
        if (heldInActive.Value != incomingTarget?.StorageEntity &&
            (_itemOrigins.ContainsKey(heldInActive.Value) || IsToolOrWelder(heldInActive.Value)))
        {
            EnqueueStowHeldItem(player, heldInActive.Value, allowHandSwitchFallback: true);
            return true;
        }

        foreach (var handName in hands.Hands.Keys)
        {
            if (GetHeldInHand(player, hands, handName) == null)
            {
                var targetHand = handName;
                _steps.Enqueue((StepDelay, () => RaisePredictiveEvent(new RequestSetHandEvent(targetHand))));
                return true;
            }
        }

        EnqueueStowHeldItem(player, heldInActive.Value, allowHandSwitchFallback: true);
        return true;
    }

    private void EnqueueStowHeldItem(EntityUid player, EntityUid heldUid, bool allowHandSwitchFallback)
    {
        // 1. Extinguish lit welder first so its ItemSize shrinks back to Small before storage insertion
        _steps.Enqueue((0f, () =>
        {
            if (Exists(heldUid) && IsWelderLit(heldUid))
            {
                RaisePredictiveEvent(new RequestUseInHandEvent());
                _wait = StepDelay;
            }
        }));

        // 2. Determine target storage entity: return to origin storage if recorded; otherwise prefer "belt" first, then "back".
        // IMPORTANT: We stow via ClickEntity(player, storageEntity) instead of UseSlotNetworkMessage(slot),
        // because UseSlotNetworkMessage unequips the belt/backpack if the active hand is empty on the server!
        EntityUid? primaryStorage = null;
        string? primarySlot = null;

        if (_itemOrigins.TryGetValue(heldUid, out var origin))
        {
            if (!string.IsNullOrEmpty(origin.SlotOrHand) &&
                _inventory.TryGetSlotEntity(player, origin.SlotOrHand, out var origSlotEnt) &&
                origSlotEnt.HasValue &&
                HasComp<StorageComponent>(origSlotEnt.Value))
            {
                primarySlot = origin.SlotOrHand;
                primaryStorage = origSlotEnt.Value;
            }
            else if (origin.StorageEntity is { } sEnt && Exists(sEnt) && HasComp<StorageComponent>(sEnt))
            {
                primaryStorage = sEnt;
            }
            else if (origin.Source == ItemSource.Slot &&
                     !string.IsNullOrEmpty(origin.SlotOrHand) &&
                     _inventory.TryGetSlotEntity(player, origin.SlotOrHand, out var curSlotEnt) &&
                     curSlotEnt == null)
            {
                // Pocket or single-item slot that is currently empty
                primarySlot = origin.SlotOrHand;
            }
        }

        if (primaryStorage == null && primarySlot == null)
        {
            if (_inventory.TryGetSlotEntity(player, "belt", out var beltEnt) && beltEnt.HasValue && HasComp<StorageComponent>(beltEnt.Value))
            {
                primarySlot = "belt";
                primaryStorage = beltEnt.Value;
            }
            else if (_inventory.TryGetSlotEntity(player, "back", out var backEnt) && backEnt.HasValue && HasComp<StorageComponent>(backEnt.Value))
            {
                primarySlot = "back";
                primaryStorage = backEnt.Value;
            }
        }

        if (primaryStorage is { } storageTarget)
        {
            _steps.Enqueue((StepDelay, () =>
            {
                if (!Exists(player) || !Exists(storageTarget) || !TryComp<HandsComponent>(player, out var h))
                    return;

                var curActive = _hands.GetActiveHand(player);
                if (curActive != null && GetHeldInHand(player, h, curActive) != null)
                    ClickEntity(player, storageTarget);
            }));
        }
        else if (primarySlot != null)
        {
            var slotToUse = primarySlot;
            _steps.Enqueue((StepDelay, () =>
            {
                if (!Exists(player) || !TryComp<HandsComponent>(player, out var h))
                    return;

                var curActive = _hands.GetActiveHand(player);
                if (curActive != null &&
                    GetHeldInHand(player, h, curActive) != null &&
                    _inventory.TryGetSlotEntity(player, slotToUse, out var slotEnt) &&
                    slotEnt == null)
                {
                    RaisePredictiveEvent(new UseSlotNetworkMessage(slotToUse));
                }
            }));
        }

        // 3. Zero-delay verification: if active hand is STILL holding an item (e.g. belt rejected a material stack or was full),
        // fall back cleanly using ClickEntity on the alternate storage container so the belt/backpack is NEVER unequipped.
        _steps.Enqueue((0f, () =>
        {
            if (!Exists(player) || !TryComp<HandsComponent>(player, out var h))
                return;

            var curActive = _hands.GetActiveHand(player);
            var stillHeld = curActive != null ? GetHeldInHand(player, h, curActive) : null;
            if (stillHeld == null)
                return;

            if (IsWelderLit(stillHeld.Value))
            {
                RaisePredictiveEvent(new RequestUseInHandEvent());
                _wait = StepDelay;
                return;
            }

            // Try alternate storage container if primaryStorage couldn't accept the item (e.g. Steel stack in hand when belt only accepts tools)
            var fallbackSlot = primarySlot == "belt" ? "back" : "belt";
            if (_inventory.TryGetSlotEntity(player, fallbackSlot, out var fbEnt) &&
                fbEnt.HasValue &&
                HasComp<StorageComponent>(fbEnt.Value))
            {
                ClickEntity(player, fbEnt.Value);
                _wait = StepDelay;
                return;
            }

            if (allowHandSwitchFallback)
            {
                foreach (var handName in h.Hands.Keys)
                {
                    if (GetHeldInHand(player, h, handName) == null)
                    {
                        RaisePredictiveEvent(new RequestSetHandEvent(handName));
                        _wait = StepDelay;
                        return;
                    }
                }
            }
        }));

        // 4. Second check in case Step 3 extinguished a lagging welder or fallback storage also couldn't fit it
        _steps.Enqueue((0f, () =>
        {
            if (!Exists(player) || !TryComp<HandsComponent>(player, out var h))
                return;

            var curActive = _hands.GetActiveHand(player);
            var stillHeld = curActive != null ? GetHeldInHand(player, h, curActive) : null;
            if (stillHeld == null)
                return;

            if (primaryStorage is { } pStorage && Exists(pStorage))
            {
                ClickEntity(player, pStorage);
                _wait = StepDelay;
                return;
            }

            if (allowHandSwitchFallback)
            {
                foreach (var handName in h.Hands.Keys)
                {
                    if (GetHeldInHand(player, h, handName) == null)
                    {
                        RaisePredictiveEvent(new RequestSetHandEvent(handName));
                        _wait = StepDelay;
                        return;
                    }
                }
            }
        }));
    }

    public bool IsToolOrWelder(EntityUid uid)
    {
        if (!Exists(uid))
            return false;

        foreach (var comp in _entMan.GetComponents(uid))
        {
            var name = comp.GetType().Name;
            if (name.Equals("ToolComponent", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("WelderComponent", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }

    public bool IsWelderLit(EntityUid uid)
    {
        if (!Exists(uid))
            return false;

        bool isWelder = false;
        bool isActivated = false;

        foreach (var comp in _entMan.GetComponents(uid))
        {
            var name = comp.GetType().Name;
            if (name.Equals("WelderComponent", StringComparison.OrdinalIgnoreCase))
            {
                isWelder = true;
                var enabledProp = comp.GetType().GetProperty("Enabled", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                var enabledField = comp.GetType().GetField("Enabled", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if ((enabledProp?.GetValue(comp) ?? enabledField?.GetValue(comp)) is true)
                    isActivated = true;
            }
            else if (name.Equals("ItemToggleComponent", StringComparison.OrdinalIgnoreCase))
            {
                var actProp = comp.GetType().GetProperty("Activated", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                var actField = comp.GetType().GetField("Activated", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if ((actProp?.GetValue(comp) ?? actField?.GetValue(comp)) is true)
                    isActivated = true;
            }
        }

        return isWelder && isActivated;
    }

    private void EnqueueFetchLocation(EntityUid player, ItemLocation loc)
    {
        if (loc.Source is ItemSource.Slot or ItemSource.Storage)
            _itemOrigins[loc.Entity] = (loc.Source, loc.SlotOrHand, loc.StorageEntity);

        switch (loc.Source)
        {
            case ItemSource.Hand:
                if (loc.SlotOrHand != null)
                {
                    var handName = loc.SlotOrHand;
                    _steps.Enqueue((StepDelay, () => RaisePredictiveEvent(new RequestSetHandEvent(handName))));
                }
                break;

            case ItemSource.Slot:
                if (loc.SlotOrHand != null)
                {
                    var slotName = loc.SlotOrHand;
                    var expectedUid = loc.Entity;
                    _steps.Enqueue((StepDelay, () =>
                    {
                        if (!Exists(player) || !Exists(expectedUid) || !TryComp<HandsComponent>(player, out var h))
                            return;

                        var curActive = _hands.GetActiveHand(player);
                        if (curActive == null || GetHeldInHand(player, h, curActive) != null)
                            return;

                        if (_inventory.TryGetSlotEntity(player, slotName, out var slotEnt) && slotEnt == expectedUid)
                            RaisePredictiveEvent(new UseSlotNetworkMessage(slotName));
                    }));
                }
                break;

            case ItemSource.Storage:
                if (loc.StorageEntity.HasValue)
                {
                    if (loc.SlotOrHand != null && _inventory.TryGetSlotEntity(player, loc.SlotOrHand, out var slotEnt) && slotEnt == loc.StorageEntity)
                    {
                        var slotName = loc.SlotOrHand;
                        _steps.Enqueue((StepDelay, () => RaisePredictiveEvent(new OpenSlotStorageNetworkMessage(slotName))));
                    }
                    var itemUid = loc.Entity;
                    var storageUid = loc.StorageEntity.Value;
                    _steps.Enqueue((StepDelay, () =>
                    {
                        if (!Exists(itemUid) || !Exists(storageUid))
                            return;

                        // Final guard: if active hand is still not empty, switch to an empty hand so StorageInteractWithItemEvent picks up instead of interacting
                        if (TryComp<HandsComponent>(player, out var h))
                        {
                            var curActive = _hands.GetActiveHand(player);
                            if (curActive != null && GetHeldInHand(player, h, curActive) != null)
                            {
                                foreach (var handName in h.Hands.Keys)
                                {
                                    if (GetHeldInHand(player, h, handName) == null)
                                    {
                                        RaisePredictiveEvent(new RequestSetHandEvent(handName));
                                        break;
                                    }
                                }
                            }
                        }

                        var itemNet = _entMan.GetNetEntity(itemUid);
                        var storageNet = _entMan.GetNetEntity(storageUid);
                        RaisePredictiveEvent(new StorageInteractWithItemEvent(itemNet, storageNet));
                    }));
                }
                break;

            case ItemSource.Vicinity:
                {
                    var itemUid = loc.Entity;
                    _steps.Enqueue((StepDelay, () =>
                    {
                        if (Exists(itemUid))
                            ClickEntity(player, itemUid);
                    }));
                }
                break;
        }
    }

    private void EnqueueMergeFromLocation(EntityUid player, ItemLocation extra)
    {
        switch (extra.Source)
        {
            case ItemSource.Vicinity:
                {
                    var itemUid = extra.Entity;
                    _steps.Enqueue((StepDelay, () =>
                    {
                        if (Exists(itemUid))
                            ClickEntity(player, itemUid);
                    }));
                }
                break;

            case ItemSource.Storage:
                if (extra.StorageEntity.HasValue)
                {
                    if (extra.SlotOrHand != null && _inventory.TryGetSlotEntity(player, extra.SlotOrHand, out var slotEnt) && slotEnt == extra.StorageEntity)
                    {
                        var slotName = extra.SlotOrHand;
                        _steps.Enqueue((StepDelay, () => RaisePredictiveEvent(new OpenSlotStorageNetworkMessage(slotName))));
                    }
                    var itemUid = extra.Entity;
                    var storageUid = extra.StorageEntity.Value;
                    _steps.Enqueue((StepDelay, () =>
                    {
                        if (!Exists(itemUid) || !Exists(storageUid))
                            return;
                        var itemNet = _entMan.GetNetEntity(itemUid);
                        var storageNet = _entMan.GetNetEntity(storageUid);
                        RaisePredictiveEvent(new StorageInteractWithItemEvent(itemNet, storageNet));
                    }));
                }
                break;

            case ItemSource.Hand:
                if (extra.SlotOrHand != null)
                {
                    var otherHand = extra.SlotOrHand;
                    _steps.Enqueue((StepDelay, () => RaisePredictiveEvent(new RequestHandInteractUsingEvent(otherHand))));
                }
                break;
        }
    }

    // --- Low-level Click Helpers ---

    public void ClickEntity(EntityUid player, EntityUid target)
    {
        if (!Exists(target) || _player.LocalSession == null)
            return;

        var coords = Transform(target).Coordinates;
        var screenCoords = _eye.CoordinatesToScreen(coords);
        var keyFunctionId = _input.NetworkBindMap.KeyFunctionID(EngineKeyFunctions.Use);

        var downMsg = new ClientFullInputCmdMessage(_timing.CurTick, _timing.TickFraction, keyFunctionId)
        {
            State = BoundKeyState.Down,
            Coordinates = coords,
            ScreenCoordinates = screenCoords,
            Uid = target
        };
        var upMsg = new ClientFullInputCmdMessage(_timing.CurTick, _timing.TickFraction, keyFunctionId)
        {
            State = BoundKeyState.Up,
            Coordinates = coords,
            ScreenCoordinates = screenCoords,
            Uid = target
        };

        var inputSys = _entMan.System<InputSystem>();
        inputSys.HandleInputCommand(_player.LocalSession, EngineKeyFunctions.Use, downMsg);
        inputSys.HandleInputCommand(_player.LocalSession, EngineKeyFunctions.Use, upMsg);
    }

    public void ClickCoordinates(EntityCoordinates coords)
    {
        if (!coords.IsValid(_entMan) || _player.LocalSession == null)
            return;

        var screenCoords = _eye.CoordinatesToScreen(coords);
        var keyFunctionId = _input.NetworkBindMap.KeyFunctionID(EngineKeyFunctions.Use);

        var downMsg = new ClientFullInputCmdMessage(_timing.CurTick, _timing.TickFraction, keyFunctionId)
        {
            State = BoundKeyState.Down,
            Coordinates = coords,
            ScreenCoordinates = screenCoords,
            Uid = EntityUid.Invalid
        };
        var upMsg = new ClientFullInputCmdMessage(_timing.CurTick, _timing.TickFraction, keyFunctionId)
        {
            State = BoundKeyState.Up,
            Coordinates = coords,
            ScreenCoordinates = screenCoords,
            Uid = EntityUid.Invalid
        };

        var inputSys = _entMan.System<InputSystem>();
        inputSys.HandleInputCommand(_player.LocalSession, EngineKeyFunctions.Use, downMsg);
        inputSys.HandleInputCommand(_player.LocalSession, EngineKeyFunctions.Use, upMsg);
    }
}