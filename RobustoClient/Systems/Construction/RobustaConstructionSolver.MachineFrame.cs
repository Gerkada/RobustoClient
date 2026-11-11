using System.Collections;
using System.Reflection;
using Content.Shared.Stacks;
using Robust.Shared.Prototypes;

namespace RobustoClient.Systems.Construction;

public sealed partial class RobustaConstructionSolver
{
    public void NoteHeldEntity(EntityUid? heldUid)
    {
        if (!heldUid.HasValue || _entMan == null || !_entMan.EntityExists(heldUid.Value))
            return;

        if (IsMachineBoardEntity(heldUid.Value))
        {
            var protoId = _entMan.GetComponent<MetaDataComponent>(heldUid.Value).EntityPrototype?.ID;
            if (!string.IsNullOrEmpty(protoId))
                _lastKnownMachineBoardProtoId = protoId;
        }
    }

    public bool HasMachineFrameBoard(EntityUid uid)
    {
        if (_entMan == null || !_entMan.EntityExists(uid))
            return false;

        if (_container != null &&
            _container.TryGetContainer(uid, "machine_board", out var boardCont) &&
            boardCont.ContainedEntities.Count > 0)
        {
            return true;
        }

        var mfComp = FindComponentByName(uid, "MachineFrameComponent");
        if (mfComp != null && GetMemberValue(mfComp, "HasBoard") is bool hasBoardProp && hasBoardProp)
            return true;

        if (TryGetSpriteLayerZeroState(uid, out var spriteState) && !string.IsNullOrEmpty(spriteState))
        {
            if (spriteState.EndsWith("_2", StringComparison.OrdinalIgnoreCase))
                return true;
            if (spriteState.EndsWith("_0", StringComparison.OrdinalIgnoreCase) ||
                spriteState.EndsWith("_1", StringComparison.OrdinalIgnoreCase))
                return false;
        }

        if (TryGetMachineFrameVisualState(uid, out int visualState))
            return visualState >= 3;

        return false;
    }

    private bool TryGetMachineFrameVisualState(EntityUid uid, out int visualState)
    {
        visualState = 0;
        var appComp = FindComponentByName(uid, "AppearanceComponent");
        if (appComp == null)
            return false;

        if (GetMemberValue(appComp, "AppearanceData") is IDictionary dict)
        {
            foreach (DictionaryEntry entry in dict)
            {
                var keyType = entry.Key?.GetType().Name ?? string.Empty;
                var keyName = entry.Key?.ToString() ?? string.Empty;
                if (keyType.Contains("MachineFrameVisuals", StringComparison.OrdinalIgnoreCase) ||
                    keyName.Contains("MachineFrameVisuals", StringComparison.OrdinalIgnoreCase))
                {
                    if (entry.Value is int i)
                    {
                        visualState = i;
                        return true;
                    }
                }
            }
        }

        return false;
    }

    public bool TryGetSpriteLayerZeroState(EntityUid uid, out string? stateName)
    {
        stateName = null;
        var spriteComp = FindComponentByName(uid, "SpriteComponent");
        if (spriteComp == null)
            return false;

        try
        {
            var layerGetState = spriteComp.GetType().GetMethod(
                "LayerGetState",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance,
                null,
                [typeof(int)],
                null);
            if (layerGetState != null)
            {
                var stateIdObj = layerGetState.Invoke(spriteComp, [0]);
                var name = GetStringMember(stateIdObj, "Name") ?? stateIdObj?.ToString();
                if (!string.IsNullOrEmpty(name))
                {
                    stateName = name;
                    return true;
                }
            }

            var layers = GetMemberValue(spriteComp, "AllLayers") as IEnumerable
                         ?? GetMemberValue(spriteComp, "_layers") as IEnumerable;
            if (layers != null)
            {
                foreach (var entry in layers)
                {
                    if (entry == null)
                        continue;

                    var layer = entry is DictionaryEntry de ? de.Value : GetMemberValue(entry, "Value") ?? entry;
                    if (layer == null)
                        continue;

                    var stateObj = GetMemberValue(layer, "RsiState")
                                   ?? GetMemberValue(layer, "State")
                                   ?? GetMemberValue(layer, "_state");
                    var name = GetStringMember(stateObj, "Name") ?? stateObj?.ToString();
                    if (!string.IsNullOrEmpty(name))
                    {
                        stateName = name;
                        return true;
                    }
                    break;
                }
            }
        }
        catch
        {
            // Ignore reflection differences across forks
        }

        return false;
    }

