using System.Collections;
using Content.Shared.Hands;
using Content.Shared.Tools.Systems;
using Robust.Shared.Prototypes;

namespace RobustoClient.Systems.Construction;

public sealed partial class RobustaAutoConstructSystem
{
    public bool IsToolReadyForQuality(EntityUid uid, string? quality)
    {
        if (!Exists(uid) || string.IsNullOrWhiteSpace(quality))
            return false;

        if (!_toolSystem.HasQuality(uid, quality))
            return false;

        if (FindComponentByName(uid, "MultipleToolComponent") is { } multiComp &&
            TryGetCurrentMultipleToolEntry(multiComp, out var curEntry) &&
            curEntry != null &&
            !EntryProvidesQuality(curEntry, quality))
        {
            return false;
        }

        if (string.Equals(quality, "Prying", StringComparison.OrdinalIgnoreCase) &&
            FindComponentByName(uid, "PryingComponent") is { } pryingComp &&
            GetMemberValue(pryingComp, "Enabled") is false)
        {
            return false;
        }

        return true;
    }

    public bool HasOrCanCycleToolQuality(EntityUid uid, string? quality)
    {
        if (!Exists(uid) || string.IsNullOrWhiteSpace(quality))
            return false;

        if (IsToolReadyForQuality(uid, quality))
            return true;

        if (FindComponentByName(uid, "MultipleToolComponent") is { } multiComp &&
            EntriesProvideQuality(GetMemberValue(multiComp, "Entries"), quality))
        {
            return true;
        }

        var proto = MetaData(uid).EntityPrototype;
        if (proto != null)
        {
            var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var queue = new Queue<EntityPrototype>();
            queue.Enqueue(proto);
            visited.Add(proto.ID);

            while (queue.Count > 0)
            {
                var cur = queue.Dequeue();
                if (_itemUtil.TryGetPrototypeComponent<object>(cur, "MultipleTool", out var protoMulti) &&
                    protoMulti != null &&
                    EntriesProvideQuality(GetMemberValue(protoMulti, "Entries"), quality))
                {
                    return true;
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
        }

        if (MatchesPrototypeOrParentSubstring(uid, "JawsOfLife"))
        {
            if (string.Equals(quality, "Prying", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(quality, "Cutting", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        if (MatchesPrototypeOrParentSubstring(uid, "PowerDrill") ||
            MatchesPrototypeOrParentSubstring(uid, "HandDrill"))
        {
            if (string.Equals(quality, "Screwing", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(quality, "Anchoring", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static bool TryGetCurrentMultipleToolEntry(object multiComp, out object? entry)
    {
        entry = null;
        if (GetMemberValue(multiComp, "Entries") is not IEnumerable entriesEnum)
            return false;

        var list = new List<object>();
        foreach (var item in entriesEnum)
        {
            if (item != null)
                list.Add(item);
        }

        if (list.Count == 0)
            return false;

        var curObj = GetMemberValue(multiComp, "CurrentEntry");
        int idx = curObj switch
        {
            uint u => (int)u,
            int i => i,
            byte b => b,
            short s => s,
            _ => 0
        };

        if (idx < 0 || idx >= list.Count)
            return false;

        entry = list[idx];
        return true;
    }

    private static bool EntriesProvideQuality(object? entriesObj, string quality)
    {
        if (entriesObj is not IEnumerable entries)
            return false;

        foreach (var entry in entries)
        {
            if (entry != null && EntryProvidesQuality(entry, quality))
                return true;
        }

        return false;
    }

    private static bool EntryProvidesQuality(object entry, string quality)
    {
        var behaviorObj = GetMemberValue(entry, "Behavior")
                          ?? GetMemberValue(entry, "Qualities")
                          ?? GetMemberValue(entry, "Quality");
        if (behaviorObj == null)
            return false;

        if (behaviorObj is string s)
            return string.Equals(s, quality, StringComparison.OrdinalIgnoreCase);

        if (behaviorObj is IEnumerable enumerable)
        {
            foreach (var item in enumerable)
            {
                if (item == null)
                    continue;

                var qStr = item as string ?? GetStringMember(item, "Id") ?? item.ToString();
                if (!string.IsNullOrEmpty(qStr) &&
                    string.Equals(qStr, quality, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }

        return false;
    }

    public bool TryEquipToolOrMultiTool(string quality, string? description = null)
    {
        if (string.IsNullOrWhiteSpace(quality))
            return false;

        _multiToolCycleRetries = 0;
        var label = description ?? $"Tool:{quality}";

        // 1. If the active hand already holds a tool or multi-tool that provides (or can cycle to) `quality`,
        //    keep it in hand so we can cycle it in-place without stowing/swapping.
        if (_player.LocalEntity is { } player &&
            Exists(player) &&
            _hands.GetActiveItem(player) is { } activeHeld &&
            Exists(activeHeld) &&
            HasOrCanCycleToolQuality(activeHeld, quality))
        {
            return true;
        }

        // 2. Prefer equipping a tool that is already in the required mode
        if (_itemUtil.TryEquipByPredicate(
                uid => IsToolReadyForQuality(uid, quality),
                label))
        {
            return true;
        }

        // 3. Equip any multi-tool (e.g. PowerDrill or JawsOfLife) that can cycle to `quality`
        if (_itemUtil.TryEquipByPredicate(
                uid => HasOrCanCycleToolQuality(uid, quality),
                $"Multi{label}"))
        {
            return true;
        }

        // 4. Deep container / interaction-range vicinity fallback
        if (_player.LocalEntity is { } p && Exists(p))
        {
            return TryEquipFromDeepInventoryOrVicinity(
                p,
                uid => HasOrCanCycleToolQuality(uid, quality),
                1);
        }

        return false;
    }

    public bool EnsureHeldToolModeReady(string quality, out bool failed)
    {
        failed = false;
        if (string.IsNullOrWhiteSpace(quality) ||
            _player.LocalEntity is not { } player ||
            !Exists(player) ||
            _hands.GetActiveItem(player) is not { } held ||
            !Exists(held))
        {
            failed = true;
            return false;
        }

        if (IsToolReadyForQuality(held, quality))
        {
            _multiToolCycleRetries = 0;
            return true;
        }

        if (!HasOrCanCycleToolQuality(held, quality))
        {
            failed = true;
            return false;
        }

        _multiToolCycleRetries++;
        if (_multiToolCycleRetries > 6)
        {
            _multiToolCycleRetries = 0;
            failed = true;
            return false;
        }

        RaisePredictiveEvent(new RequestUseInHandEvent());
        return false;
    }

    public bool EnsureHeldWireUiToolReady(out bool failed)
    {
        failed = false;
        if (_player.LocalEntity is not { } player ||
            !Exists(player) ||
            _hands.GetActiveItem(player) is not { } held ||
            !Exists(held))
        {
            failed = true;
            return false;
        }

        if (IsToolReadyForQuality(held, SharedToolSystem.CutQuality) ||
            IsToolReadyForQuality(held, SharedToolSystem.PulseQuality))
        {
            _multiToolCycleRetries = 0;
            return true;
        }

        if (HasOrCanCycleToolQuality(held, SharedToolSystem.CutQuality))
            return EnsureHeldToolModeReady(SharedToolSystem.CutQuality, out failed);

        if (HasOrCanCycleToolQuality(held, SharedToolSystem.PulseQuality))
            return EnsureHeldToolModeReady(SharedToolSystem.PulseQuality, out failed);

        failed = true;
        return false;
    }

    private bool RequiresWelderIgnition(EntityUid toolUid)
    {
        foreach (var comp in EntityManager.GetComponents(toolUid))
        {
            var name = comp.GetType().Name;
            if (name.Equals("WelderComponent", StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    private bool IsWelderLit(EntityUid toolUid)
    {
        foreach (var comp in EntityManager.GetComponents(toolUid))
        {
            var name = comp.GetType().Name;
            if (name.Equals("ItemToggleComponent", StringComparison.OrdinalIgnoreCase))
                return GetBoolMember(comp, "Activated");
            if (name.Equals("WelderComponent", StringComparison.OrdinalIgnoreCase) &&
                GetMemberValue(comp, "Enabled") is bool enabled)
            {
                return enabled;
            }
        }
        return false;
    }

    private void ExtinguishActiveWelderIfNeeded()
    {
        if (_litWelderUid is not { } welder || !Exists(welder))
        {
            _litWelderUid = null;
            return;
        }

        if (IsWelderLit(welder) && _player.LocalEntity is { } player && Exists(player))
        {
            if (_hands.GetActiveItem(player) == welder)
                RaisePredictiveEvent(new RequestUseInHandEvent());
        }

        _litWelderUid = null;
    }
}
