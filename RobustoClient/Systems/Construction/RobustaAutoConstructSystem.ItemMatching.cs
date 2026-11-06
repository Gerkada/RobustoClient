using Content.Shared.Stacks;
using Content.Shared.Tag;
using Robust.Shared.Prototypes;

namespace RobustoClient.Systems.Construction;

public sealed partial class RobustaAutoConstructSystem
{
    private bool HasValidTag(EntityUid uid, string? tag)
    {
        if (!Exists(uid) || string.IsNullOrWhiteSpace(tag))
            return false;

        try
        {
            if (!_protoMan.HasIndex<TagPrototype>(tag))
                return false;

            return _tagSystem.HasTag(uid, tag);
        }
        catch
        {
            return false;
        }
    }

    private bool MatchesTagOrPrototype(EntityUid uid, string tag)
    {
        if (!Exists(uid) || string.IsNullOrWhiteSpace(tag))
            return false;

        if (HasValidTag(uid, tag))
            return true;

        if (MatchesPrototypeOrParentId(uid, tag, allowPrefixMatch: false))
            return true;

        if (EntityManager.GetComponents(uid).Any(c =>
                c.GetType().Name.Equals(tag, StringComparison.OrdinalIgnoreCase) ||
                c.GetType().Name.Equals(tag + "Component", StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        var proto = MetaData(uid).EntityPrototype;
        if (proto != null && _itemUtil.HasPrototypeComponent(proto, tag))
            return true;

        return false;
    }

    private bool MatchesMaterialOrStack(EntityUid uid, string matId)
    {
        if (!Exists(uid) || string.IsNullOrWhiteSpace(matId))
            return false;

        string? liveStackType = null;
        if (TryComp<StackComponent>(uid, out var stack))
        {
            liveStackType = GetStringMember(stack, "StackTypeId") ?? GetStringMember(stack, "StackType");
        }

        if (string.IsNullOrEmpty(liveStackType) && FindComponentByName(uid, "StackComponent") is { } rawStack)
        {
            liveStackType = GetStringMember(rawStack, "StackTypeId") ?? GetStringMember(rawStack, "StackType");
        }

        if (!string.IsNullOrEmpty(liveStackType) &&
            string.Equals(liveStackType, matId, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (TryGetPrototypeStackType(uid, out var protoStackType))
        {
            if (string.Equals(protoStackType, matId, StringComparison.OrdinalIgnoreCase))
                return true;

            if (string.IsNullOrEmpty(liveStackType))
                liveStackType = protoStackType;
        }

        // Check if StackPrototype's Spawn entity prototype matches this entity or its parent prototypes
        if (_protoMan.TryIndex<StackPrototype>(matId, out var stackProto))
        {
            var spawnProtoId = GetStringMember(stackProto, "Spawn");
            if (!string.IsNullOrEmpty(spawnProtoId) &&
                MatchesPrototypeOrParentId(uid, spawnProtoId, allowPrefixMatch: true))
            {
                return true;
            }
        }

        // Handle LV / MV / HV Cable family matching
        bool wantsLvCable = string.Equals(matId, "Cable", StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(matId, "CableLV", StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(matId, "CableApc", StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(matId, "CableApcExtension", StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(matId, "CableCoil", StringComparison.OrdinalIgnoreCase);

        if (wantsLvCable)
        {
            // Exclude MV/HV cables and non-coil items like cable cuffs, terminals, zipties, bolas
            if (!string.IsNullOrEmpty(liveStackType) &&
                (liveStackType.Contains("MV", StringComparison.OrdinalIgnoreCase) ||
                 liveStackType.Contains("HV", StringComparison.OrdinalIgnoreCase)))
            {
                return false;
            }

            if (MatchesPrototypeOrParentSubstring(uid, "CableMV") ||
                MatchesPrototypeOrParentSubstring(uid, "CableHV") ||
                MatchesPrototypeOrParentSubstring(uid, "CableTerminal") ||
                MatchesPrototypeOrParentSubstring(uid, "Cuffs") ||
                MatchesPrototypeOrParentSubstring(uid, "Restraint") ||
                MatchesPrototypeOrParentSubstring(uid, "Ziptie") ||
                MatchesPrototypeOrParentSubstring(uid, "Bola"))
            {
                return false;
            }

            if (HasValidTag(uid, "CableCoil") ||
                HasValidTag(uid, "Cable") ||
                HasValidTag(uid, "CableLV") ||
                HasValidTag(uid, "CableApc"))
            {
                return true;
            }

            if (!string.IsNullOrEmpty(liveStackType) &&
                (liveStackType.Contains("Cable", StringComparison.OrdinalIgnoreCase) ||
                 liveStackType.Contains("Apc", StringComparison.OrdinalIgnoreCase) ||
                 liveStackType.Contains("LV", StringComparison.OrdinalIgnoreCase) ||
                 liveStackType.Contains("Wire", StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }

            if (MatchesPrototypeOrParentSubstring(uid, "CableApc") ||
                MatchesPrototypeOrParentSubstring(uid, "CableLV") ||
                MatchesPrototypeOrParentSubstring(uid, "CableStack"))
            {
                return true;
            }

            if (FindComponentByName(uid, "CablePlacerComponent") is { } cablePlacer)
            {
                var cableProto = GetStringMember(cablePlacer, "CablePrototypeId")
                                 ?? GetStringMember(cablePlacer, "CablePrototypeID")
                                 ?? GetStringMember(cablePlacer, "Cable")
                                 ?? string.Empty;
                if (!cableProto.Contains("MV", StringComparison.OrdinalIgnoreCase) &&
                    !cableProto.Contains("HV", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            var proto = MetaData(uid).EntityPrototype;
            if (proto != null &&
                _itemUtil.TryGetPrototypeComponent<object>(proto, "CablePlacer", out var protoPlacer) &&
                protoPlacer != null)
            {
                var cableProto = GetStringMember(protoPlacer, "CablePrototypeId")
                                 ?? GetStringMember(protoPlacer, "CablePrototypeID")
                                 ?? GetStringMember(protoPlacer, "Cable")
                                 ?? string.Empty;
                if (!cableProto.Contains("MV", StringComparison.OrdinalIgnoreCase) &&
                    !cableProto.Contains("HV", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }
        else if (string.Equals(matId, "CableMV", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(matId, "CableMVStack", StringComparison.OrdinalIgnoreCase))
        {
            if ((!string.IsNullOrEmpty(liveStackType) && liveStackType.Contains("CableMV", StringComparison.OrdinalIgnoreCase)) ||
                MatchesPrototypeOrParentSubstring(uid, "CableMV"))
            {
                return true;
            }
        }
        else if (string.Equals(matId, "CableHV", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(matId, "CableHVStack", StringComparison.OrdinalIgnoreCase))
        {
            if ((!string.IsNullOrEmpty(liveStackType) && liveStackType.Contains("CableHV", StringComparison.OrdinalIgnoreCase)) ||
                MatchesPrototypeOrParentSubstring(uid, "CableHV"))
            {
                return true;
            }
        }

        // General sheet/stack prototype naming conventions (e.g. SheetSteel, SheetGlass, MaterialPlasteel)
        if (HasComp<StackComponent>(uid) || !string.IsNullOrEmpty(liveStackType))
        {
            if (MatchesPrototypeOrParentId(uid, matId, allowPrefixMatch: true) ||
                MatchesPrototypeOrParentId(uid, $"Sheet{matId}", allowPrefixMatch: true) ||
                MatchesPrototypeOrParentId(uid, $"Material{matId}", allowPrefixMatch: true) ||
                MatchesPrototypeOrParentId(uid, $"{matId}Stack", allowPrefixMatch: true) ||
                MatchesPrototypeOrParentId(uid, $"Part{matId}", allowPrefixMatch: true))
            {
                return true;
            }
        }

        return HasValidTag(uid, matId);
    }

    private bool TryGetPrototypeStackType(EntityUid uid, out string stackType)
    {
        stackType = string.Empty;
        var proto = MetaData(uid).EntityPrototype;
        if (proto == null)
            return false;

        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var queue = new Queue<EntityPrototype>();
        queue.Enqueue(proto);
        visited.Add(proto.ID);

        while (queue.Count > 0)
        {
            var cur = queue.Dequeue();
            if (_itemUtil.TryGetPrototypeComponent<object>(cur, "Stack", out var stackComp) && stackComp != null)
            {
                var st = GetStringMember(stackComp, "StackTypeId") ?? GetStringMember(stackComp, "StackType");
                if (!string.IsNullOrEmpty(st))
                {
                    stackType = st;
                    return true;
                }
            }

            if (cur.Parents != null)
            {
                foreach (var parentId in cur.Parents)
                {
                    if (visited.Add(parentId) && _protoMan.TryIndex<EntityPrototype>(parentId, out var parentProto))
                        queue.Enqueue(parentProto);
                }
            }
        }

        return false;
    }

    private bool MatchesPrototypeOrParentId(EntityUid uid, string targetProtoId, bool allowPrefixMatch)
    {
        if (!Exists(uid) || string.IsNullOrEmpty(targetProtoId))
            return false;

        var proto = MetaData(uid).EntityPrototype;
        if (proto == null)
            return false;

        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var queue = new Queue<EntityPrototype>();
        queue.Enqueue(proto);
        visited.Add(proto.ID);

        while (queue.Count > 0)
        {
            var cur = queue.Dequeue();
            if (string.Equals(cur.ID, targetProtoId, StringComparison.OrdinalIgnoreCase))
                return true;

            if (allowPrefixMatch &&
                (cur.ID.StartsWith(targetProtoId, StringComparison.OrdinalIgnoreCase) ||
                 targetProtoId.StartsWith(cur.ID, StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }

            if (cur.Parents != null)
            {
                foreach (var parentId in cur.Parents)
                {
                    if (string.Equals(parentId, targetProtoId, StringComparison.OrdinalIgnoreCase))
                        return true;

                    if (visited.Add(parentId) && _protoMan.TryIndex<EntityPrototype>(parentId, out var parentProto))
                        queue.Enqueue(parentProto);
                }
            }
        }

        return false;
    }

    private bool MatchesPrototypeOrParentSubstring(EntityUid uid, string substring)
    {
        if (!Exists(uid) || string.IsNullOrEmpty(substring))
            return false;

        var proto = MetaData(uid).EntityPrototype;
        if (proto == null)
            return false;

        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var queue = new Queue<EntityPrototype>();
        queue.Enqueue(proto);
        visited.Add(proto.ID);

        while (queue.Count > 0)
        {
            var cur = queue.Dequeue();
            if (cur.ID.Contains(substring, StringComparison.OrdinalIgnoreCase))
                return true;

            if (cur.Parents != null)
            {
                foreach (var parentId in cur.Parents)
                {
                    if (parentId.Contains(substring, StringComparison.OrdinalIgnoreCase))
                        return true;

                    if (visited.Add(parentId) && _protoMan.TryIndex<EntityPrototype>(parentId, out var parentProto))
                        queue.Enqueue(parentProto);
                }
            }
        }

        return false;
    }

    private bool MatchesMachinePart(EntityUid uid, string partId)
    {
        if (!Exists(uid) || string.IsNullOrEmpty(partId))
            return false;

        if (MatchesMaterialOrStack(uid, partId))
            return true;

        foreach (var comp in EntityManager.GetComponents(uid))
        {
            if (string.Equals(comp.GetType().Name, "MachinePartComponent", StringComparison.OrdinalIgnoreCase))
            {
                var partVal = GetMemberValue(comp, "Part") ?? GetMemberValue(comp, "PartType");
                var partStr = GetMemberValue(partVal, "Id") as string ?? partVal?.ToString();
                if (string.Equals(partStr, partId, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
        }

        var proto = MetaData(uid).EntityPrototype;
        if (proto != null)
        {
            if (_itemUtil.TryGetPrototypeComponent<object>(proto, "MachinePart", out var mpComp) && mpComp != null)
            {
                var partVal = GetMemberValue(mpComp, "Part") ?? GetMemberValue(mpComp, "PartType");
                var partStr = GetMemberValue(partVal, "Id") as string ?? partVal?.ToString();
                if (string.Equals(partStr, partId, StringComparison.OrdinalIgnoreCase))
                    return true;
            }

            if (proto.ID.Contains(partId, StringComparison.OrdinalIgnoreCase) &&
                proto.ID.Contains("StockPart", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return HasValidTag(uid, partId);
    }
}