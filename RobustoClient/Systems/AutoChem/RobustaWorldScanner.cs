using System;
using System.Collections.Generic;
using System.Linq;
using System.Collections;
using Robust.Client.Player;
using Robust.Shared.GameObjects;
using Robust.Shared.IoC;
using Robust.Shared.Map;
using Robust.Shared.Log;
using Content.Shared.Chemistry;
using Content.Shared.Chemistry.Components;
using Content.Shared.Chemistry.EntitySystems;
using Content.Shared.Hands.EntitySystems;
using Content.Shared.Interaction;
using Content.Shared.Storage;
using Robust.Shared.Containers;
using RobustoClient.Reflection;
using RobustoClient.Systems.Abstract;

namespace RobustoClient.Systems.AutoChem;

public sealed class RobustaWorldScanner : EntitySystem
{
    [Dependency] private readonly IEntityManager _entMan = default!;
    [Dependency] private readonly IPlayerManager _player = default!;
    [Dependency] private readonly SharedTransformSystem _transform = default!;
    [Dependency] private readonly EntityLookupSystem _lookup = default!;
    [Dependency] private readonly SharedHandsSystem _hands = default!;
    [Dependency] private readonly SharedContainerSystem _containerSystem = default!;
    [Dependency] private readonly ItemUtilitySystem _itemUtil = default!;

    /// <summary>Numeric view of a reflected value, or 0 when it is not a number.</summary>
    private static float Float(object? value) =>
        ReflectConvert.TryToDouble(value, out var number) ? (float)number : 0f;

    public bool IsBeaker(EntityUid uid)
    {
        if (!uid.IsValid()) return false;
        var meta = MetaData(uid);
        var proto = (meta.EntityPrototype?.ID ?? "").ToLower();
        var name = meta.EntityName.ToLower();

        return proto.Contains("beaker") || proto.Contains("jug") || proto.Contains("vial") || 
               name.Contains("beaker") || name.Contains("glass") || name.Contains("flask") || name.Contains("vial");
    }

    public List<EntityUid> FindMachines(string keyword)
    {
        var localPlayer = _player.LocalSession?.AttachedEntity;
        if (localPlayer == null) return [];

        var playerPos = _transform.GetMapCoordinates(localPlayer.Value);
        var entities = _lookup.GetEntitiesInRange(playerPos, SharedInteractionSystem.InteractionRange, LookupFlags.Uncontained);

        var kwLower = keyword.ToLower();
        var keywords = new List<string> { kwLower };
        if (kwLower == "heater")
        {
            keywords.Add("hotplate");
            keywords.Add("oven");
            keywords.Add("stove");
        }

        var validMachines = new List<EntityUid>();
        foreach (var ent in entities)
        {
            var meta = MetaData(ent);
            var proto = (meta.EntityPrototype?.ID ?? "").ToLower();
            var name = meta.EntityName.ToLower();

            if (keywords.Any(k => proto.Contains(k) || name.Contains(k)))
                validMachines.Add(ent);
        }

        return OrderByDistance(validMachines, playerPos);
    }

    /// <summary>Nearest first, with entities on another map last.</summary>
    private List<EntityUid> OrderByDistance(IEnumerable<EntityUid> entities, MapCoordinates origin) =>
        [.. entities.OrderBy(uid => {
            var pos = _transform.GetMapCoordinates(uid);
            if (pos.MapId != origin.MapId) return float.MaxValue;
            return (pos.Position - origin.Position).LengthSquared();
        })];

    public EntityUid? GetBeakerInHand()
    {
        var localPlayer = _player.LocalSession?.AttachedEntity;
        if (localPlayer == null) return null;

        foreach (var heldItem in _itemUtil.GetHeldItems(localPlayer.Value))
        {
            if (IsBeaker(heldItem)) return heldItem;
        }

        var loc = _itemUtil.FindItemByPredicate(IsBeaker);
        if (loc != null && loc.Source == ItemSource.Hand)
            return loc.Entity;

        return null;
    }

