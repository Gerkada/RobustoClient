using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
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

    private static object? GetMemberValue(object obj, string name)
    {
        if (obj == null) return null;
        var type = obj.GetType();
        var flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
        
        var prop = type.GetProperty(name, flags);
        if (prop != null) return prop.GetValue(obj);
        
        var field = type.GetField(name, flags);
        if (field != null) return field.GetValue(obj);
        
        return null;
    }

    private static float GetFloatValue(object? obj)
    {
        if (obj == null) return 0f;
        if (obj is float f) return f;
        if (obj is double d) return (float)d;
        if (obj is int i) return (float)i;

        var floatMethod = obj.GetType().GetMethod("Float");
        if (floatMethod != null)
        {
            var val = floatMethod.Invoke(obj, null);
            if (val is float fv) return fv;
        }

        try { return Convert.ToSingle(obj); } catch { return 0f; }
    }

    public bool IsBeaker(EntityUid uid)
    {
        if (!uid.IsValid()) return false;
        var meta = MetaData(uid);
        var proto = (meta.EntityPrototype?.ID ?? "").ToLower();
        var name = meta.EntityName.ToLower();

        return proto.Contains("beaker") || proto.Contains("jug") || proto.Contains("vial") || 
               name.Contains("beaker") || name.Contains("glass") || name.Contains("flask") || name.Contains("vial");
    }

    public EntityUid? FindMachine(string keyword)
    {
        return FindMachines(keyword).FirstOrDefault();
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

        return [.. validMachines.OrderBy(m => {
            var mPos = _transform.GetMapCoordinates(m);
            if (mPos.MapId != playerPos.MapId) return float.MaxValue;
            return (mPos.Position - playerPos.Position).LengthSquared();
        })];
    }

    public EntityUid? GetBeakerInHand()
    {
        var localPlayer = _player.LocalSession?.AttachedEntity;
        if (localPlayer == null) return null;

        foreach (var heldItem in _hands.EnumerateHeld(localPlayer.Value))
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
                if (GetMemberValue(comp, "PlacedEntities") is IEnumerable placed)
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

    public EntityUid? FindMachineWithReagent(string reagentId)
    {
        return FindMachinesWithReagent(reagentId).FirstOrDefault();
    }

    public List<EntityUid> FindMachinesWithReagent(string reagentId)
    {
        var localPlayer = _player.LocalSession?.AttachedEntity;
        if (localPlayer == null) return [];

        var playerPos = _transform.GetMapCoordinates(localPlayer.Value);
        // Increased radius to 15, as chem labs can be large
        var entities = _lookup.GetEntitiesInRange(playerPos, 15f, LookupFlags.Uncontained);
        
        var validMachines = new List<EntityUid>();

        foreach (var ent in entities)
        {
            // Only consider actual reagent dispensers
            // On the client, ReagentDispenserComponent might not be visible as it's often server-only.
            // We use a combination of name-based and component-based detection.
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
        
        return [.. validMachines.OrderBy(m => {
            var mPos = _transform.GetMapCoordinates(m);
            if (mPos.MapId != playerPos.MapId) return float.MaxValue;
            return (mPos.Position - playerPos.Position).LengthSquared();
        })];
    }

    private void ForEachSolution(EntityUid beakerUid, Action<object> action)
    {
        var entitiesToScan = new HashSet<EntityUid> { beakerUid };
        
        foreach (var comp in _entMan.GetComponents(beakerUid))
        {
            var compName = comp.GetType().Name;
            if (compName == "SolutionContainerManagerComponent" || compName == "SolutionManagerComponent")
            {
                if (GetMemberValue(comp, "Containers") is IEnumerable containers)
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
                    var sol = GetMemberValue(comp, "Solution");
                    if (sol != null) action(sol);
                }
                
                if (name == "SolutionContainerManagerComponent" || name == "SolutionManagerComponent")
                {
                    if (GetMemberValue(comp, "Solutions") is IDictionary dict)
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
            if (GetMemberValue(solution, "Contents") is not IEnumerable contents) return;

            foreach (var quantity in contents)
            {
                var reagent = GetMemberValue(quantity, "Reagent");
                var qAmount = GetMemberValue(quantity, "Quantity");
                var proto = GetMemberValue(reagent!, "Prototype")?.ToString(); // string or ProtoId<ReagentPrototype> depending on version
                if (proto != null && proto.Equals(reagentId, StringComparison.OrdinalIgnoreCase))
                    totalAmount += GetFloatValue(qAmount);
            }
        });
        return totalAmount;
    }

    // --- DEBUG: Get a full breakdown of all reagents ---
    public Dictionary<string, float> GetFullBreakdown(EntityUid beakerUid)
    {
        var result = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);
        if (!beakerUid.IsValid()) return result;

        ForEachSolution(beakerUid, solution => {
            if (GetMemberValue(solution, "Contents") is not IEnumerable contents) return;

            foreach (var quantity in contents)
            {
                var reagent = GetMemberValue(quantity, "Reagent");
                var qAmount = GetMemberValue(quantity, "Quantity");
                var proto = GetMemberValue(reagent!, "Prototype")?.ToString(); // string or ProtoId<ReagentPrototype> depending on version
                
                if (proto != null)
                {
                    if (!result.ContainsKey(proto)) result[proto] = 0f;
                    result[proto] += GetFloatValue(qAmount);
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
            var mVol = GetMemberValue(solution, "MaxVolume") ?? 
                       GetMemberValue(solution, "Capacity") ??
                       GetMemberValue(solution, "_maxVolume");
            
            if (mVol != null)
            {
                float f = GetFloatValue(mVol);
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
            var tempVal = GetMemberValue(obj, "Temperature") ?? 
                          GetMemberValue(obj, "CurrentTemperature") ??
                          GetMemberValue(obj, "_temperature");

            if (tempVal != null)
            {
                float f = GetFloatValue(tempVal);
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
            var state = GetMemberValue(ui, "State");
            if (state == null) continue;
            var stateTypeName = state.GetType().Name;
            if (state is not ReagentDispenserBoundUserInterfaceState &&
                stateTypeName != "EnergyReagentDispenserBoundUserInterfaceState")
                continue;

            var selectedAmt = GetMemberValue(state, "SelectedDispenseAmount");
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
                var state = GetMemberValue(ui, "State");
                if (state == null || state.GetType().Name != "EnergyReagentDispenserBoundUserInterfaceState")
                    continue;

                if (GetMemberValue(state, "Inventory") is IEnumerable inventory)
                {
                    foreach (var item in inventory)
                    {
                        var rId = GetMemberValue(item, "ReagentId") as string;
                        var label = GetMemberValue(item, "ReagentLabel") as string;
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
                GetMemberValue(comp, "Reagents") is IDictionary dict)
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
        // 0. Server-sent BUI state: exactly the locations the vanilla dispense buttons use.
        if (TryComp<UserInterfaceComponent>(dispenserUid, out var uiComp))
        {
            foreach (var ui in uiComp.ClientOpenInterfaces.Values)
            {
                if (GetMemberValue(ui, "State") is not ReagentDispenserBoundUserInterfaceState state) continue;
                foreach (var item in state.Inventory)
                {
                    if (item.ReagentLabel.Contains(reagentId, StringComparison.OrdinalIgnoreCase))
                        return item.StorageLocation;
                }
            }
        }

        // 1. ReagentDispenserComponent's specialized inventory (server-only component, kept for forks)
        foreach (var comp in _entMan.GetComponents(dispenserUid))
        {
            var compName = comp.GetType().Name;
            if (compName.Contains("ReagentDispenser") || compName.Contains("SharedReagentDispenser"))
            {
                // Try to get the inventory (list of ReagentInventoryItem or similar)
                if (GetMemberValue(comp, "Inventory") is IEnumerable inventory)
                {
                    foreach (var item in inventory)
                    {
                        var location = GetMemberValue(item, "StorageLocation");

                        // Robust label matching: check if requested ID is part of the label or vice versa
                        if (GetMemberValue(item, "ReagentLabel") is string label && (label.Contains(reagentId, StringComparison.OrdinalIgnoreCase) || reagentId.Contains(label, StringComparison.OrdinalIgnoreCase)))
                        {
                            if (location is ItemStorageLocation loc) return loc;
                        }
                    }
                }
            }
        }

        // 2. Fallback check: General StorageComponent
        // This is useful for older builds or machines that hold "jug" entities directly in storage slots.
        if (TryComp<StorageComponent>(dispenserUid, out var storage))
        {
            foreach (var (itemUid, location) in storage.StoredItems)
            {
                var meta = MetaData(itemUid);
                var proto = (meta.EntityPrototype?.ID ?? "").ToLower();
                var name = meta.EntityName.ToLower();

                // If the item itself matches the reagent name, it's likely a container for it
                if (proto.Contains(reagentId.ToLower()) || name.Contains(reagentId.ToLower()))
                    return location;

                // Optionally, we could scan INSIDE these items if they are jugs, but that might be overkill 
                // as most machines expose the jug directly to the dispenser inventory.
            }
        }

        return null;
    }
}