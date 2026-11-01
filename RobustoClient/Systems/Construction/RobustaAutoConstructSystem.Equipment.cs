using Content.Shared.Hands;
using Content.Shared.Hands.Components;
using Content.Shared.Interaction;
using Content.Shared.Inventory.Events;
using Content.Shared.Stacks;
using Content.Shared.Storage;
using Robust.Shared.Containers;
using Robust.Shared.Map;
using Robust.Shared.Prototypes;
using RobustoClient.Systems.Abstract;

namespace RobustoClient.Systems.Construction;

public sealed partial class RobustaAutoConstructSystem
{
    private bool PrepareAndEquipForStep(EntityUid player, ConstructionActionStep step)
    {
        switch (step.Kind)
        {
            case ConstructionActionKind.StartGhostConstruction:
            {
                // Equip the first required material/item from the next step so the server accepts TryStartStructureConstructionMessage
                if (_activePlan != null && _stepIndex + 1 < _activePlan.Steps.Count)
                {
                    var firstStep = _activePlan.Steps[_stepIndex + 1];
                    if (firstStep.Kind != ConstructionActionKind.StartGhostConstruction)
                        return PrepareAndEquipForStep(player, firstStep);
                }
                return true;
            }

            case ConstructionActionKind.AddMaterial:
            {
                var matId = step.PrototypeOrQualityOrTag;
                if (string.IsNullOrEmpty(matId))
                    return false;

                // 1. Unified material/stack matcher (handles StackTypeId, prototype Stack, StackPrototype.Spawn, CableCoil/LV/MV/HV cables, etc.)
                if (_itemUtil.TryEquipByPredicate(
                        uid => MatchesMaterialOrStack(uid, matId),
                        $"Material:{matId}",
                        step.Amount))
                {
                    return true;
                }

                // 2. MachinePart / StockPart fallback
                if (_itemUtil.TryEquipByPredicate(
                        uid => MatchesMachinePart(uid, matId),
                        $"MaterialOrPart:{matId}",
                        step.Amount))
                {
                    return true;
                }

                // 3. Deep container (e.g. toolbox/box inside backpack, secondary containers) & interaction-range vicinity fallback
                return TryEquipFromDeepInventoryOrVicinity(
                    player,
                    uid => MatchesMaterialOrStack(uid, matId) || MatchesMachinePart(uid, matId),
                    step.Amount);
            }

            case ConstructionActionKind.UseTool:
            case ConstructionActionKind.CutWires:
            {
                var quality = step.PrototypeOrQualityOrTag ?? "Cutting";
                if (string.IsNullOrEmpty(quality))
                    return false;

                return TryEquipToolOrMultiTool(quality, $"Tool:{quality}");
            }

            case ConstructionActionKind.InsertPrototype:
            {
                var protoId = step.PrototypeOrQualityOrTag;
                if (string.IsNullOrEmpty(protoId))
                    return false;

                if (_itemUtil.TryEquipItem(new EntProtoId(protoId), step.Amount, includeChildren: true))
                    return true;

                if (_itemUtil.TryEquipByPredicate(
                        uid => MatchesPrototypeOrParentId(uid, protoId, allowPrefixMatch: false) || HasValidTag(uid, protoId),
                        $"Prototype:{protoId}",
                        step.Amount))
                {
                    return true;
                }

                return TryEquipFromDeepInventoryOrVicinity(
                    player,
                    uid => MatchesPrototypeOrParentId(uid, protoId, allowPrefixMatch: false) || HasValidTag(uid, protoId),
                    step.Amount);
            }

            case ConstructionActionKind.InsertComponent:
            {
                var compName = step.PrototypeOrQualityOrTag;
                if (string.IsNullOrEmpty(compName))
                    return false;

                if (string.Equals(compName, "MachineBoard", StringComparison.OrdinalIgnoreCase))
                {
                    return _itemUtil.TryEquipByPredicate(
                               uid => _solver.IsMachineBoardEntity(uid),
                               "Component:MachineBoard",
                               step.Amount) ||
                           TryEquipFromDeepInventoryOrVicinity(
                               player,
                               uid => _solver.IsMachineBoardEntity(uid),
                               step.Amount);
                }

                if (compName.StartsWith("MachinePart:", StringComparison.OrdinalIgnoreCase))
                {
                    var partId = compName["MachinePart:".Length..];
                    return _itemUtil.TryEquipByPredicate(
                               uid => MatchesMachinePart(uid, partId),
                               $"MachinePart:{partId}",
                               step.Amount) ||
                           TryEquipFromDeepInventoryOrVicinity(
                               player,
                               uid => MatchesMachinePart(uid, partId),
                               step.Amount);
                }

                bool MatchesComponentRequirement(EntityUid uid)
                {
                    if (!Exists(uid))
                        return false;

                    if (EntityManager.GetComponents(uid).Any(c =>
                            c.GetType().Name.Equals(compName, StringComparison.OrdinalIgnoreCase) ||
                            c.GetType().Name.Equals(compName + "Component", StringComparison.OrdinalIgnoreCase)))
                    {
                        return true;
                    }

                    var proto = MetaData(uid).EntityPrototype;
                    if (proto != null && _itemUtil.HasPrototypeComponent(proto, compName))
                        return true;

                    return MatchesTagOrPrototype(uid, compName);
                }

                return _itemUtil.TryEquipByPredicate(
                           MatchesComponentRequirement,
                           $"Component:{compName}",
                           step.Amount) ||
                       TryEquipFromDeepInventoryOrVicinity(
                           player,
                           MatchesComponentRequirement,
                           step.Amount);
            }

            case ConstructionActionKind.InsertTag:
            {
                var tag = step.PrototypeOrQualityOrTag;
                if (string.IsNullOrEmpty(tag))
                    return false;

                return _itemUtil.TryEquipByPredicate(
                           uid => MatchesTagOrPrototype(uid, tag),
                           $"Tag:{tag}",
                           step.Amount) ||
                       TryEquipFromDeepInventoryOrVicinity(
                           player,
                           uid => MatchesTagOrPrototype(uid, tag),
                           step.Amount);
            }

            default:
                return false;
        }
    }