    private void AppendMachineFrameStepsIfNeeded(
        in ConstructionTargetSnapshot snapshot,
        IndexedConstructionGraph graph,
        IndexedGraphNode fromNode,
        IndexedGraphNode toNode,
        List<ConstructionActionStep> steps)
    {
        if (!string.Equals(graph.Id, "Machine", StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(fromNode.Name, "machineFrame", StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(toNode.Name, "machine", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var board = snapshot.MachineBoard;
        if (!snapshot.HasMachineFrameBoard)
        {
            if (board != null && !string.IsNullOrEmpty(board.BoardPrototypeId))
            {
                _lastKnownMachineBoardProtoId = board.BoardPrototypeId;
                steps.Add(new ConstructionActionStep(
                    ConstructionActionKind.InsertPrototype,
                    $"Insert {board.BoardPrototypeId}",
                    PrototypeOrQualityOrTag: board.BoardPrototypeId,
                    Amount: 1,
                    DoAfterDelay: 0f,
                    ExpectedNextNode: null));
            }
            else
            {
                steps.Add(new ConstructionActionStep(
                    ConstructionActionKind.InsertComponent,
                    "Insert MachineBoard",
                    PrototypeOrQualityOrTag: "MachineBoard",
                    Amount: 1,
                    DoAfterDelay: 0f,
                    ExpectedNextNode: null));
            }
        }

        if (board == null)
            return;

        var seenStacks = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (board.StackRequirements != null)
        {
            foreach (var (stackId, amount) in board.StackRequirements)
            {
                if (amount > 0 && !string.IsNullOrEmpty(stackId) && seenStacks.Add(stackId))
                {
                    steps.Add(new ConstructionActionStep(
                        ConstructionActionKind.AddMaterial,
                        $"Add {amount}x {stackId}",
                        PrototypeOrQualityOrTag: stackId,
                        Amount: amount,
                        DoAfterDelay: 0f,
                        ExpectedNextNode: null));
                }
            }
        }

        if (board.PartRequirements != null)
        {
            foreach (var (partId, amount) in board.PartRequirements)
            {
                if (amount <= 0 || string.IsNullOrEmpty(partId) || seenStacks.Contains(partId))
                    continue;

                for (int k = 0; k < amount; k++)
                {
                    steps.Add(new ConstructionActionStep(
                        ConstructionActionKind.InsertComponent,
                        $"Insert {partId} ({k + 1}/{amount})",
                        PrototypeOrQualityOrTag: $"MachinePart:{partId}",
                        Amount: 1,
                        DoAfterDelay: 0f,
                        ExpectedNextNode: null));
                }
            }
        }

        if (board.ComponentRequirements != null)
        {
            foreach (var (compName, amt) in board.ComponentRequirements)
            {
                if (amt > 0 && !string.IsNullOrEmpty(compName))
                {
                    for (int k = 0; k < amt; k++)
                    {
                        steps.Add(new ConstructionActionStep(
                            ConstructionActionKind.InsertComponent,
                            $"Insert {compName} ({k + 1}/{amt})",
                            PrototypeOrQualityOrTag: compName,
                            Amount: 1,
                            DoAfterDelay: 0f,
                            ExpectedNextNode: null));
                    }
                }
            }
        }

        if (board.TagRequirements != null)
        {
            foreach (var (tagId, amt) in board.TagRequirements)
            {
                if (amt > 0 && !string.IsNullOrEmpty(tagId))
                {
                    for (int k = 0; k < amt; k++)
                    {
                        steps.Add(new ConstructionActionStep(
                            ConstructionActionKind.InsertTag,
                            $"Insert [{tagId}] ({k + 1}/{amt})",
                            PrototypeOrQualityOrTag: tagId,
                            Amount: 1,
                            DoAfterDelay: 0f,
                            ExpectedNextNode: null));
                    }
                }
            }
        }
    }

    private MachineBoardRequirements? ExtractMachineBoardRequirements(
        EntityUid? target,
        string? desiredEntityProtoId,
        bool alreadyHasBoard)
    {
        if (_entMan == null || _protoMan == null)
            return null;

        EntityUid? boardUid = null;
        EntityPrototype? boardProto = null;

        if (target.HasValue &&
            _entMan.EntityExists(target.Value) &&
            _container != null &&
            _container.TryGetContainer(target.Value, "machine_board", out var boardContainer) &&
            boardContainer.ContainedEntities.Count > 0)
        {
            boardUid = boardContainer.ContainedEntities[0];
            boardProto = _entMan.GetComponent<MetaDataComponent>(boardUid.Value).EntityPrototype;
            if (boardProto != null)
                _lastKnownMachineBoardProtoId = boardProto.ID;
        }

        if (boardUid == null && boardProto == null)
        {
            if (!alreadyHasBoard)
            {
                TryFindAvailableMachineBoard(target, desiredEntityProtoId, out boardUid, out boardProto);
            }
            else
            {
                if (!string.IsNullOrEmpty(desiredEntityProtoId))
                    TryFindBoardPrototypeForMachine(desiredEntityProtoId, out boardProto);
                if (boardProto == null && !string.IsNullOrEmpty(_lastKnownMachineBoardProtoId))
                    _protoMan.TryIndex(_lastKnownMachineBoardProtoId, out boardProto);
                if (boardProto == null)
                    TryFindAvailableMachineBoard(target, desiredEntityProtoId, out boardUid, out boardProto);
            }
        }

        object? boardComp = null;
        if (boardUid.HasValue && _entMan.EntityExists(boardUid.Value))
            boardComp = FindComponentByName(boardUid.Value, "MachineBoardComponent");
        if (boardComp == null && boardProto != null && _itemUtil != null)
            _itemUtil.TryGetPrototypeComponent<object>(boardProto, "MachineBoard", out boardComp);

        var boardProtoId = boardProto?.ID ?? string.Empty;
        if (boardComp == null)
            return !string.IsNullOrEmpty(boardProtoId) ? new MachineBoardRequirements(boardProtoId) : null;

        var stacks = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        PopulateRequirementDict(boardComp, ["StackRequirements", "MaterialRequirements", "MaterialIdRequirements"], stacks);

        var rawParts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        PopulateRequirementDict(boardComp, ["Requirements", "MachinePartRequirements", "PartRequirements"], rawParts);

        var parts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var (partId, amount) in rawParts)
        {
            if (stacks.ContainsKey(partId))
                continue;

            if (_protoMan.HasIndex<StackPrototype>(partId))
                stacks[partId] = amount;
            else
                parts[partId] = amount;
        }

        var comps = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        PopulateRequirementDict(boardComp, ["ComponentRequirements"], comps);

        var tags = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        PopulateRequirementDict(boardComp, ["TagRequirements"], tags);

        return new MachineBoardRequirements(boardProtoId, stacks, parts, comps, tags);
    }

    private void PopulateRequirementDict(
        object boardComp,
        ReadOnlySpan<string> propNames,
        Dictionary<string, int> target)
    {
        foreach (var propName in propNames)
        {
            if (GetMemberValue(boardComp, propName) is not IDictionary reqs)
                continue;

            foreach (DictionaryEntry entry in reqs)
            {
                var id = ExtractIdString(entry.Key);
                var amount = entry.Value is int i ? i : GetIntMember(entry.Value, "Amount", 1);
                if (amount > 0 && !string.IsNullOrEmpty(id) && !target.ContainsKey(id))
                    target[id] = amount;
            }
        }
    }

    public bool IsMachineBoardEntity(EntityUid uid)
    {
        if (_entMan == null || !_entMan.EntityExists(uid))
            return false;

        if (FindComponentByName(uid, "MachineBoardComponent") != null)
            return true;

        var proto = _entMan.GetComponent<MetaDataComponent>(uid).EntityPrototype;
        if (proto == null)
            return false;

        if (_itemUtil != null && _itemUtil.HasPrototypeComponent(proto, "MachineBoard"))
            return true;

        if (proto.ID.Contains("MachineCircuitboard", StringComparison.OrdinalIgnoreCase))
            return true;

        if (proto.Parents != null &&
            proto.Parents.Any(p => p.Contains("MachineCircuitboard", StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        return false;
    }

    private bool TryFindAvailableMachineBoard(
        EntityUid? target,
        string? desiredMachineProtoId,
        out EntityUid? boardUid,
        out EntityPrototype? boardProto)
    {
        boardUid = null;
        boardProto = null;

        if (_itemUtil == null || _entMan == null)
            return false;

        var candidates = _itemUtil.FindItemsByPredicate(IsMachineBoardEntity);
        if (!string.IsNullOrEmpty(desiredMachineProtoId))
        {
            foreach (var loc in candidates)
            {
                var proto = _entMan.GetComponent<MetaDataComponent>(loc.Entity).EntityPrototype;
                object? boardComp = FindComponentByName(loc.Entity, "MachineBoardComponent");
                if (boardComp == null && proto != null)
                    _itemUtil.TryGetPrototypeComponent<object>(proto, "MachineBoard", out boardComp);

                var builtProto = GetStringMember(boardComp, "Prototype")
                                 ?? GetStringMember(boardComp, "MachinePrototype")
                                 ?? GetStringMember(boardComp, "Machine");
                if (string.Equals(builtProto, desiredMachineProtoId, StringComparison.OrdinalIgnoreCase))
                {
                    boardUid = loc.Entity;
                    boardProto = proto;
                    return true;
                }
            }
        }

        if (candidates.Count > 0)
        {
            boardUid = candidates[0].Entity;
            boardProto = _entMan.GetComponent<MetaDataComponent>(boardUid.Value).EntityPrototype;
            return true;
        }

        if (!string.IsNullOrEmpty(desiredMachineProtoId) &&
            TryFindBoardPrototypeForMachine(desiredMachineProtoId, out boardProto))
        {
            return true;
        }

        return false;
    }

    private bool TryFindBoardPrototypeForMachine(string machineProtoId, out EntityPrototype? boardProto)
    {
        boardProto = null;
        if (_protoMan == null || _itemUtil == null)
            return false;

        foreach (var eProto in _protoMan.EnumeratePrototypes<EntityPrototype>())
        {
            if (!_itemUtil.TryGetPrototypeComponent<object>(eProto, "MachineBoard", out var boardComp) || boardComp == null)
                continue;

            var builtProto = GetStringMember(boardComp, "Prototype")
                             ?? GetStringMember(boardComp, "MachinePrototype")
                             ?? GetStringMember(boardComp, "Machine");
            if (string.Equals(builtProto, machineProtoId, StringComparison.OrdinalIgnoreCase))
            {
                boardProto = eProto;
                return true;
            }
        }
        return false;
    }

    private bool HasPrototypeContainer(EntityPrototype proto, string containerId)
    {
        if (_itemUtil == null || !_itemUtil.TryGetPrototypeComponent<object>(proto, "ContainerContainer", out var ccObj) || ccObj == null)
            return false;

        if (GetMemberValue(ccObj, "Containers") is IDictionary dict)
        {
            foreach (DictionaryEntry entry in dict)
            {
                var key = ExtractIdString(entry.Key);
                if (string.Equals(key, containerId, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
        }

        return false;
    }
}
