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
using Content.Shared.Storage;
using Robust.Shared.Containers;

namespace RobustoClient.Systems.AutoChem;

public sealed class RobustaWorldScanner : EntitySystem
{
    [Dependency] private readonly IEntityManager _entMan = default!;
    [Dependency] private readonly IPlayerManager _player = default!;
    [Dependency] private readonly SharedTransformSystem _transform = default!;
    [Dependency] private readonly EntityLookupSystem _lookup = default!;
    [Dependency] private readonly SharedHandsSystem _hands = default!;
    [Dependency] private readonly SharedContainerSystem _containerSystem = default!;

    public const float InteractionRange = 1.5f;

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
        if (localPlayer == null) return new List<EntityUid>();

        var playerPos = _transform.GetMapCoordinates(localPlayer.Value);
        var entities = _lookup.GetEntitiesInRange(playerPos, InteractionRange, LookupFlags.Uncontained);

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

        return validMachines.OrderBy(m => {
            var mPos = _transform.GetMapCoordinates(m);
            if (mPos.MapId != playerPos.MapId) return float.MaxValue;
            return (mPos.Position - playerPos.Position).LengthSquared();
        }).ToList();
    }

    public EntityUid? GetBeakerInHand()
    {
        var localPlayer = _player.LocalSession?.AttachedEntity;
        if (localPlayer == null) return null;

        foreach (var heldItem in _hands.EnumerateHeld(localPlayer.Value))
        {
            if (IsBeaker(heldItem)) return heldItem;
        }
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
                var placed = GetMemberValue(comp, "PlacedEntities") as IEnumerable;
                if (placed != null)
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
        if (localPlayer == null) return new List<EntityUid>();

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

            if (isDispenser && FindJugLocation(ent, reagentId) != null) 
            {
                validMachines.Add(ent);
            }
        }
        
        return validMachines.OrderBy(m => {
            var mPos = _transform.GetMapCoordinates(m);
            if (mPos.MapId != playerPos.MapId) return float.MaxValue;
            return (mPos.Position - playerPos.Position).LengthSquared();
        }).ToList();
    }

    private void ForEachSolution(EntityUid beakerUid, Action<object> action)
    {
        var entitiesToScan = new HashSet<EntityUid> { beakerUid };
        
        foreach (var comp in _entMan.GetComponents(beakerUid))
        {
            var compName = comp.GetType().Name;
            if (compName == "SolutionContainerManagerComponent" || compName == "SolutionManagerComponent")
            {
                var containers = GetMemberValue(comp, "Containers") as IEnumerable;
                if (containers != null)
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
                    var dict = GetMemberValue(comp, "Solutions") as IDictionary;
                    if (dict != null)
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
            var contents = GetMemberValue(solution, "Contents") as IEnumerable;
            if (contents == null) return;

            foreach (var quantity in contents)
            {
                var reagent = GetMemberValue(quantity, "Reagent");
                var qAmount = GetMemberValue(quantity, "Quantity");
                var proto = GetMemberValue(reagent!, "Prototype") as string;
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
            var contents = GetMemberValue(solution, "Contents") as IEnumerable;
            if (contents == null) return;

            foreach (var quantity in contents)
            {
                var reagent = GetMemberValue(quantity, "Reagent");
                var qAmount = GetMemberValue(quantity, "Quantity");
                var proto = GetMemberValue(reagent!, "Prototype") as string;
                
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

        foreach (var (key, ui) in uiComp.ClientOpenInterfaces)
        {
            var state = GetMemberValue(ui, "State") as ReagentDispenserBoundUserInterfaceState;
            if (state == null) continue;

            // In some versions, the enum itself defines the buttons. 
            // In others, we might need to check the UI definition.
            // For now, we'll return all standard enums as a base, 
            // but the system will fallback if the click fails.
            return Enum.GetValues<ReagentDispenserDispenseAmount>().ToList();
        }
        return doses;
    }

    public ItemStorageLocation? FindJugLocation(EntityUid dispenserUid, string reagentId)
    {
        // 1. Primary check: ReagentDispenserComponent's specialized inventory
        foreach (var comp in _entMan.GetComponents(dispenserUid))
        {
            var compName = comp.GetType().Name;
            if (compName.Contains("ReagentDispenser") || compName.Contains("SharedReagentDispenser"))
            {
                // Try to get the inventory (list of ReagentInventoryItem or similar)
                var inventory = GetMemberValue(comp, "Inventory") as IEnumerable;
                if (inventory != null)
                {
                    foreach (var item in inventory)
                    {
                        var label = GetMemberValue(item, "ReagentLabel") as string;
                        var location = GetMemberValue(item, "StorageLocation");
                        
                        // Robust label matching: check if requested ID is part of the label or vice versa
                        if (label != null && (label.Contains(reagentId, StringComparison.OrdinalIgnoreCase) || reagentId.Contains(label, StringComparison.OrdinalIgnoreCase)))
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