    public bool IsBeakerInsideMachine(EntityUid machine, EntityUid beaker)
    {
        if (_entMan.TryGetComponent<ContainerManagerComponent>(machine, out var containerManager))
        {
            foreach (var container in containerManager.Containers.Values)
            {
                if (container.ContainedEntities.Contains(beaker)) return true;
            }
        }

        foreach (var comp in _entMan.GetComponents(machine))
        {
            if (comp.GetType().Name == "ItemPlacerComponent")
            {
                if (Reflect.Get(comp, "PlacedEntities", ReflectOptions.Any) is IEnumerable placed)
                {
                    foreach (var entObj in placed)
                    {
                        if (entObj is EntityUid ent && ent == beaker) return true;
                    }
                }
            }
        }
        return false;
    }

    public List<EntityUid> FindMachinesWithReagent(string reagentId)
    {
        var localPlayer = _player.LocalSession?.AttachedEntity;
        if (localPlayer == null) return [];

        var playerPos = _transform.GetMapCoordinates(localPlayer.Value);
        var entities = _lookup.GetEntitiesInRange(playerPos, 15f, LookupFlags.Uncontained);

        var validMachines = new List<EntityUid>();

        foreach (var ent in entities)
        {
            // ReagentDispenserComponent is often server-only, so fall back to name and component-name
            // detection.
            bool isDispenser = false;
            var meta = MetaData(ent);
            var name = meta.EntityName.ToLower();
            var proto = (meta.EntityPrototype?.ID ?? "").ToLower();

            if (name.Contains("dispenser") && !name.Contains("kit") && !proto.Contains("kit"))
            {
                isDispenser = true;
            }
            else
            {
                foreach (var comp in _entMan.GetComponents(ent))
                {
                    var compName = comp.GetType().Name;
                    if (compName.Contains("ReagentDispenser") || compName.Contains("SharedReagentDispenser"))
                    {
                        isDispenser = true;
                        break;
                    }
                }
            }

            if (isDispenser && (FindJugLocation(ent, reagentId) != null || FindEnergyReagentId(ent, reagentId) != null)) 
            {
                validMachines.Add(ent);
            }
        }
        
        return OrderByDistance(validMachines, playerPos);
    }

    private void ForEachSolution(EntityUid beakerUid, Action<object> action)
    {
        var entitiesToScan = new HashSet<EntityUid> { beakerUid };
        
        foreach (var comp in _entMan.GetComponents(beakerUid))
        {
            var compName = comp.GetType().Name;
            if (compName == "SolutionContainerManagerComponent" || compName == "SolutionManagerComponent")
            {
                if (Reflect.Get(comp, "Containers", ReflectOptions.Any) is IEnumerable containers)
                {
                    foreach (var solObj in containers)
                    {
                        if (solObj is string solName &&
                            _containerSystem.TryGetContainer(beakerUid, $"solution@{solName}", out var container) &&
                            container is ContainerSlot slot && slot.ContainedEntity.HasValue)
                        {
                            entitiesToScan.Add(slot.ContainedEntity.Value);
                        }
                    }
                }
            }
        }

        if (_entMan.TryGetComponent<TransformComponent>(beakerUid, out var xform))
        {
            var childEnumerator = xform.ChildEnumerator;
            while (childEnumerator.MoveNext(out var child))
            {
                entitiesToScan.Add(child);
            }
        }

        foreach (var ent in entitiesToScan)
        {
            foreach (var comp in _entMan.GetComponents(ent))
            {
                var name = comp.GetType().Name;

                if (name == "SolutionComponent")
                {
                    var sol = Reflect.Get(comp, "Solution", ReflectOptions.Any);
                    if (sol != null) action(sol);
                }
                
                if (name == "SolutionContainerManagerComponent" || name == "SolutionManagerComponent")
                {
                    if (Reflect.Get(comp, "Solutions", ReflectOptions.Any) is IDictionary dict)
                    {
                        foreach (var value in dict.Values)
                        {
                            if (value != null) action(value);
                        }
                    }
                }

                if (name == "TemperatureComponent")
                {
                    action(comp);
                }
            }
        }
    }