    private bool TryOpenWornStorageSlots(EntityUid player)
    {
        if (!Exists(player))
            return false;

        bool openedAny = false;
        string[] storageSlots = { "belt", "back", "suitstorage", "outerClothing", "pocket1", "pocket2" };
        foreach (var slotName in storageSlots)
        {
            if (_inventory.TryGetSlotEntity(player, slotName, out var slotEnt) &&
                slotEnt.HasValue &&
                Exists(slotEnt.Value) &&
                HasComp<StorageComponent>(slotEnt.Value))
            {
                RaisePredictiveEvent(new OpenSlotStorageNetworkMessage(slotName));
                openedAny = true;
            }
        }

        return openedAny;
    }

    private bool TryEquipFromDeepInventoryOrVicinity(
        EntityUid player,
        Func<EntityUid, bool> predicate,
        int neededAmount)
    {
        if (!Exists(player) || !TryComp<HandsComponent>(player, out var hands))
            return false;

        int needed = Math.Max(1, neededAmount);

        // 1. Check within interaction range via ItemUtilitySystem's vicinity search
        var vicinityMatches = _itemUtil.FindItemsByPredicate(predicate, needed, SharedInteractionSystem.InteractionRange);
        int vicinityTotal = vicinityMatches.Sum(m => m.StackCount);
        if (vicinityMatches.Count > 0 && vicinityTotal >= needed)
        {
            var best = vicinityMatches[0];
            _itemUtil.StowActiveTrackedItem();
            if (best.Source == ItemSource.Storage && best.StorageEntity is { } storageUid)
            {
                if (!string.IsNullOrEmpty(best.SlotOrHand))
                {
                    var slotName = best.SlotOrHand;
                    _itemUtil.EnqueueStep(ItemUtilitySystem.StepDelay, () => RaisePredictiveEvent(new OpenSlotStorageNetworkMessage(slotName)));
                }
                var itemUid = best.Entity;
                _itemUtil.EnqueueStep(ItemUtilitySystem.StepDelay, () =>
                {
                    if (!Exists(itemUid) || !Exists(storageUid))
                        return;
                    EnsureEmptyActiveHandImmediate(player);
                    RaisePredictiveEvent(new StorageInteractWithItemEvent(GetNetEntity(itemUid), GetNetEntity(storageUid)));
                });
                return true;
            }

            if (best.Source == ItemSource.Vicinity)
            {
                _itemUtil.EnqueueClickEntity(best.Entity);
                return true;
            }
        }

        // 2. Deep scan nested storage containers (e.g. toolbox/box inside backpack, secondary containers on worn items, or open containers nearby)
        var deepCandidates = new List<(EntityUid Item, EntityUid? StorageOwner, string? RootSlot, int Count)>();
        var visited = new HashSet<EntityUid>();

        void ScanContainerRecursive(EntityUid containerOwner, string? rootSlot, EntityUid? activeStorageOwner, int depth)
        {
            if (depth > 3 || !Exists(containerOwner))
                return;

            EntityUid? currentStorage = HasComp<StorageComponent>(containerOwner) ? containerOwner : activeStorageOwner;

            if (TryComp<StorageComponent>(containerOwner, out var stComp))
            {
                foreach (var ent in stComp.Container.ContainedEntities)
                {
                    if (!Exists(ent) || !visited.Add(ent))
                        continue;

                    if (predicate(ent))
                    {
                        int count = TryComp<StackComponent>(ent, out var s) ? s.Count : 1;
                        deepCandidates.Add((ent, currentStorage ?? containerOwner, rootSlot, count));
                    }

                    ScanContainerRecursive(ent, rootSlot, currentStorage ?? containerOwner, depth + 1);
                }
            }

            if (TryComp<ContainerManagerComponent>(containerOwner, out var containerMan))
            {
                foreach (var container in _container.GetAllContainers(containerOwner, containerMan))
                {
                    foreach (var ent in container.ContainedEntities)
                    {
                        if (!Exists(ent) || !visited.Add(ent))
                            continue;

                        if (predicate(ent))
                        {
                            int count = TryComp<StackComponent>(ent, out var s) ? s.Count : 1;
                            deepCandidates.Add((ent, currentStorage ?? containerOwner, rootSlot, count));
                        }

                        ScanContainerRecursive(ent, rootSlot, currentStorage ?? containerOwner, depth + 1);
                    }
                }
            }
        }

        // Scan hands recursively
        foreach (var handName in hands.Hands.Keys)
        {
            if (_container.TryGetContainer(player, handName, out var handCont) && handCont.ContainedEntities.Count > 0)
            {
                var heldEnt = handCont.ContainedEntities[0];
                ScanContainerRecursive(heldEnt, null, HasComp<StorageComponent>(heldEnt) ? heldEnt : null, 1);
            }
        }

        // Scan worn slots recursively
        string[] allSlots =
        {
            "belt", "back", "pocket1", "pocket2", "pocket3", "pocket4",
            "suitstorage", "id", "outerClothing", "gloves", "shoes",
            "head", "eyes", "ears", "mask", "neck", "jumpsuit"
        };
        foreach (var slotName in allSlots)
        {
            if (_inventory.TryGetSlotEntity(player, slotName, out var slotEnt) && slotEnt.HasValue)
            {
                ScanContainerRecursive(slotEnt.Value, slotName, HasComp<StorageComponent>(slotEnt.Value) ? slotEnt.Value : null, 1);
            }
        }

        // Also scan open StorageComponent entities within interaction range (e.g. open toolbox on the floor)
        var playerCoords = _transform.GetMapCoordinates(player);
        if (playerCoords.MapId != MapId.Nullspace)
        {
            foreach (var nearEnt in _lookup.GetEntitiesInRange(playerCoords, SharedInteractionSystem.InteractionRange, LookupFlags.Uncontained))
            {
                if (nearEnt == player || !Exists(nearEnt) || !HasComp<StorageComponent>(nearEnt))
                    continue;

                ScanContainerRecursive(nearEnt, null, nearEnt, 1);
            }
        }

        if (deepCandidates.Count == 0 || deepCandidates.Sum(c => c.Count) < needed)
            return false;

        var chosen = deepCandidates
            .OrderByDescending(c => c.Count >= needed)
            .ThenByDescending(c => c.Count)
            .First();

        _itemUtil.StowActiveTrackedItem();

        if (!string.IsNullOrEmpty(chosen.RootSlot))
        {
            var rootSlot = chosen.RootSlot;
            _itemUtil.EnqueueStep(ItemUtilitySystem.StepDelay, () => RaisePredictiveEvent(new OpenSlotStorageNetworkMessage(rootSlot)));
        }

        if (chosen.StorageOwner is { } storageOwner && Exists(storageOwner))
        {
            var itemToFetch = chosen.Item;
            _itemUtil.EnqueueStep(ItemUtilitySystem.StepDelay, () =>
            {
                if (!Exists(itemToFetch) || !Exists(storageOwner))
                    return;
                EnsureEmptyActiveHandImmediate(player);
                RaisePredictiveEvent(new StorageInteractWithItemEvent(GetNetEntity(itemToFetch), GetNetEntity(storageOwner)));
            });
            return true;
        }

        _itemUtil.EnqueueClickEntity(chosen.Item);
        return true;
    }

    private void EnsureEmptyActiveHandImmediate(EntityUid player)
    {
        if (!TryComp<HandsComponent>(player, out var hands))
            return;

        var activeHand = _hands.GetActiveHand(player);
        if (activeHand != null && _hands.GetActiveItem(player) == null)
            return;

        foreach (var handName in hands.Hands.Keys)
        {
            if (_container.TryGetContainer(player, handName, out var cont) && cont.ContainedEntities.Count == 0)
            {
                RaisePredictiveEvent(new RequestSetHandEvent(handName));
                return;
            }
        }
    }
}