    public float GetReagentAmount(EntityUid beakerUid, string reagentId)
    {
        if (!beakerUid.IsValid()) return 0f;
        float totalAmount = 0f;

        ForEachSolution(beakerUid, solution => {
            if (Reflect.Get(solution, "Contents", ReflectOptions.Any) is not IEnumerable contents) return;

            foreach (var quantity in contents)
            {
                var reagent = Reflect.Get(quantity, "Reagent", ReflectOptions.Any);
                var qAmount = Reflect.Get(quantity, "Quantity", ReflectOptions.Any);
                var proto = Reflect.Get(reagent!, "Prototype", ReflectOptions.Any)?.ToString(); // string or ProtoId<ReagentPrototype> depending on version
                if (proto != null && proto.Equals(reagentId, StringComparison.OrdinalIgnoreCase))
                    totalAmount += Float(qAmount);
            }
        });
        return totalAmount;
    }

    public Dictionary<string, float> GetFullBreakdown(EntityUid beakerUid)
    {
        var result = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);
        if (!beakerUid.IsValid()) return result;

        ForEachSolution(beakerUid, solution => {
            if (Reflect.Get(solution, "Contents", ReflectOptions.Any) is not IEnumerable contents) return;

            foreach (var quantity in contents)
            {
                var reagent = Reflect.Get(quantity, "Reagent", ReflectOptions.Any);
                var qAmount = Reflect.Get(quantity, "Quantity", ReflectOptions.Any);
                var proto = Reflect.Get(reagent!, "Prototype", ReflectOptions.Any)?.ToString(); // string or ProtoId<ReagentPrototype> depending on version
                
                if (proto != null)
                {
                    if (!result.ContainsKey(proto)) result[proto] = 0f;
                    result[proto] += Float(qAmount);
                }
            }
        });
        return result;
    }

    public float GetBeakerCapacity(EntityUid beakerUid)
    {
        if (!beakerUid.IsValid()) return 50f;
        float maxVol = 0f;

        ForEachSolution(beakerUid, solution => {
            var mVol = Reflect.GetFirst(solution, "MaxVolume", "Capacity", "_maxVolume");

            if (mVol != null)
            {
                float f = Float(mVol);
                if (f > maxVol) maxVol = f;
            }
        });

        return maxVol > 0 ? maxVol : 50f;
    }

    public float? GetBeakerTemperature(EntityUid? beakerUid)
    {
        if (!beakerUid.HasValue || !beakerUid.Value.IsValid()) return null;
        float maxTemp = -1f;

        ForEachSolution(beakerUid.Value, obj => {
            var tempVal = Reflect.GetFirst(obj, "Temperature", "CurrentTemperature", "_temperature");

            if (tempVal != null)
            {
                float f = Float(tempVal);
                if (f > 1.0f)
                {
                    if (Math.Abs(f - 293.15f) > 0.1f)
                    {
                        if (f > maxTemp) maxTemp = f;
                    }
                    else if (maxTemp < 0) maxTemp = f;
                }
            }
        });

        return maxTemp > 0 ? maxTemp : null;
    }

    public List<ReagentDispenserDispenseAmount> GetAvailableDoses(EntityUid dispenser)
    {
        var doses = new List<ReagentDispenserDispenseAmount>();
        if (!TryComp<UserInterfaceComponent>(dispenser, out var uiComp)) return doses;
        foreach (var (_, ui) in uiComp.ClientOpenInterfaces)
        {
            var state = Reflect.Get(ui, "State", ReflectOptions.Any);
            if (state == null) continue;
            var stateTypeName = state.GetType().Name;
            if (state is not ReagentDispenserBoundUserInterfaceState &&
                stateTypeName != "EnergyReagentDispenserBoundUserInterfaceState")
                continue;

            var selectedAmt = Reflect.Get(state, "SelectedDispenseAmount", ReflectOptions.Any);
            if (selectedAmt != null && selectedAmt.GetType().IsEnum)
            {
                foreach (var val in Enum.GetValues(selectedAmt.GetType()))
                    doses.Add((ReagentDispenserDispenseAmount)(int)val);
                return doses;
            }

            return [.. Enum.GetValues<ReagentDispenserDispenseAmount>()];
        }
        return doses;
    }

    public string? FindEnergyReagentId(EntityUid dispenserUid, string reagentId)
    {
        if (TryComp<UserInterfaceComponent>(dispenserUid, out var uiComp))
        {
            foreach (var ui in uiComp.ClientOpenInterfaces.Values)
            {
                var state = Reflect.Get(ui, "State", ReflectOptions.Any);
                if (state == null || state.GetType().Name != "EnergyReagentDispenserBoundUserInterfaceState")
                    continue;

                if (Reflect.Get(state, "Inventory", ReflectOptions.Any) is IEnumerable inventory)
                {
                    foreach (var item in inventory)
                    {
                        var rId = Reflect.Get(item, "ReagentId", ReflectOptions.Any) as string;
                        var label = Reflect.Get(item, "ReagentLabel", ReflectOptions.Any) as string;
                        if ((rId != null && rId.Equals(reagentId, StringComparison.OrdinalIgnoreCase)) ||
                            (label != null && label.Contains(reagentId, StringComparison.OrdinalIgnoreCase)))
                        {
                            return rId ?? reagentId;
                        }
                    }
                }
            }
        }

        foreach (var comp in _entMan.GetComponents(dispenserUid))
        {
            if (comp.GetType().Name.Contains("EnergyReagentDispenser", StringComparison.OrdinalIgnoreCase) &&
                Reflect.Get(comp, "Reagents", ReflectOptions.Any) is IDictionary dict)
            {
                foreach (var key in dict.Keys)
                {
                    if (key is string k && k.Equals(reagentId, StringComparison.OrdinalIgnoreCase))
                        return k;
                }
            }
        }

        return null;
    }

    public ItemStorageLocation? FindJugLocation(EntityUid dispenserUid, string reagentId)
    {
        // Server-sent BUI state: exactly the locations the vanilla dispense buttons use.
        if (TryComp<UserInterfaceComponent>(dispenserUid, out var uiComp))
        {
            foreach (var ui in uiComp.ClientOpenInterfaces.Values)
            {
                if (Reflect.Get(ui, "State", ReflectOptions.Any) is not ReagentDispenserBoundUserInterfaceState state) continue;
                foreach (var item in state.Inventory)
                {
                    if (item.ReagentLabel.Contains(reagentId, StringComparison.OrdinalIgnoreCase))
                        return item.StorageLocation;
                }
            }
        }

        // ReagentDispenserComponent's own inventory. Server-only, so only forks that replicate it hit
        // this branch.
        foreach (var comp in _entMan.GetComponents(dispenserUid))
        {
            var compName = comp.GetType().Name;
            if (compName.Contains("ReagentDispenser") || compName.Contains("SharedReagentDispenser"))
            {
                if (Reflect.Get(comp, "Inventory", ReflectOptions.Any) is IEnumerable inventory)
                {
                    foreach (var item in inventory)
                    {
                        var location = Reflect.Get(item, "StorageLocation", ReflectOptions.Any);

                        // Either id may be a substring of the other.
                        if (Reflect.Get(item, "ReagentLabel", ReflectOptions.Any) is string label && (label.Contains(reagentId, StringComparison.OrdinalIgnoreCase) || reagentId.Contains(label, StringComparison.OrdinalIgnoreCase)))
                        {
                            if (location is ItemStorageLocation loc) return loc;
                        }
                    }
                }
            }
        }

        // Older builds store the jug entity itself in a plain StorageComponent.
        if (TryComp<StorageComponent>(dispenserUid, out var storage))
        {
            foreach (var (itemUid, location) in storage.StoredItems)
            {
                var meta = MetaData(itemUid);
                var proto = (meta.EntityPrototype?.ID ?? "").ToLower();
                var name = meta.EntityName.ToLower();
                if (proto.Contains(reagentId.ToLower()) || name.Contains(reagentId.ToLower()))
                    return location;
            }
        }

        return null;
    }
}